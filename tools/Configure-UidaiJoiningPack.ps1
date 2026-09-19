param(
    [Parameter(Mandatory=$true)][string]$ApiBase,
    [Parameter(Mandatory=$true)][int]$ClientId,
    [pscredential]$Credential,
    [switch]$ConfigureDocuments,
    [switch]$InstallOfferTemplate,
    [int]$TemplateId
)
$ErrorActionPreference = 'Stop'
$documents = @(
    @('DOB', 'Proof of Date of Birth (Birth Certificate / SSC)'),
    @('PAN', 'PAN Card'), @('AADHAAR', 'Aadhaar Card'),
    @('PHOTO', 'Passport size colour photographs (soft copy)'),
    @('ADDRESS', 'Address / ID Proof (Passport / Voter ID / Driving License)'),
    @('ACADEMIC', 'Academic certificates (Class 10 onwards)'),
    @('PREVIOUS_APPOINTMENT', 'Appointment letter from previous employer'),
    @('RELIEVING', 'Resignation / Relieving letter from previous employer'),
    @('SALARY', 'Salary slips / Bank statement for last 3 months'),
    @('REFERENCES', 'Two professional references (preferably previous employers)'),
    @('BANK', 'Bank passbook with IFSC or Cancelled Cheque'), @('UAN', 'Copy of UAN Card')
)
if (!$ConfigureDocuments -and !$InstallOfferTemplate) {
    Write-Output 'Preview only. Creates 12 client-scoped upload/checklist fields (PAN and Aadhaar separate), and one versioned joining form. No schema changes.'
    $documents | ForEach-Object { Write-Output $_[1] }
    Write-Output 'Use -ConfigureDocuments to apply. Only after deploying the branded renderer: -InstallOfferTemplate -TemplateId <existing UIDAI offer template id>.'
    return
}
if (!$Credential) { $Credential = Get-Credential -Message 'Global super-admin login (not saved)' }
$api = $ApiBase.TrimEnd('/')
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
function CallApi([string]$Path, $Body = $null) {
    if ($null -eq $Body) { $result = Invoke-RestMethod "$api$Path" -WebSession $session; return $result }
    $json = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 30))
    $result = Invoke-RestMethod "$api$Path" -Method Post -ContentType 'application/json; charset=utf-8' -Body $json -WebSession $session
    return $result
}
$login = CallApi '/api/auth/login' @{email=$Credential.UserName; password=$Credential.GetNetworkCredential().Password}
$me = CallApi '/api/auth/me'
if ($null -ne $me.clientId -or $me.roles -notcontains 'super_admin') { throw 'A global super_admin account is required.' }
$clients = @(CallApi '/api/clients')
$client = $clients | Where-Object id -eq $ClientId
if (!$client -or $client.name -notmatch 'Unique Identification|UIDAI') { throw 'The selected client is not UIDAI; nothing changed.' }
$setup = CallApi '/api/recruitment-admin'
if ($ConfigureDocuments) {
    $attributes = @(CallApi "/api/attachment-attributes?clientId=$ClientId")
    $configs = @(CallApi "/api/attachment-configurations?clientId=$ClientId")
    $fields = @()
    $order = 0
    foreach ($doc in $documents) {
        $order += 10
        $code = 'UIDAI_JOIN_' + $doc[0]
        $attribute = $attributes | Where-Object { $_.clientId -eq $ClientId -and $_.attributeCode -eq $code } | Select-Object -First 1
        if (!$attribute) { $attribute = CallApi '/api/attachment-attributes' @{clientId=$ClientId; attributeCode=$code; attributeName=$doc[1]; dataClassification='Confidential'; isActive=$true} }
        $field = $configs | Where-Object { $_.clientId -eq $ClientId -and $_.formCode -eq 'UIDAI_JOINING_DOCUMENTS' -and $_.fieldKey -eq $code } | Select-Object -First 1
        if (!$field) { $field = CallApi '/api/attachment-configurations' @{
            clientId=$ClientId; attachmentAttributeId=$attribute.id; moduleCode='RECRUITMENT'; formCode='UIDAI_JOINING_DOCUMENTS'; sectionCode='DOCUMENTS'; fieldKey=$code; fieldLabel=$doc[1]
            helpText='Upload self-attested scanned copies. Produce originals at joining.'; isRequired=$true; allowMultiple=$true; minimumFileCount=1; maximumFileCount=10
            maximumFileSizeBytes=10485760; maximumTotalSizeBytes=52428800; ownerCanView=$true; ownerCanUpload=$true; ownerCanReplace=$true; requiresVerification=$true; displayOrder=$order; isActive=$true
        } }
        $existing = $setup.documentChecklist | Where-Object { $_.clientId -eq $ClientId -and $_.documentName -eq $doc[1] -and $_.stage -eq 'Pre-Onboarding' } | Select-Object -First 1
        if (!$existing) { $null = CallApi '/api/recruitment-admin/document-checklist' @{
            clientId=$ClientId; documentName=$doc[1]; stage='Pre-Onboarding'; mandatory=$true; attachmentAttributeId=$attribute.id; requiresVerification=$true; displayOrder=$order; isActive=$true
        } } elseif (!$existing.attachmentAttributeId -or !$existing.requiresVerification) {
            throw "Existing checklist '$($doc[1])' differs. Review it rather than silently replacing it."
        }
        $fields += @{ stableFieldCode=$code; label=$doc[1]; fieldTypeCode='UPLOAD'; isRequired=$true; widthColumns=12; displayOrder=$order; attachmentFieldConfigurationId=$field.id; helpText='Self-attested copy; original verification at joining.'; isActive=$true }
    }
    $forms = @(CallApi "/api/recruitment-orchestration/forms?clientId=$ClientId")
    $form = $forms | Where-Object { $_.clientId -eq $ClientId -and $_.formCode -eq 'UIDAI_JOINING_DOCUMENTS' } | Select-Object -First 1
    if (!$form) { $form = CallApi '/api/recruitment-orchestration/forms' @{clientId=$ClientId; formCode='UIDAI_JOINING_DOCUMENTS'; formName='UIDAI Joining Documents'; purposeCode='DOCUMENT_REQUEST'; requiresEmailVerification=$true} }
    if (!$form.currentPublishedVersionId) {
        $version = CallApi "/api/recruitment-orchestration/forms/$($form.id)/versions" @{formDefinitionId=$form.id; sections=@(@{sectionCode='JOINING_DOCUMENTS'; sectionLabel='Documents required at joining'; description='Submit self-attested scanned copies and produce originals at joining.'; displayOrder=10; fields=$fields})}
        $null = CallApi "/api/recruitment-orchestration/form-versions/$($version.id)/publish" @{}
    }
    Write-Output 'UIDAI joining checklist and upload form configured. Existing issued links remain unchanged; newly generated document-stage links use this form after API deployment.'
}
if ($InstallOfferTemplate) {
    if ($TemplateId -le 0) { throw 'Specify the existing UIDAI Offer Letter TemplateId.' }
    # Capability probe prevents enabling a rich template against an old plain-text renderer.
    $capabilities = CallApi '/api/recruitment/offers/template-capabilities'
    if ($capabilities.brandedUidaiOffer -ne $true) { throw 'Deploy the new API before activating the template.' }
    $template = $setup.templates | Where-Object { $_.id -eq $TemplateId -and $_.clientId -eq $ClientId -and $_.templateType -eq 'Offer Letter' }
    if (!$template) { throw 'An exact client-scoped Offer Letter template was not found.' }
    $bodyPath = Join-Path $PSScriptRoot '../Payroll.API/Assets/OfferLetters/uidai-offer.txt'
    $template.bodyTemplate = Get-Content -Raw -Encoding UTF8 -LiteralPath $bodyPath
    $template.isHtml = $false
    $template.subjectTemplate = 'Offer Letter for {{positionTitle}}'
    $null = CallApi '/api/recruitment-admin/templates' $template
    Write-Output 'Offer template updated. Past issued/accepted PDFs are unchanged. Signed release requires the configured final signatory and private signing file.'
}
