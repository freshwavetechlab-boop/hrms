<?php
declare(strict_types=1);
// LLM-only maintenance. This never loads WordPress or accepts shell/path input.
ini_set('display_errors', '0');
header('Content-Type: application/json');
header('Cache-Control: no-store');
header('X-Content-Type-Options: nosniff');
function maintenance_reply(int $status, string $code, string $id = ''): void {
    http_response_code($status); echo json_encode(['code' => $code, 'requestId' => $id]); exit;
}
function maintenance_request_valid($body): bool {
    return is_array($body) && count($body) === 2 && isset($body['action'], $body['requestId'])
        && in_array($body['action'], ['install', 'status', 'prepare_training','probe_training_network'], true) && is_string($body['requestId'])
        && preg_match('/\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z/D', $body['requestId']) === 1;
}
if (defined('LLM_MAINTENANCE_LIBRARY_ONLY')) return;
try {
    if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'POST') maintenance_reply(405, 'method_not_allowed');
    $key = trim((string) @file_get_contents('E:/LLM/secrets/control-api-key.txt'));
    $auth = $_SERVER['HTTP_AUTHORIZATION'] ?? $_SERVER['REDIRECT_HTTP_AUTHORIZATION'] ?? '';
    if (!preg_match('/\A[0-9a-f]{64}\z/D', $key) || !hash_equals('Bearer ' . $key, $auth)) maintenance_reply(401, 'unauthorized');
    if (stripos($_SERVER['CONTENT_TYPE'] ?? '', 'application/json') !== 0) maintenance_reply(415, 'invalid_request');
    if ((int) ($_SERVER['CONTENT_LENGTH'] ?? 0) > 512) maintenance_reply(413, 'invalid_request');
    $raw = file_get_contents('php://input', false, null, 0, 513);
    if ($raw === false || strlen($raw) > 512) maintenance_reply(413, 'invalid_request');
    $body = json_decode($raw, true);
    if (!maintenance_request_valid($body)) maintenance_reply(400, 'invalid_request');
    if ($body['action'] === 'status') {
        $raw = @file_get_contents('E:/LLM/logs/deployment-status.json');
        $state = $raw === false ? null : json_decode($raw, true);
        maintenance_reply(200, is_array($state) ? ($state['Code'] ?? 'not_installed') : 'not_installed', $body['requestId']);
    }
    $training = $body['action'] === 'prepare_training';
    $script = $training ? 'E:/LLM/maintenance/Initialize-LLMTraining.ps1' : 'E:/LLM/maintenance/Invoke-LLMDeployment.ps1';
    $expected = $training ? '__TRAINING_SETUP_SHA256__' : '__INSTALLER_SHA256__';
    if ($body['action'] === 'probe_training_network') {
        $script = 'E:/LLM/maintenance/Probe-LLMTrainingNetwork.ps1'; $expected = '__NETWORK_PROBE_SHA256__';
    }
    if (!hash_equals($expected, strtoupper((string) @hash_file('sha256', $script)))) maintenance_reply(503, 'package_invalid', $body['requestId']);
    $command = ['C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe', '-NoProfile', '-NonInteractive', '-File', $script, '-Action', 'Dispatch'];
    $process = proc_open($command, [0=>['file','NUL','r'],1=>['pipe','w'],2=>['file','NUL','w']], $pipes, 'E:/LLM', null, ['bypass_shell'=>true,'suppress_errors'=>true]);
    if (!is_resource($process)) maintenance_reply(503, 'deployment_unavailable', $body['requestId']);
    $deadline = microtime(true) + 20;
    do { $state = proc_get_status($process); if (!$state['running']) break; usleep(50000); } while (microtime(true) < $deadline);
    if ($state['running']) { proc_terminate($process); fclose($pipes[1]); proc_close($process); maintenance_reply(503, 'outcome_unknown', $body['requestId']); }
    $output = stream_get_contents($pipes[1], 4097); fclose($pipes[1]); proc_close($process);
    $result = $output !== false && strlen($output) <= 4096 ? json_decode($output, true) : null;
    $code = is_array($result) && in_array($result['code'] ?? '', ['installing','already_installed','preparing_training','training_ready','training_network_probed'], true) ? $result['code'] : 'deployment_failed';
    maintenance_reply(in_array($code,['installing','preparing_training'],true) ? 202 : (in_array($code,['already_installed','training_ready','training_network_probed'],true) ? 200 : 503), $code, $body['requestId']);
} catch (Throwable $error) { maintenance_reply(503, 'deployment_unavailable'); }
