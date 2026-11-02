<?php
declare(strict_types=1);
// Private LLM training-data API. No WordPress bootstrap, inference-key access,
// caller-supplied filename, shell command, model path or training hyperparameter.
ini_set('display_errors', '0');
header('Content-Type: application/json'); header('Cache-Control: no-store');
header('X-Content-Type-Options: nosniff');

function learning_normalize(string $value): string { return trim((string) preg_replace('/\s+/u', ' ', $value)); }
function learning_source_contains(string $source, string $value): bool {
    return $value !== '' && preg_match('/(?<![\p{L}\p{N}])' . preg_quote(learning_normalize($value), '/') . '(?![\p{L}\p{N}])/iu', learning_normalize($source)) === 1;
}
function learning_validate_example($input): array {
    $allowed = ['schemaVersion','clientId','subjectRef','sourceText','reviewedFields','verified','verificationKind','verificationRef'];
    if (!is_array($input) || array_diff(array_keys($input), $allowed) || ($input['schemaVersion'] ?? null) !== 1
        || !is_int($input['clientId'] ?? null) || $input['clientId'] < 0 || $input['clientId'] > 1000000000
        || ($input['verified'] ?? false) !== true
        || !in_array($input['verificationKind'] ?? '', ['recruiter_review','verified_regression'], true)
        || !is_string($input['verificationRef'] ?? null) || strlen($input['verificationRef']) < 3 || strlen($input['verificationRef']) > 160
        || !is_string($input['subjectRef'] ?? null) || preg_match('/\A[a-f0-9]{64}\z/D', $input['subjectRef']) !== 1
        || !is_string($input['sourceText'] ?? null) || strlen($input['sourceText']) < 60 || strlen($input['sourceText']) > 24000
        || !is_array($input['reviewedFields'] ?? null)) throw new InvalidArgumentException('Invalid verified example');
    $source = str_replace(["\r\n", "\r"], "\n", $input['sourceText']);
    // Contact and protected-trait lines are not learning material. The sender
    // must remove them before upload instead of silently changing label evidence.
    if (preg_match('/[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}/i', $source)
        || preg_match('/\b(?:date of birth|passport number|aadhaar|aadhar|religion|marital status)\s*[:\-]/i', $source))
        throw new InvalidArgumentException('Professional evidence must exclude contact/protected data');
    $fields = $input['reviewedFields'];
    if (array_diff(array_keys($fields), ['currentCompany','currentTitle','highestQualification','skills']) || count($fields) === 0)
        throw new InvalidArgumentException('Invalid learning field');
    $clean = [];
    foreach (['currentCompany','currentTitle','highestQualification'] as $field) {
        if (!array_key_exists($field, $fields)) continue;
        if (!is_string($fields[$field]) || strlen($fields[$field]) < 2 || strlen($fields[$field]) > 250
            || !learning_source_contains($source, $fields[$field]) || preg_match('/[\r\n<>@]/', $fields[$field]))
            throw new InvalidArgumentException('Unsupported scalar label');
        $clean[$field] = learning_normalize($fields[$field]);
    }
    if (isset($fields['skills'])) {
        if (!is_array($fields['skills']) || count($fields['skills']) < 1 || count($fields['skills']) > 100) throw new InvalidArgumentException('Invalid skills');
        $skills = [];
        foreach ($fields['skills'] as $skill) {
            if (!is_string($skill) || strlen($skill) < 2 || strlen($skill) > 100 || !learning_source_contains($source, $skill)
                || preg_match('/[\r\n<>@]/', $skill)) throw new InvalidArgumentException('Unsupported skill label');
            $skills[] = learning_normalize($skill);
        }
        $clean['skills'] = array_values(array_unique($skills));
    }
    return ['schemaVersion'=>1,'clientId'=>$input['clientId'],'subjectRef'=>$input['subjectRef'],
        'sourceText'=>$source,'reviewedFields'=>$clean,'verified'=>true,
        'verificationKind'=>$input['verificationKind'],'verificationRef'=>$input['verificationRef']];
}
function learning_encrypt(array $example, string $key): array {
    if (strlen($key) !== 32) throw new RuntimeException('Invalid data protection key');
    $sourceHash = hash('sha256', learning_normalize($example['sourceText']));
    $aad = 'EESL-LLM-learning:v1:' . $example['clientId'] . ':' . $sourceHash . ':' . $example['subjectRef'];
    $iv = random_bytes(12); $tag = '';
    $plaintext = json_encode($example, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
    $cipher = openssl_encrypt($plaintext, 'aes-256-gcm', $key, OPENSSL_RAW_DATA, $iv, $tag, $aad, 16);
    if ($cipher === false) throw new RuntimeException('Data protection unavailable');
    return ['version'=>1,'clientId'=>$example['clientId'],'subjectRef'=>$example['subjectRef'],'sourceHash'=>$sourceHash,
        'fingerprint'=>hash('sha256',json_encode([$example['sourceText'],$example['reviewedFields']],JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES)),
        'updatedUtc'=>gmdate('c'),'iv'=>base64_encode($iv),'tag'=>base64_encode($tag),'ciphertext'=>base64_encode($cipher)];
}
function learning_decrypt(array $envelope, string $key): array {
    $aad = 'EESL-LLM-learning:v1:' . $envelope['clientId'] . ':' . $envelope['sourceHash'] . ':' . $envelope['subjectRef'];
    $plain = openssl_decrypt(base64_decode($envelope['ciphertext'],true),'aes-256-gcm',$key,OPENSSL_RAW_DATA,
        base64_decode($envelope['iv'],true),base64_decode($envelope['tag'],true),$aad);
    if ($plain === false) throw new RuntimeException('Previous example integrity failed');
    return learning_validate_example(json_decode($plain,true,512,JSON_THROW_ON_ERROR));
}
function learning_atomic_json(string $path, array $value): void {
    $temporary = $path . '.' . bin2hex(random_bytes(8)) . '.tmp';
    try {
        $data = json_encode($value,JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
        if (file_put_contents($temporary,$data,LOCK_EX) !== strlen($data) || !rename($temporary,$path)) throw new RuntimeException('Atomic write failed');
    } finally { if (is_file($temporary)) unlink($temporary); }
}
function learning_reply(int $status, array $value): void { http_response_code($status); echo json_encode($value); exit; }
function learning_reconcile_revision(string $root, string $revisionFile, array $revision): array {
    $dirty = $root . '/state/dataset-dirty.json';
    if (!is_file($dirty)) return $revision;
    $revision['count'] = count(glob($root . '/datasets/*/*.enc') ?: []);
    $revision['revision']++; $revision['updatedUtc'] = gmdate('c');
    learning_atomic_json($revisionFile,$revision);
    if (!unlink($dirty)) throw new RuntimeException('Dataset reconciliation incomplete');
    return $revision;
}
if (defined('LLM_LEARNING_LIBRARY_ONLY')) return;
try {
    $key = trim((string) @file_get_contents('E:/LLM/secrets/learning-api-key.txt'));
    $auth = $_SERVER['HTTP_AUTHORIZATION'] ?? $_SERVER['REDIRECT_HTTP_AUTHORIZATION'] ?? '';
    if (preg_match('/\A[a-f0-9]{64}\z/D',$key) !== 1 || !hash_equals('Bearer ' . $key,$auth)) learning_reply(401,['code'=>'unauthorized']);
    $root = 'E:/LLM/training';
    if (!is_dir($root . '/datasets') || !is_dir($root . '/state')) learning_reply(503,['code'=>'learning_not_prepared']);
    $method = $_SERVER['REQUEST_METHOD'] ?? '';
    $revisionFile = $root . '/state/dataset-revision.json';
    if ($method === 'GET') {
        $lock = fopen($root . '/state/dataset.lock','c+');
        if (!$lock || !flock($lock,LOCK_EX | LOCK_NB)) learning_reply(429,['code'=>'busy']);
        try {
        $revision = json_decode((string) @file_get_contents($revisionFile),true);
        if (!is_array($revision)) {
            if (is_file($revisionFile)) throw new RuntimeException('Revision state invalid');
            $revision = ['revision'=>0,'count'=>0];
        }
        $revision = learning_reconcile_revision($root,$revisionFile,$revision);
        } finally { flock($lock,LOCK_UN); fclose($lock); }
        $setup = json_decode((string) @file_get_contents('E:/LLM/logs/training-setup.json'),true);
        $training = json_decode((string) @file_get_contents($root . '/state/training-status.json'),true);
        $serving = json_decode((string) @file_get_contents('E:/LLM/logs/host-status.json'),true);
        $activeAdapter = $serving['ActiveAdapter'] ?? null;
        if (!is_string($activeAdapter) || preg_match('/\A[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}\z/D',$activeAdapter) !== 1) $activeAdapter = null;
        learning_reply(200,['code'=>'ready','datasetRevision'=>$revision['revision'] ?? 0,'exampleCount'=>$revision['count'] ?? 0,
            'setupState'=>$setup['State'] ?? 'Unknown','setupStep'=>$setup['Step'] ?? '',
            'trainingState'=>$training['state'] ?? 'WaitingForExamples','trainingStep'=>$training['step'] ?? '',
            'jobId'=>$training['jobId'] ?? null,'activeAdapter'=>$activeAdapter,
            'servingState'=>$serving['Status'] ?? 'Unknown']);
    }
    if ($method !== 'POST') learning_reply(405,['code'=>'method_not_allowed']);
    if (stripos($_SERVER['CONTENT_TYPE'] ?? '', 'application/json') !== 0) learning_reply(415,['code'=>'invalid_request']);
    if ((int) ($_SERVER['CONTENT_LENGTH'] ?? 0) > 1048576) learning_reply(413,['code'=>'request_too_large']);
    $raw = file_get_contents('php://input',false,null,0,1048577);
    if ($raw === false || strlen($raw) > 1048576) learning_reply(413,['code'=>'request_too_large']);
    $body = json_decode($raw,true);
    if (!is_array($body) || !is_string($body['action'] ?? null)) learning_reply(400,['code'=>'invalid_request']);
    $action = $body['action']; $examples = [];
    if ($action === 'ingest') {
        if (array_diff(array_keys($body),['action','examples']) || !is_array($body['examples'] ?? null) || count($body['examples']) < 1 || count($body['examples']) > 25
            || array_keys($body['examples']) !== range(0,count($body['examples'])-1))
            learning_reply(400,['code'=>'invalid_request']);
        foreach ($body['examples'] as $input) $examples[] = learning_validate_example($input);
    } elseif ($action === 'train') {
        if (count($body) !== 1) learning_reply(400,['code'=>'invalid_request']);
    } elseif ($action === 'purge_subject') {
        if (array_diff(array_keys($body),['action','clientId','subjectRef']) || !is_int($body['clientId'] ?? null) || $body['clientId'] < -1
            || !is_string($body['subjectRef'] ?? null) || preg_match('/\A[a-f0-9]{64}\z/D',$body['subjectRef']) !== 1)
            learning_reply(400,['code'=>'invalid_request']);
    } else learning_reply(400,['code'=>'unsupported_action']);
    $lock = fopen($root . '/state/dataset.lock','c+');
    if (!$lock || !flock($lock,LOCK_EX | LOCK_NB)) learning_reply(429,['code'=>'busy']);
    $result = null;
    try {
        $revision = is_file($revisionFile) ? json_decode((string) file_get_contents($revisionFile),true) : ['revision'=>0,'count'=>0];
        if (!is_array($revision) || !is_int($revision['revision'] ?? null) || !is_int($revision['count'] ?? null)) throw new RuntimeException('Revision state invalid');
        $revision = learning_reconcile_revision($root,$revisionFile,$revision);
        $changed = 0; $accepted = 0;
        if ($action === 'ingest') {
            $dataKeyText = trim((string) @file_get_contents('E:/LLM/secrets/learning-data-key.txt'));
            if (preg_match('/\A[a-f0-9]{64}\z/D',$dataKeyText) !== 1) throw new RuntimeException('Data key unavailable');
            $dataKey = hex2bin($dataKeyText); $dataKeyText = '';
            $revocations = json_decode((string) @file_get_contents($root . '/state/revocations.json'),true);
            if (is_file($root . '/state/revocations.json') && !is_array($revocations)) throw new RuntimeException('Revocation state invalid');
            foreach ($examples as $example) {
                if (isset($revocations[$example['subjectRef']])) throw new InvalidArgumentException('Subject learning has been revoked');
                $envelope = learning_encrypt($example,$dataKey);
                $directory = $root . '/datasets/' . $example['clientId'];
                if (!is_dir($directory) && !mkdir($directory,0700)) throw new RuntimeException('Client dataset unavailable');
                $path = $directory . '/' . $envelope['sourceHash'] . '-' . $example['subjectRef'] . '.enc';
                $legacy = $directory . '/' . $envelope['sourceHash'] . '.enc';
                if (!is_file($path) && is_file($legacy)) {
                    $old = json_decode((string) file_get_contents($legacy),true);
                    if (is_array($old) && ($old['subjectRef'] ?? '') === $example['subjectRef']) {
                        if (!rename($legacy,$path)) throw new RuntimeException('Example identity migration failed');
                    }
                }
                $existing = is_file($path) ? json_decode((string) file_get_contents($path),true) : null;
                if (is_array($existing)) {
                    $previous = learning_decrypt($existing,$dataKey);
                    $example['reviewedFields'] = array_merge($previous['reviewedFields'],$example['reviewedFields']);
                    $envelope = learning_encrypt($example,$dataKey);
                }
                if (is_array($existing) && ($existing['fingerprint'] ?? '') === $envelope['fingerprint']) { $accepted++; continue; }
                if (!is_file($path) && $revision['count'] >= 10000) throw new RuntimeException('Dataset capacity reached');
                learning_atomic_json($root . '/state/dataset-dirty.json',['startedUtc'=>gmdate('c')]);
                if (!is_file($path)) $revision['count']++;
                learning_atomic_json($path,$envelope); $accepted++; $changed++;
            }
            $dataKey = '';
        } elseif ($action === 'purge_subject') {
            learning_atomic_json($root . '/state/dataset-dirty.json',['startedUtc'=>gmdate('c')]);
            // Persist revocation first. A partial request/retry must never make a
            // previously trained subject eligible after its example was removed.
            $revocationsFile = $root . '/state/revocations.json';
            $revocations = json_decode((string) @file_get_contents($revocationsFile),true);
            if (!is_array($revocations)) $revocations = [];
            $revocations[$body['subjectRef']] = gmdate('c');
            if (count($revocations) > 10000) throw new RuntimeException('Revocation capacity reached');
            learning_atomic_json($revocationsFile,$revocations);
            $directories = $body['clientId'] === -1 ? (glob($root . '/datasets/*',GLOB_ONLYDIR) ?: []) : [$root . '/datasets/' . $body['clientId']];
            foreach ($directories as $directory) {
                foreach (glob($directory . '/*.enc') ?: [] as $path) {
                    $envelope = json_decode((string) file_get_contents($path),true);
                    if (is_array($envelope) && ($envelope['subjectRef'] ?? '') === $body['subjectRef']) {
                        if (!unlink($path)) throw new RuntimeException('Example removal failed');
                        $revision['count']--; $changed++;
                    }
                }
            }
        }
        $revision = learning_reconcile_revision($root,$revisionFile,$revision);
        if ($action === 'train') learning_atomic_json($root . '/state/train-request.json',['requestedUtc'=>gmdate('c'),'revision'=>$revision['revision']]);
        $result = ['code'=>$action === 'train' ? 'queued' : 'accepted','accepted'=>$accepted,'changed'=>$changed,
            'datasetRevision'=>$revision['revision'],'exampleCount'=>$revision['count']];
    } finally {
        try {
            if (isset($revision) && is_array($revision)) learning_reconcile_revision($root,$revisionFile,$revision);
        } finally { flock($lock,LOCK_UN); fclose($lock); }
    }
    learning_reply(200,$result);
} catch (InvalidArgumentException $error) { learning_reply(400,['code'=>'invalid_verified_example']); }
catch (Throwable $error) { learning_reply(503,['code'=>'learning_unavailable']); }
