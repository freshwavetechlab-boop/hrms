import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { DatabaseSync } from 'node:sqlite'

// Execute the production SQL expressions against isolated fixtures; no HRMS database access.
const source = readFileSync(new URL('../../Payroll.API/Services/RecruitmentPanelSignatures.cs', import.meta.url), 'utf8')
const sql = {}
for (const match of source.matchAll(/internal (?:const|static readonly) string (\w+) = ([\s\S]*?);/g)) {
  const expression = match[2].replace(/@"([\s\S]*?)"/g, (_, value) => JSON.stringify(value)).replaceAll('.Replace(', '.replace(')
  sql[match[1]] = Function(...Object.keys(sql), `return ${expression}`)(...Object.values(sql))
}
function fixture() {
  const db = new DatabaseSync(':memory:')
  db.exec(`
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
const evaluateSql = (expression, locals = {}) => Function('RecruitmentPanelSignatures', ...Object.keys(locals), `return ${expression.replace(/@"([\s\S]*?)"/g, (_, value) => JSON.stringify(value))}`)({ ...sql, CompleteFor: alias => sql.CompleteSql.replaceAll('documentRow.', `${alias}.`) }, ...Object.values(locals))
function documentFixture() {
  const db = fixture()
  db.function('GREATEST', { varargs: true }, (...values) => Math.max(...values))
  db.exec(`ALTER TABLE recruitment_process_documents ADD DocumentType TEXT DEFAULT 'MOM';
    ALTER TABLE recruitment_process_documents ADD Status TEXT DEFAULT 'Prepared';
    ALTER TABLE recruitment_process_documents ADD VersionNumber INTEGER DEFAULT 1;
    ALTER TABLE recruitment_process_documents ADD CreatedAtUtc TEXT DEFAULT '2026-09-28';
    ALTER TABLE recruitment_process_documents ADD AttachmentPublicId TEXT;
    CREATE TABLE entity_attachments(entity_type TEXT, entity_id INTEGER, is_current INTEGER, is_deleted INTEGER, public_id TEXT);
    CREATE TABLE recruitment_position_pipeline_instances(Id INTEGER, PositionId INTEGER);
    ALTER TABLE recruitment_candidate_applications ADD PositionId INTEGER;
    ALTER TABLE recruitment_candidate_applications ADD ApplicationType TEXT DEFAULT 'Application';
    CREATE TABLE recruitment_stage_process_document_requirements(Id INTEGER, PipelineStageId INTEGER, DocumentType TEXT, IsRequired INTEGER, RequiresSignature INTEGER, DisplayOrder INTEGER);
    INSERT INTO recruitment_stage_process_document_requirements VALUES(1,400,'MOM',1,1,1);
    CREATE TABLE recruitment_position_stage_events(PositionPipelineInstanceId INTEGER, EventType TEXT, CreatedAtUtc TEXT);`)
  return db
}
test('document list restricts panel-only access and reports the same completion count as progression', () => {
  const db = documentFixture()
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
