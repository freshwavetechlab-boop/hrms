// Real API + disposable loopback database. Browser is visible unless explicitly headless.
import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'
import net from 'node:net'
import { spawn } from 'node:child_process'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import assert from 'node:assert/strict'
import { runRecruitmentPortalChecks } from './test-recruitment-workflow-steps.mjs'
import { startRecruitmentMailSink } from './recruitment-loopback-smtp.mjs'
import { seedHiringFlow } from './recruitment-hiring-flow-fixture.mjs'
const root = fileURLToPath(new URL('../', import.meta.url))
const require = createRequire(path.join(root, 'playwright-e2e/package.json'))
const mysql = require('mysql2/promise')
const raw = JSON.parse(fs.readFileSync(path.join(root, 'Payroll.API/appsettings.Development.json'), 'utf8')).ConnectionStrings.Default
const parts = Object.fromEntries(raw.split(';').filter(p => p.includes('=')).map(p => { const i = p.indexOf('='); return [p.slice(0, i).trim().toLowerCase(), p.slice(i + 1).trim()] }))
assert(['localhost', '127.0.0.1', '::1'].includes(parts.server || parts.host), 'Loopback DB only')
const name = 'hrms_workflow_portal_' + crypto.randomBytes(12).toString('hex')
const output = path.join(root, '.codex-validation/recruitment-workflows/portal')
fs.mkdirSync(output, { recursive: true })
const startedAt = new Date().toISOString()
fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ status: 'Running', startedAt }))
const owner = await mysql.createConnection({ host: parts.server || parts.host, port: Number(parts.port || 3306), user: parts.user || parts['user id'] || parts.uid, password: parts.password || parts.pwd, timezone: '+00:00', multipleStatements: true })
const connection = raw.replace(/(?:Database|Initial Catalog)=[^;]*/i, `Database=${name}`)
assert(connection.includes(`Database=${name}`))
const ui = process.env.HRMS_TEST_UI_URL || 'http://localhost:5184'
assert(['localhost', '127.0.0.1'].includes(new URL(ui).hostname))
assert((await fetch(ui)).ok, 'Start the isolated Vite preview before provisioning the test database')
const free = net.createServer(); await new Promise(r => free.listen(0, '127.0.0.1', r)); const port = free.address().port; await new Promise(r => free.close(r))
const apiUrl = `http://127.0.0.1:${port}`
const mail = await startRecruitmentMailSink()
const dll = path.join(root, 'Payroll.API/bin/Debug/net8.0/Payroll.API.dll')
const environment = { ...process.env, ConnectionStrings__Default: connection, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
  ASPNETCORE_URLS: apiUrl, BackgroundWorkers__Enabled: 'true', Database__AutoMigrate: 'false', OutboundDelivery__Suppressed: 'false',
  EngineHistory__Enabled: 'false', EngineActivity__Enabled: 'false', InternalInterviews__Enabled: 'false', FrevoPilot__Enabled: 'false',
  OfferSigning__Uidai__PostAcceptanceEnabled: 'false', OfferSigning__Uidai__ClientId: '0', OfferSigning__Uidai__FinalApproverUserId: '0',
  AttachmentStorage__DataRootPath: output, AttachmentStorage__RootPath: path.join(output, 'attachments'), Logging__LogLevel__Default: 'Warning' }
