<?php
define('LLM_MAINTENANCE_LIBRARY_ONLY', true);
require __DIR__ . '/../maintenance.php';
$id = '11111111-2222-3333-4444-555555555555';
foreach (['install','status','prepare_training'] as $action) {
    if (!maintenance_request_valid(['action'=>$action,'requestId'=>$id])) throw new RuntimeException('Valid action rejected');
}
foreach ([['action'=>'exec','requestId'=>$id], ['action'=>'install','requestId'=>$id,'command'=>'whoami'],
    ['action'=>'install','requestId'=>'not-a-guid'], ['action'=>'install','requestId'=>[]],
    ['action'=>['install'],'requestId'=>$id], ['action'=>'install','requestId'=>$id,'path'=>'C:/other']] as $request) {
    if (maintenance_request_valid($request)) throw new RuntimeException('Invalid request accepted');
}
echo "PASS: fixed maintenance operations, no command/path input, bounded request contract.\n";
