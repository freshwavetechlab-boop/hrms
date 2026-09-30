import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { DatabaseSync } from 'node:sqlite'

// Execute the production SQL expressions against isolated fixtures; no HRMS database access.
const loadSql = (path, dependencies = {}) => {
  const source = readFileSync(new URL(path, import.meta.url), 'utf8')
  const values = {}
  for (const match of source.matchAll(/internal (?:const|static readonly) string (\w+) = ([\s\S]*?);/g)) {
    const expression = match[2].replace(/@"([\s\S]*?)"/g, (_, value) => JSON.stringify(value)).replaceAll('.Replace(', '.replaceAll(')
    const scope = { ...dependencies, ...values }
    values[match[1]] = Function(...Object.keys(scope), 'return ' + expression)(...Object.values(scope))
  }
  return values
}
const job = loadSql('../../Payroll.API/Services/RecruitmentJobMom.cs')
job.CoversApplication = (document, application) => job.CoversApplicationSql.replaceAll('documentRow.', document + '.').replaceAll('applicationRow.', application + '.')
job.CurrentFor = document => job.CurrentSql.replaceAll('documentRow.', document + '.')
const sql = loadSql('../../Payroll.API/Services/RecruitmentPanelSignatures.cs', { RecruitmentJobMom: job })
function jsonAt(value, path) { return value == null ? null : JSON.parse(value)[path.slice(2)] }
function contains(value, expected) {
  if (Array.isArray(value)) return value.some(item => contains(item, expected))
  if (expected && typeof expected === 'object') return value && Object.entries(expected).every(([key, entry]) => contains(value[key], entry))
  return value === expected
}
function fixture() {
  const db = new DatabaseSync(':memory:')
  db.function('JSON_CONTAINS', (value, expected, path) => value == null ? null : +Boolean(contains(jsonAt(value, path), JSON.parse(expected))))
  db.function('JSON_LENGTH', (value, path) => jsonAt(value, path)?.length ?? null)
  db.function('CONCAT', { varargs: true }, (...args) => args.join(''))
  db.exec(`
    CREATE TABLE recruitment_audit(Id INTEGER PRIMARY KEY, EntityType TEXT, EntityId INTEGER, Action TEXT, NewValueJson TEXT);
    CREATE TABLE authusers(Id INTEGER);
    INSERT INTO authusers VALUES(10),(11),(12),(13),(99);
    CREATE TABLE recruitment_process_documents(Id INTEGER, ClientId INTEGER, ApplicationId INTEGER, InterviewId INTEGER, HiringCaseId INTEGER, PipelineStageId INTEGER);
    CREATE TABLE recruitment_interviews(Id INTEGER, ApplicationId INTEGER, Status TEXT, Result TEXT);
    CREATE TABLE recruitment_candidate_applications(Id INTEGER, ClientId INTEGER);
    CREATE TABLE recruitment_interview_panel_members(InterviewId INTEGER, PanelUserId INTEGER);
    CREATE TABLE recruitment_stage_default_panel_members(PipelineStageId INTEGER, PanelUserId INTEGER, IsRequired INTEGER);
    CREATE TABLE recruitment_process_document_signatures(ProcessDocumentId INTEGER, SignerUserId INTEGER, CandidateId INTEGER);
    CREATE TABLE recruitment_profile_submission_batches(Id INTEGER, HiringCaseId INTEGER, Status TEXT);
    CREATE TABLE recruitment_profile_submission_batch_items(BatchId INTEGER, ApplicationId INTEGER);
    INSERT INTO recruitment_process_documents VALUES (1,20,100,200,300,400);
    INSERT INTO recruitment_candidate_applications VALUES (100,20),(101,20),(102,30);
    INSERT INTO recruitment_interviews VALUES (200,100,'Completed','Selected'),(201,101,'Completed','Selected'),(202,102,'Completed','Selected');
    INSERT INTO recruitment_interview_panel_members VALUES (200,10),(200,11),(201,12),(202,13);
    INSERT INTO recruitment_stage_default_panel_members VALUES (400,99,1),(400,98,0);
    INSERT INTO recruitment_profile_submission_batches VALUES (500,300,'Approved'),(501,301,'Forwarded');
    INSERT INTO recruitment_profile_submission_batch_items VALUES (500,100),(501,101),(500,102);
  `)
  return db
}
const gate = db => ({ ...db.prepare(`SELECT ${sql.RequiredSql} required,${sql.CapturedSql} captured,${sql.CompleteSql} complete FROM recruitment_process_documents documentRow WHERE Id=1`).get() })
const sign = (db, user, candidate = null) => db.prepare('INSERT INTO recruitment_process_document_signatures VALUES (1,?,?)').run(user, candidate)
const member = (db, user) => db.prepare(`SELECT ? IN (${sql.MembersSql}) allowed FROM recruitment_process_documents documentRow WHERE Id=1`).get(user).allowed

