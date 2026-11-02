<?php
define('LLM_LEARNING_LIBRARY_ONLY',true);
require __DIR__ . '/../learning.php';
$sample = ['schemaVersion'=>1,'clientId'=>20,'subjectRef'=>str_repeat('a',64),'verified'=>true,
    'verificationKind'=>'verified_regression','verificationRef'=>'synthetic-role-date-regression',
    'sourceText'=>"SKILLS\nSQL\nDocker\nEXPERIENCE\nApril 2021 - Current\nChief Technology Officer\nExample Labs\nEDUCATION\nMBA\nExample University, 2010",
    'reviewedFields'=>['currentCompany'=>'Example Labs','currentTitle'=>'Chief Technology Officer','highestQualification'=>'MBA','skills'=>['SQL','Docker']]];
$valid = learning_validate_example($sample);
if ($valid['reviewedFields']['currentCompany'] !== 'Example Labs') throw new RuntimeException('Valid fields lost');
$invalid = [];
$row=$sample;$row['verified']=false;$invalid[]=$row;
$row=$sample;$row['reviewedFields']['currentCompany']='Invented Company';$invalid[]=$row;
$row=$sample;$row['reviewedFields']['salary']=100;$invalid[]=$row;
$row=$sample;$row['sourceText'].="\nprivate@example.test";$invalid[]=$row;
$row=$sample;$row['subjectRef']='../../other';$invalid[]=$row;
$row=$sample;$row['clientId']='20';$invalid[]=$row;
$row=$sample;$row['path']='C:/other';$invalid[]=$row;
foreach ($invalid as $input) {
    $refused=false; try { learning_validate_example($input); } catch (InvalidArgumentException $error) { $refused=true; }
    if (!$refused) throw new RuntimeException('Unverified/unsupported/out-of-scope input accepted');
}
if (function_exists('openssl_encrypt')) {
    $key=random_bytes(32); $envelope=learning_encrypt($sample,$key);
    $aad='EESL-LLM-learning:v1:20:'.$envelope['sourceHash'].':'.$sample['subjectRef'];
    $clear=openssl_decrypt(base64_decode($envelope['ciphertext']),'aes-256-gcm',$key,OPENSSL_RAW_DATA,base64_decode($envelope['iv']),base64_decode($envelope['tag']),$aad);
    if ($clear===false || json_decode($clear,true)['reviewedFields']['currentCompany']!=='Example Labs') throw new RuntimeException('Encrypted round-trip failed');
    if (learning_decrypt($envelope,$key)['subjectRef']!==$sample['subjectRef']) throw new RuntimeException('Validated envelope round-trip failed');
    $swapped=$envelope;$swapped['clientId']=21;$refused=false;
    try { learning_decrypt($swapped,$key); } catch (RuntimeException $error) { $refused=true; }
    if (!$refused) throw new RuntimeException('Cross-client envelope substitution accepted');
    if (strpos(json_encode($envelope),'Example Labs')!==false) throw new RuntimeException('Plaintext dataset leaked');
    if (openssl_decrypt(base64_decode($envelope['ciphertext']),'aes-256-gcm',$key,OPENSSL_RAW_DATA,base64_decode($envelope['iv']),base64_decode($envelope['tag']),$aad.'wrong')!==false) throw new RuntimeException('AAD substitution accepted');
}
echo "PASS: verified/source-grounded professional fields, tenant/id/path constraints, privacy filtering and available AES-GCM checks.\n";
