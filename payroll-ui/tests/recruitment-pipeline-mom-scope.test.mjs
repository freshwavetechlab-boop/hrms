import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { DatabaseSync } from 'node:sqlite'

const read = file => readFileSync(new URL(`../../Payroll.API/Repositories/${file}.cs`, import.meta.url), 'utf8')
const cases = read('RecruitmentCaseRepository')
const historical = cases.match(/HistoricalHiringCaseSql = @"([\s\S]*?)";/)[1]
const postInterview = cases.match(/CurrentStakeholderCode,\s*(\(hiringCase.PositionId[\s\S]*?) IsPostInterviewStage/)[1]
const negotiation = read('RecruitmentTalentRepository').match(/var inPipeline = await db.ExecuteScalarAsync<bool>\(@"([\s\S]*?)", application\)/)[1]

function fixture() {
  const db = new DatabaseSync(':memory:')
  db.exec(`
    CREATE TABLE recruitment_requisitions(Id INTEGER, ClientId INTEGER);
    CREATE TABLE recruitment_open_positions(Id INTEGER, ClientId INTEGER);
    CREATE TABLE recruitment_position_pipeline_instances(Id INTEGER, ClientId INTEGER, RequisitionId INTEGER, PositionId INTEGER, Status TEXT, PipelineVersionId INTEGER, CurrentStageInstanceId INTEGER, CompletedAtUtc TEXT);
    CREATE TABLE recruitment_work_order_lines(Id INTEGER, RequisitionId INTEGER, Status TEXT);
    CREATE TABLE recruitment_pipeline_stages(Id INTEGER, PipelineVersionId INTEGER, CardScope TEXT, IsActive INTEGER, StageCode TEXT, StageType TEXT, DisplayOrder INTEGER);
    CREATE TABLE recruitment_stage_process_document_requirements(PipelineStageId INTEGER, DocumentType TEXT);
    CREATE TABLE recruitment_position_stage_instances(Id INTEGER, PipelineStageId INTEGER);
    CREATE TABLE recruitment_application_pipeline_instances(ApplicationId INTEGER, CurrentStageInstanceId INTEGER, Status TEXT);
    CREATE TABLE recruitment_application_stage_instances(Id INTEGER, PipelineStageId INTEGER, Status TEXT);
    INSERT INTO recruitment_requisitions VALUES(42,20),(43,20);
    INSERT INTO recruitment_open_positions VALUES(22,20);
    INSERT INTO recruitment_position_pipeline_instances VALUES(1,20,42,22,'Active',3,501,NULL);
    INSERT INTO recruitment_work_order_lines VALUES(1,42,'Open'),(2,43,'Open'),(3,NULL,'Open');
    INSERT INTO recruitment_pipeline_stages VALUES
      (10,3,'Position',1,'ORDER_FOR_HIRING','Screening',1),
      (20,3,'Position',1,'SIGNING_MOM','Approval',4),
      (30,3,'Position',1,'NEGOTIATION_AND_MOM_TO_HR','HR',5),
      (40,3,'Position',1,'HR_DIVISION_APPROVAL','Approval',6),
      (50,3,'Application',1,'SELECTION_HR_REVIEW','HR',3),
      (60,3,'Application',1,'ATS','Screening',1);
    INSERT INTO recruitment_position_stage_instances VALUES(501,30);
    INSERT INTO recruitment_application_stage_instances VALUES(601,50,'Active');
    INSERT INTO recruitment_application_pipeline_instances VALUES(81,601,'Active');
  `)
  return db
}

test('deleted request and position cases are excluded; live demand and genuine intake remain current', () => {
  const db = fixture()
  try {
    const value = () => db.prepare(`SELECT ${historical} historical FROM recruitment_position_pipeline_instances hiringCase WHERE Id=1`).get().historical
    assert.equal(value(), 0)
    db.exec('DELETE FROM recruitment_requisitions WHERE Id=42')
    assert.equal(value(), 1)
    db.exec('INSERT INTO recruitment_requisitions VALUES(42,21)')
    assert.equal(value(), 1, 'Wrong client must not revive the request')
    db.exec('UPDATE recruitment_requisitions SET ClientId=20 WHERE Id=42; DELETE FROM recruitment_open_positions')
    assert.equal(value(), 1)
    db.exec('UPDATE recruitment_position_pipeline_instances SET RequisitionId=NULL,PositionId=NULL')
    assert.equal(value(), 0, 'A genuine unlinked work-order intake remains valid')
    db.exec("UPDATE recruitment_position_pipeline_instances SET Status='Superseded'")
    assert.equal(value(), 1)
  } finally { db.close() }
})

test('deleting a request retires only its own demand line and case', () => {
  const db = fixture()
  db.function('UTC_TIMESTAMP', precision => '2026-09-22T10:00:00Z')
  try {
    const source = read('RecruitmentRepository')
    const lineSql = source.match(/"(UPDATE recruitment_work_order_lines SET RequisitionId=NULL,Status='Superseded'[^"\r\n]+)"/)[1]
    const caseSql = source.match(/"(UPDATE recruitment_position_pipeline_instances SET Status='Superseded',CompletedAtUtc[^"\r\n]+)"/)[1]
    db.prepare(lineSql).run({ Id: 42 })
    db.prepare(caseSql).run({ Id: 42 })
    assert.deepEqual(db.prepare('SELECT Status FROM recruitment_work_order_lines ORDER BY Id').all().map(row => row.Status), ['Superseded', 'Open', 'Open'])
    assert.equal(db.prepare('SELECT RequisitionId FROM recruitment_work_order_lines WHERE Id=1').get().RequisitionId, null)
    assert.equal(db.prepare('SELECT Status FROM recruitment_position_pipeline_instances WHERE Id=1').get().Status, 'Superseded')
  } finally { db.close() }
})

test('MoM submenu follows the configured signing stage and hides earlier or rolled-back stages', () => {
  const db = fixture()
  try {
    const eligibleAt = stageId => db.prepare(`SELECT ${postInterview} eligible FROM recruitment_position_pipeline_instances hiringCase CROSS JOIN recruitment_pipeline_stages stage WHERE hiringCase.Id=1 AND stage.Id=?`).get(stageId).eligible
    assert.equal(eligibleAt(10), 0)
    assert.equal(eligibleAt(20), 1)
    assert.equal(eligibleAt(30), 1)
    assert.equal(eligibleAt(40), 1)
    assert.equal(eligibleAt(10), 0, 'Rollback hides the journey again')
    db.exec("UPDATE recruitment_position_pipeline_instances SET Status='Superseded'")
    assert.equal(eligibleAt(20), 0)
  } finally { db.close() }
})

test('negotiation requires both candidate and hiring pipelines; raw intake cannot qualify', () => {
  const db = fixture()
  try {
    const eligible = () => Object.values(db.prepare(negotiation).get({ Id: 81, PositionId: 22, ClientId: 20 }))[0]
    assert.equal(eligible(), 1)
    db.exec('UPDATE recruitment_application_stage_instances SET PipelineStageId=60')
    assert.equal(eligible(), 0, 'Raw ATS candidate is excluded even when job reached negotiation')
    db.exec('UPDATE recruitment_application_stage_instances SET PipelineStageId=50; UPDATE recruitment_position_stage_instances SET PipelineStageId=20')
    assert.equal(eligible(), 0, 'Signing MoM is before negotiation')
    db.exec('UPDATE recruitment_position_stage_instances SET PipelineStageId=30; DELETE FROM recruitment_requisitions WHERE Id=42')
    assert.equal(eligible(), 0)
  } finally { db.close() }
})