test('every assigned panel member must sign; duplicate, outsider and candidate signatures cannot complete MoM', () => {
  const db = fixture()
  try {
    assert.deepEqual(gate(db), { required: 2, captured: 0, complete: 0 })
    sign(db, 10); sign(db, 10); sign(db, 12); sign(db, 99); sign(db, 11, 999)
    assert.deepEqual(gate(db), { required: 2, captured: 1, complete: 0 })
    sign(db, 11)
    assert.deepEqual(gate(db), { required: 2, captured: 2, complete: 1 })
    assert.equal(member(db, 10), 1); assert.equal(member(db, 12), 0); assert.equal(member(db, 99), 0)
  } finally { db.close() }
})
test('committee MoM uses only the selected candidates in this hiring case and client', () => {
  const db = fixture()
  try {
    db.exec('UPDATE recruitment_process_documents SET ApplicationId=NULL,InterviewId=NULL')
    sign(db, 10); sign(db, 11); sign(db, 13)
    assert.deepEqual(gate(db), { required: 2, captured: 2, complete: 1 })
    assert.equal(member(db, 12), 0); assert.equal(member(db, 13), 0)
    db.exec("INSERT INTO recruitment_interviews VALUES (203,100,'Scheduled','Pending')")
    assert.equal(member(db, 10), 0, 'Older selected round is not the current committee')
  } finally { db.close() }
})
test('required stage panel is a fallback only; no assigned panel stays incomplete', () => {
  const db = fixture()
  try {
    db.exec('DELETE FROM recruitment_interview_panel_members WHERE InterviewId=200')
    assert.deepEqual(gate(db), { required: 1, captured: 0, complete: 0 })
    assert.equal(member(db, 99), 1); assert.equal(member(db, 98), 0)
    sign(db, 99)
    assert.equal(gate(db).complete, 1)
    db.exec('DELETE FROM recruitment_stage_default_panel_members')
    assert.deepEqual(gate(db), { required: 0, captured: 0, complete: 0 })
  } finally { db.close() }
})
test('signatures remain attached to their exact document version', () => {
  const db = fixture()
  try {
    db.exec('INSERT INTO recruitment_process_document_signatures VALUES (2,10,NULL),(2,11,NULL)')
    assert.equal(gate(db).captured, 0)
  } finally { db.close() }
})