let api, serverLog = '', checksResult
function start(args) {
  const child = spawn('dotnet', [dll, ...args], { cwd: root, env: environment, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })
  child.stdout.on('data', d => { serverLog = (serverLog + d).slice(-50000) }); child.stderr.on('data', d => { serverLog = (serverLog + d).slice(-50000) })
  return child
}
try {
  await owner.query(`CREATE DATABASE \`${name}\` CHARACTER SET utf8mb4`)
  await owner.query(`USE \`${name}\``)
  console.log('Preparing isolated schema; production connections are prohibited.')
  const migration = start(['--migrate'])
  const exit = await new Promise((resolve, reject) => { migration.on('exit', resolve); migration.on('error', reject) })
  assert.equal(exit, 0, 'Fixture migration failed: ' + serverLog.slice(-3000))
  // Fill only mandatory, no-default legacy fixture columns; business values are explicit.
  async function seed(table, values) {
    assert(/^[a-z_]+$/.test(table))
    const [columns] = await owner.query(`SHOW COLUMNS FROM \`${table}\``)
    for (const c of columns) if (!(c.Field in values) && c.Null === 'NO' && c.Default == null && !c.Extra.includes('auto_increment'))
      values[c.Field] = /int|decimal|float|double|bit/.test(c.Type) ? 0 : /date|time/.test(c.Type) ? new Date() : c.Type === 'json' ? '{}' : ''
    const keys = Object.keys(values)
    await owner.query(`INSERT INTO \`${table}\` (${keys.map(k => `\`${k}\``).join(',')}) VALUES (${keys.map(() => '?').join(',')})`, Object.values(values))
  }
  const password = 'Fixture-' + crypto.randomBytes(20).toString('hex') + '!9'
  const salt = crypto.randomBytes(16), hash = `PBKDF2-SHA256$120000$${salt.toString('base64')}$${crypto.pbkdf2Sync(password, salt, 120000, 32, 'sha256').toString('base64')}`
  await owner.query("INSERT IGNORE INTO authroles(Code,Name) VALUES('super_admin','Super Admin'),('fixture_panel','Test panel'),('fixture_approver','Test budget approver')")
  await owner.query("INSERT IGNORE INTO authrolepermissions(RoleId,PermissionId) SELECT r.Id,p.Id FROM authroles r CROSS JOIN authpermissions p WHERE r.Code='super_admin' OR (r.Code='fixture_panel' AND p.Code IN ('recruitment.interview.panel','recruitment.document.sign','recruitment.document.view','dashboard.view')) OR (r.Code='fixture_approver' AND p.Code IN ('workflow.manage','dashboard.view'))")
  for (const [id, email, display, role] of [[9001, 'admin', 'Test Super Admin', 'super_admin'], [9002, 'panel1', 'Test Panel One', 'fixture_panel'], [9003, 'panel2', 'Test Panel Two', 'fixture_panel'], [9004, 'approver', 'Test Budget Approver', 'fixture_approver']]) {
    await seed('authusers', { Id: id, Email: `${email}@example.invalid`, DisplayName: display, PasswordHash: hash, MustChangePassword: false, IsActive: true, ClientId: id === 9004 ? 901 : null })
    await owner.query('INSERT INTO authuserroles(UserId,RoleId) SELECT ?,Id FROM authroles WHERE Code=?', [id, role])
  }
  await seed('clients', { Id: 901, Name: 'TEST MODE — Isolated UIDAI', Code: 'TEST-UIDAI' })
  await seed('recruitment_settings', { ClientId: 901, RecruitmentEnabled: true, IsActive: true })
  await seed('recruitment_requisitions', { Id: 901, ClientId: 901, RfrNumber: 'TEST-RFR-901', PositionTitle: 'Software Architect', NumberOfOpenings: 1, SalaryMin: 700000, SalaryMax: 1000000, BudgetAvailable: true, BudgetAmount: 1000000, RequestedByUserId: 9001, Status: 'Approved' })
  await seed('recruitment_open_positions', { Id: 901, RequisitionId: 901, ClientId: 901, PositionCode: 'TEST-POS-901', PositionTitle: 'Software Architect', RecruiterUserId: 9001, NumberOfPositions: 1, ApprovedPositions: 1, RemainingPositions: 1, SalaryMin: 700000, SalaryMax: 1000000, BudgetAvailable: true, BudgetAmount: 1000000 })
  await seed('recruitment_job_description_versions', { Id: 901, RequisitionId: 901, ClientId: 901, VersionNumber: 1, Title: 'Software Architect', Summary: 'Isolated regression fixture', RolePurpose: 'Test workflow', Status: 'Approved', CreatedByUserId: 9001 })
  for (const id of [901, 902]) await seed('recruitment_job_postings', { Id: id, PositionId: 901, ClientId: 901, JobDescriptionVersionId: 901, PublicSlug: `test-${id}`, PublicTitle: 'Software Architect', Status: 'Published', AutoRunAts: true, RequireEmailOtp: false, CreatedByUserId: 9001, PublishedAtUtc: new Date(Date.now() - (903 - id) * 10000) })
  await seed('recruitment_pipeline_definitions', { Id: 901, ClientId: 901, PipelineCode: 'TEST-CANDIDATE', PipelineName: 'TEST Candidate Journey', CurrentPublishedVersionId: 902, CreatedByUserId: 9001 })
  for (const v of [901, 902]) {
    await seed('recruitment_pipeline_versions', { Id: v, PipelineDefinitionId: 901, VersionNumber: v - 900, Status: v === 901 ? 'Retired' : 'Published', CreatedByUserId: 9001 })
    for (const [i, code, type, label] of [[1, 'ATS', 'ATS', 'ATS Screening'], [2, 'PROFILE_SHARING', 'Screening', 'Sharing of Profiles for Interview'], [3, 'INTERVIEW', 'Interview', 'Interview / Panel Assessment'], [4, 'OFFER', 'Offer', 'Offer stage']])
      await seed('recruitment_pipeline_stages', { Id: v * 10 + i, PipelineVersionId: v, StageCode: code, StageName: label, StageType: type, StageNumber: i, DisplayOrder: i, IsInitial: i === 1, IsTerminal: i === 4 })
    await seed('recruitment_stage_ats_configurations', { PipelineStageId: v * 10 + 1, MinimumAdvanceScore: 75, AutoAdvance: true, RequireHumanConfirmation: v === 901 })
    await seed('recruitment_interview_stage_configurations', { PipelineStageId: v * 10 + 3, RoundNumber: 1, MinimumPanelCount: 2, FeedbackRequired: true })
    for (const i of [1, 2, 3]) await seed('recruitment_pipeline_transitions', { PipelineVersionId: v, FromStageId: v * 10 + i, ToStageId: v * 10 + i + 1, OutcomeCode: i === 1 ? 'SHORTLIST' : i === 2 ? 'ADVANCE' : 'SELECTED', ActionLabel: 'Move manually' })
  }
  await seed('recruitment_position_pipeline_assignments', { Id: 901, PositionId: 901, JobPostingId: 901, PipelineVersionId: 901, AssignedByUserId: 9001 })
  for (const [id, first, scoreStatus] of [[901, 'Qualified', 'Scored'], [902, 'NeedsEvidence', 'NeedsReview']]) {
    await seed('recruitment_candidates', { Id: id, CandidateCode: `TEST-CAN-${id}`, ClientId: 901, FirstName: first, LastName: 'Candidate', Email: `${first.toLowerCase()}@example.invalid`, Phone: `${9847035000 + id}`, CreatedByUserId: 9001 })
    await seed('recruitment_candidate_resumes', { Id: id, CandidateId: id, AttachmentPublicId: crypto.randomUUID(), ParsingStatus: 'Parsed', ParsedText: 'Synthetic Software Architect resume evidence', ParsedJson: '{}' })
    await seed('recruitment_candidate_applications', { Id: id, ApplicationCode: `TEST-APP-${id}`, CandidateId: id, PositionId: 901, ClientId: 901, JobPostingId: 901, ResumeId: id, CurrentStage: 'ATS Screening', CurrentPipelineStageInstanceId: id, RecruiterUserId: 9001 })
    await seed('recruitment_application_pipeline_instances', { Id: id, ApplicationId: id, PipelineVersionId: 901, PositionPipelineAssignmentId: 901, CurrentStageInstanceId: id })
    await seed('recruitment_application_stage_instances', { Id: id, ApplicationId: id, ApplicationPipelineInstanceId: id, PipelineStageId: 9011, EnteredByUserId: 9001 })
    await seed('recruitment_application_scores', { Id: id, ApplicationId: id, ResumeId: id, TotalScore: 77.3, ShortlistThreshold: 75, HumanReviewRequired: true, ScoreStatus: scoreStatus, ScoringMethod: 'Fixture score', ComponentScoresJson: '{}', MatchedSkillsJson: '[]', MissingSkillsJson: '[]', ExplanationJson: '{}' })
  }
  // Work-order legacy duplicate: one unlinked record and its current linked journey.
  await seed('recruitment_work_orders', { Id: 901, ClientId: 901, WorkOrderNumber: 'TEST-WO-901', ReceivedAtUtc: new Date(), CreatedByUserId: 9001 })
  for (const id of [901, 902]) await seed('recruitment_work_order_lines', { Id: id, WorkOrderId: 901, LineNumber: id - 900, PositionName: 'Chief Architect', RequisitionId: id === 902 ? 902 : null, PositionId: id === 902 ? 902 : null })
  await seed('recruitment_requisitions', { Id: 902, ClientId: 901, RfrNumber: 'TEST-RFR-902', PositionTitle: 'Chief Architect', WorkOrderId: 901, WorkOrderLineNumber: 2, RequestedByUserId: 9001 })
  await seed('recruitment_open_positions', { Id: 902, RequisitionId: 902, ClientId: 901, PositionCode: 'TEST-POS-902', PositionTitle: 'Chief Architect' })
  await seed('recruitment_pipeline_definitions', { Id: 903, ClientId: 901, PipelineCode: 'TEST-POSITION', PipelineName: 'TEST Hiring Journey', CurrentPublishedVersionId: 903, CreatedByUserId: 9001 })
  await seed('recruitment_pipeline_versions', { Id: 903, PipelineDefinitionId: 903, VersionNumber: 1, Status: 'Published', ScopeType: 'Position', CreatedByUserId: 9001 })
  await seed('recruitment_pipeline_stages', { Id: 9031, PipelineVersionId: 903, StageCode: 'SIGNING_MOM', StageName: 'Signing of MoM', StageType: 'Approval', CardScope: 'Position', StageNumber: 1, DisplayOrder: 1 })
  for (const id of [901, 902]) {
    await seed('recruitment_position_pipeline_instances', { Id: id, ClientId: 901, RequisitionId: 902, WorkOrderId: 901, WorkOrderLineId: id, PositionId: id === 902 ? 902 : null, PipelineVersionId: 903, CurrentStageInstanceId: id, StartedByUserId: 9001 })
    await seed('recruitment_position_stage_instances', { Id: id, PositionPipelineInstanceId: id, PipelineStageId: 9031, Status: 'Active', EnteredAtUtc: new Date() })
  }
  // Real configured workflow task; approving it exercises the normal auth/API/bell path.
  await seed('workflowmasters', { Id: 901, Code: 'TEST_BUDGET', Name: 'TEST Hiring budget', ClientId: 901, ResourceType: 'RecruitmentBudget', IsActive: true })
  await seed('workflowstages', { Id: 901, WorkflowId: 901, StageOrder: 1, Name: 'Approve hiring budget', ApproverType: 'Specific User', ApproverUserId: 9004 })
  await seed('workflowinstances', { Id: 901, WorkflowId: 901, ResourceType: 'RecruitmentBudget', ResourceId: '901', RequestorUserId: 9001, Status: 'Pending' })
  await seed('workflowtasks', { Id: 901, InstanceId: 901, StageId: 901, ApproverUserId: 9004, Status: 'Pending' })
  await owner.query("UPDATE recruitment_requisitions SET BudgetApproverUserId=9004,BudgetApprovalStatus='Pending',BudgetApprovalWorkflowInstanceId=901 WHERE Id=901")
  await owner.query("UPDATE notification_smtp_settings SET IsEnabled=TRUE,DeliveryPaused=FALSE,Host='127.0.0.1',Port=?,EnableSsl=FALSE,UserName='',Password='',FromEmail='fixture@example.invalid',FromName='TEST local mail sink' WHERE Id=1", [mail.port])
  await seedHiringFlow(seed, owner)
  await seed('organizations', { Id: 901, Name: 'TEST Organization', LogoDataUrl: 'data:image/png;base64,' + fs.readFileSync(path.join(root, 'ess-mss/public/assets/organization-logo.png')).toString('base64') })
  api = start([])
  for (let i = 0; i < 90; i++) { try { if ((await fetch(apiUrl + '/api/auth/me')).status === 401) break } catch {} await new Promise(r => setTimeout(r, 500)) }
  console.log('Opening VISIBLE TEST MODE browser — real isolated API.')
  await owner.query("INSERT INTO recruitment_position_stage_events(PositionPipelineInstanceId,PositionStageInstanceId,EventType,EventTitle,ActorUserId) VALUES(902,902,'StageMoved','Moved to Signing of MoM',9001)")
  checksResult = await runRecruitmentPortalChecks({ root, ui, apiUrl, output, password, db: owner, mail, seed })
} finally {
  if (api && api.exitCode == null) { api.kill(); await new Promise(resolve => api.once('exit', resolve)) }
  fs.writeFileSync(path.join(output, 'api.log'), serverLog)
  // Only the exact random database created by this run is removed. No user data is touched.
  if (/^hrms_workflow_portal_[a-f0-9]{24}$/.test(name)) await owner.query(`DROP DATABASE IF EXISTS \`${name}\``)
  await owner.end()
  await mail.close()
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ status: checksResult ? 'Passed' : 'Failed', startedAt, completedAt: new Date().toISOString(), ...(checksResult || { detail: 'See failure.json and api.log; an earlier successful run is not evidence for this run.' }) }, null, 2))
}
