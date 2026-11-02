<?php
declare(strict_types=1);
ini_set('display_errors', '0');
header('Content-Type: application/json');
header('Cache-Control: no-store');
header('X-Content-Type-Options: nosniff');

function reply(int $status, string $code, string $requestId = ''): void {
    http_response_code($status);
    echo json_encode(['code' => $code, 'requestId' => $requestId]);
    exit;
}

// Strictly two operations; no task names, commands, URLs or paths supplied by a caller.
function valid_request($body): bool {
    return is_array($body) && count($body) === 2
        && isset($body['action'], $body['requestId'])
        && in_array($body['action'], ['status', 'start'], true)
        && is_string($body['requestId'])
        && preg_match('/\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z/D', $body['requestId']) === 1;
}

function run_control(string $action): string {
    if (!in_array($action, ['status', 'start'], true)) throw new RuntimeException('Invalid action');
    // PHP 7.4 command array + bypass_shell: never interpolated into cmd.exe.
    $command = ['C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe',
        '-NoProfile', '-NonInteractive', '-File', 'E:/LLM/scripts/Invoke-LLMControl.ps1',
        '-Action', $action === 'start' ? 'Start' : 'Status'];
    $process = proc_open($command, [0 => ['file', 'NUL', 'r'], 1 => ['pipe', 'w'], 2 => ['file', 'NUL', 'w']],
        $pipes, 'E:/LLM', null, ['bypass_shell' => true, 'suppress_errors' => true]);
    if (!is_resource($process)) return 'control_unavailable';
    // Windows anonymous pipes are not reliably nonblocking. PeekNamedPipe via proc_get_status
    // is unnecessary: the script emits only a bounded final JSON line, read after exit.
    $deadline = microtime(true) + 20;
    do {
        $state = proc_get_status($process);
        if (!$state['running']) break;
        usleep(50000);
    } while (microtime(true) < $deadline);
    if ($state['running']) {
        proc_terminate($process); // Only this short-lived control process, never the LLM task.
        fclose($pipes[1]); proc_close($process);
        return 'control_unavailable'; // Task Run may already have succeeded; never retry automatically.
    }
    $raw = stream_get_contents($pipes[1], 4097);
    fclose($pipes[1]); proc_close($process);
    if ($raw === false || strlen($raw) > 4096) return 'control_unavailable';
    $result = json_decode($raw, true);
    $code = is_array($result) ? ($result['code'] ?? '') : '';
    return in_array($code, ['stopped', 'starting', 'running', 'task_disabled', 'cpu_busy',
        'memory_low', 'website_unhealthy', 'recovering', 'safety_wait', 'identity_blocked', 'control_unavailable'], true) ? $code : 'control_unavailable';
}

function serve_control(): void {
    if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'POST') reply(405, 'method_not_allowed');
    // Separate from both inference credentials. Nothing echoes any key or raw exception.
    $key = trim((string) @file_get_contents('E:/LLM/secrets/control-api-key.txt'));
    $authorization = $_SERVER['HTTP_AUTHORIZATION'] ?? $_SERVER['REDIRECT_HTTP_AUTHORIZATION'] ?? '';
    if (preg_match('/\A[0-9a-f]{64}\z/D', $key) !== 1 || !hash_equals('Bearer ' . $key, $authorization)) reply(401, 'unauthorized');
    if (trim((string) @file_get_contents('E:/LLM/api/control-enabled')) !== 'enabled') reply(503, 'control_unavailable');
    if (stripos($_SERVER['CONTENT_TYPE'] ?? '', 'application/json') !== 0) reply(415, 'invalid_request');
    if ((int) ($_SERVER['CONTENT_LENGTH'] ?? 0) > 512) reply(413, 'invalid_request');
    $raw = file_get_contents('php://input', false, null, 0, 513);
    if ($raw === false || strlen($raw) > 512) reply(413, 'invalid_request');
    $body = json_decode($raw, true);
    if (!valid_request($body)) reply(400, 'invalid_request');
    $id = $body['requestId'];
    $lock = @fopen('E:/LLM/logs/control-state.json', 'c+');
    if (!$lock) reply(503, 'control_unavailable', $id);
    if (!flock($lock, LOCK_EX | LOCK_NB)) { fclose($lock); reply(200, 'busy', $id); }
    try {
        $saved = stream_get_contents($lock, 4097);
        $state = $saved === '' ? ['lastStart' => 0, 'lastStatus' => 0, 'requestId' => '', 'code' => 'control_unavailable'] : json_decode($saved, true);
        if (!is_array($state) || !isset($state['lastStart'], $state['lastStatus'], $state['requestId'], $state['code'])) throw new RuntimeException('State invalid');
        $now = time();
        if ($body['action'] === 'start' && $state['requestId'] === $id) {
            $code = $state['code'];
        } elseif ($body['action'] === 'start' && $now - $state['lastStart'] < 300) {
            $code = 'cooldown';
        } elseif ($body['action'] === 'status' && $now - $state['lastStatus'] < 2) {
            $code = 'busy';
        } else {
            if ($body['action'] === 'start') {
                $state['lastStart'] = $now;
                $state['requestId'] = $id;
                $state['code'] = 'control_unavailable';
            } else $state['lastStatus'] = $now;
            // Persist BEFORE Task.Run: a crashed/unknown request must not cause restart loops.
            save_state($lock, $state);
            $code = run_control($body['action']);
            if ($body['action'] === 'start') { $state['code'] = $code; save_state($lock, $state); }
            // Constant metadata only; rotates at 1 MiB, no headers/prompts/secrets.
            $log = 'E:/LLM/logs/control-audit.jsonl';
            if (is_file($log) && filesize($log) > 1048576) {
                if (is_file($log . '.1')) unlink($log . '.1');
                rename($log, $log . '.1');
            }
            file_put_contents($log, json_encode(['utc' => gmdate('c'), 'requestId' => $id,
                'action' => $body['action'], 'code' => $code]) . "\n", FILE_APPEND | LOCK_EX);
        }
    } finally { flock($lock, LOCK_UN); fclose($lock); }
    reply(200, $code, $id);
}

function save_state($file, array $state): void {
    $data = json_encode($state);
    rewind($file);
    if (!ftruncate($file, 0) || fwrite($file, $data) !== strlen($data) || !fflush($file)) throw new RuntimeException('State write failed');
}

try { serve_control(); } catch (Throwable $error) { reply(503, 'control_unavailable'); }