const repository = readFileSync(new URL('../../Payroll.API/Repositories/RecruitmentCaseRepository.cs', import.meta.url), 'utf8')
const evaluateSql = (expression, locals = {}) => Function('RecruitmentPanelSignatures', 'RecruitmentJobMom', ...Object.keys(locals), `return ${expression.replace(/@"([\s\S]*?)"/g, (_, value) => JSON.stringify(value))}`)({ ...sql, CompleteFor: alias => sql.CompleteSql.replaceAll('documentRow.', `${alias}.`) }, job, ...Object.values(locals))
function documentFixture() {
  const db = fixture()
  db.function('GREATEST', { varargs: true }, (...values) => Math.max(...values))
  db.exec(`ALTER TABLE recruitment_process_documents ADD DocumentType TEXT DEFAULT 'MOM';
    ALTER TABLE recruitment_process_documents ADD Status TEXT DEFAULT 'Prepared';
    ALTER TABLE recruitment_process_documents ADD VersionNumber INTEGER DEFAULT 1;
    ALTER TABLE recruitment_process_documents ADD CreatedAtUtc TEXT DEFAULT '2026-09-28';
    ALTER TABLE recruitment_process_documents ADD AttachmentPublicId TEXT;
    CREATE TABLE entity_attachments(entity_type TEXT, entity_id INTEGER, is_current INTEGER, is_deleted INTEGER, public_id TEXT);
    CREATE TABLE recruitment_position_pipeline_instances(Id INTEGER, PositionId INTEGER, ClientId INTEGER, Status TEXT);
    INSERT INTO recruitment_position_pipeline_instances VALUES (300,600,20,'Active'),(301,601,20,'Active');
    ALTER TABLE recruitment_process_documents ADD BodySnapshot TEXT;
    ALTER TABLE recruitment_process_documents ADD WorkflowInstanceId INTEGER;
    ALTER TABLE recruitment_candidate_applications ADD CurrentStage TEXT DEFAULT 'HR';
    ALTER TABLE recruitment_candidate_applications ADD TermsVersion INTEGER DEFAULT 1;
    ALTER TABLE recruitment_candidate_applications ADD TermsConfirmedAtUtc TEXT DEFAULT '2026-09-27';
    CREATE TABLE workflowinstances(Id INTEGER, ResourceType TEXT, ResourceId TEXT, Status TEXT, WorkflowId INTEGER);
    ALTER TABLE recruitment_candidate_applications ADD PositionId INTEGER;
    UPDATE recruitment_candidate_applications SET PositionId=600 WHERE Id=100;
    UPDATE recruitment_candidate_applications SET PositionId=601 WHERE Id=101;
    ALTER TABLE recruitment_candidate_applications ADD ApplicationType TEXT DEFAULT 'Application';
    CREATE TABLE recruitment_stage_process_document_requirements(Id INTEGER, PipelineStageId INTEGER, DocumentType TEXT, IsRequired INTEGER, RequiresSignature INTEGER, DisplayOrder INTEGER);
    INSERT INTO recruitment_stage_process_document_requirements VALUES(1,400,'MOM',1,1,1);
    CREATE TABLE recruitment_position_stage_events(PositionPipelineInstanceId INTEGER, EventType TEXT, CreatedAtUtc TEXT);`)
  return db
}
function prepareJob(db, candidates = [{ ApplicationId: 100, TermsVersion: 1, InterviewId: 200 }], panel = [10, 11]) {
  db.exec("UPDATE recruitment_process_documents SET ApplicationId=NULL,InterviewId=NULL,BodySnapshot='Combined job MoM' WHERE Id=1")
  db.prepare("INSERT INTO recruitment_audit(EntityType,EntityId,Action,NewValueJson) VALUES ('RecruitmentProcessDocument',1,'Job MoM prepared',?)").run(JSON.stringify({ Candidates: candidates, PanelMembers: panel.map(UserId => ({ UserId })) }))
}

test('document list restricts panel-only access and reports the same completion count as progression', () => {
  const db = documentFixture()
  prepareJob(db)
  try {
    const method = repository.slice(repository.indexOf('public async Task<IReadOnlyList<RecruitmentProcessDocument>> ListProcessDocumentsAsync'), repository.indexOf('public async Task<(RecruitmentProcessDocument? Row, string Error)> SaveProcessDocumentAsync'))
    const locals = {}
    for (const name of ['required', 'captured']) locals[name] = evaluateSql(method.match(new RegExp(`var ${name} = ([\\s\\S]*?);`))[1])
    const query = evaluateSql(method.match(/QueryAsync<RecruitmentProcessDocument>\(([\s\S]*?),\s*new \{/)[1], locals).replaceAll('<=>', 'IS')
    const list = (user, all = false) => db.prepare(query).all({ ClientId: 20, HiringCaseId: null, ApplicationId: null, UserId: user, ViewAll: +all })
    assert.equal(list(12).length, 0, 'Unassigned panel member cannot read the MoM')
    assert.equal(list(10).length, 1)
    assert.equal(list(99, true).length, 1, 'HR can review without being a signer')
    sign(db, 10)
    assert.equal(list(10)[0].CapturedSignaturesComplete, 0)
    sign(db, 11)
    assert.equal(list(10)[0].CapturedSignaturesComplete, 1)
    assert.equal(list(10)[0].RequiredSignatureCount, 2)
  } finally { db.close() }
})
test('stage exit cannot substitute an uploaded PDF or partial panel signatures for a signed MoM', () => {
  const db = documentFixture()
  prepareJob(db)
  try {
    const section = repository.slice(repository.indexOf('var missingDocuments ='), repository.indexOf('private static async Task ApplyHiringCaseAdvanceAsync'))
    const CurrentCohortDocumentSql = repository.match(/CurrentCohortDocumentSql = @"([\s\S]*?)";/)[1]
    const query = evaluateSql(section.match(/QueryAsync<string>\(([\s\S]*?), new \{ StageId/)[1], { CurrentCohortDocumentSql }).replaceAll('<=>', 'IS')
    const missing = () => db.prepare(query).all({ StageId: 400, CaseId: 300 }).length
    db.exec("UPDATE recruitment_process_documents SET Status='Signed'; INSERT INTO entity_attachments VALUES ('RECRUITMENT_PROCESS_DOCUMENT',1,1,0,'uploaded-final-pdf')")
    assert.equal(missing(), 1)
    sign(db, 10); assert.equal(missing(), 1)
    sign(db, 11); assert.equal(missing(), 0)
  } finally { db.close() }
})

const currentJob = db => db.prepare(`SELECT ${job.CurrentFor('d')} current FROM recruitment_process_documents d WHERE d.Id=1`).get().current
const covered = db => db.prepare(`SELECT a.Id FROM recruitment_process_documents d JOIN recruitment_candidate_applications a ON ${job.CoversApplication('d', 'a')} WHERE d.Id=1 ORDER BY a.Id`).all().map(row => row.Id)
function combinedJob() {
  const db = documentFixture()
  db.exec('UPDATE recruitment_candidate_applications SET PositionId=600 WHERE Id=101')
  prepareJob(db, [{ ApplicationId: 100, TermsVersion: 1, InterviewId: 200 }, { ApplicationId: 101, TermsVersion: 1, InterviewId: 201 }], [10, 11, 12])
  return db
}

test('one job MoM covers multiple selected candidates with the union of their panel members', () => {
  const db = combinedJob()
  try {
    assert.deepEqual(covered(db), [100, 101])
    assert.equal(currentJob(db), 1)
    sign(db, 10); sign(db, 11)
    assert.deepEqual(gate(db), { required: 3, captured: 2, complete: 0 })
    sign(db, 12)
    assert.equal(gate(db).complete, 1)
    db.exec('DELETE FROM recruitment_interview_panel_members')
    assert.equal(gate(db).complete, 1, 'The prepared document retains its exact committee')
  } finally { db.close() }
})

test('a different job or client cannot inherit the shared MoM approval', () => {
  const db = combinedJob()
  try {
    db.exec('UPDATE recruitment_candidate_applications SET PositionId=601 WHERE Id=101')
    assert.deepEqual(covered(db), [100])
    assert.equal(currentJob(db), 0)
    db.exec('UPDATE recruitment_candidate_applications SET PositionId=600,ClientId=30 WHERE Id=101')
    assert.deepEqual(covered(db), [100])
    assert.equal(currentJob(db), 0)
  } finally { db.close() }
})

for (const [label, mutation] of [
  ['changed terms', 'UPDATE recruitment_candidate_applications SET TermsVersion=2 WHERE Id=101'],
  ['unconfirmed terms', 'UPDATE recruitment_candidate_applications SET TermsConfirmedAtUtc=NULL WHERE Id=101'],
  ['rejected candidate', "UPDATE recruitment_candidate_applications SET CurrentStage='Rejected' WHERE Id=101"],
  ['a new interview round', "INSERT INTO recruitment_interviews VALUES (203,101,'Scheduled','Pending')"],
  ['replacement cohort', "INSERT INTO recruitment_position_stage_events VALUES (300,'ProfilesReworkStarted','2026-09-29')"],
  ['a newer prepared version', "INSERT INTO recruitment_process_documents(Id,HiringCaseId,DocumentType,BodySnapshot) VALUES (2,300,'MOM','Revised job MoM')"],
]) test(`${label} prevents reuse of the previous job MoM`, () => {
  const db = combinedJob()
  try {
    assert.equal(currentJob(db), 1)
    db.exec(mutation)
    assert.equal(currentJob(db), 0)
  } finally { db.close() }
})

test('HR approval of one signed job MoM resumes every covered application, only after all panel signatures', () => {
  const db = combinedJob()
  try {
    const source = readFileSync(new URL('../../Payroll.API/Repositories/RecruitmentCaseRepository.CandidateMom.cs', import.meta.url), 'utf8')
    const sync = source.slice(source.indexOf('public async Task<IReadOnlyList<long>> SyncJobMomApprovalAsync'))
    const query = evaluateSql(sync.match(/QueryAsync<long>\(([\s\S]*?), new \{ documentId/)[1])
    const approved = () => db.prepare(query).all({ documentId: 1, workflowInstanceId: 70 }).map(row => row.Id).sort()
    db.exec("UPDATE recruitment_process_documents SET Status='Signed',WorkflowInstanceId=70 WHERE Id=1; INSERT INTO workflowinstances VALUES (70,'RecruitmentPipelineTransition','MOM:1','Pending',80)")
    sign(db, 10); sign(db, 11)
    assert.deepEqual(approved(), [])
    db.exec("UPDATE workflowinstances SET Status='Approved'")
    assert.deepEqual(approved(), [])
    sign(db, 12)
    assert.deepEqual(approved(), [100, 101])
    db.exec('UPDATE recruitment_candidate_applications SET TermsVersion=2 WHERE Id=101')
    assert.deepEqual(approved(), [])
  } finally { db.close() }
})
