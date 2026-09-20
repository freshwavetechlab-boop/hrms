// Explicit operator maintenance, never a startup migration. Immutable old JDs/scores remain.
import assert from 'node:assert/strict'

const children = ['recruitment_jd_responsibilities', 'recruitment_jd_skill_requirements', 'recruitment_jd_qualification_requirements', 'recruitment_jd_certification_requirements', 'recruitment_jd_language_requirements', 'recruitment_jd_benefits']
const quote = name => { assert(/^[A-Za-z][A-Za-z0-9_]*$/.test(name)); return '`' + name + '`' }

export async function optionalSkillYears(db, { jdIds, actorId, apply = false }) {
  assert(jdIds.length > 0 && jdIds.every(Number.isSafeInteger) && Number.isSafeInteger(actorId))
  const [actors] = await db.query("SELECT u.Id FROM authusers u JOIN authuserroles ur ON ur.UserId=u.Id JOIN authroles r ON r.Id=ur.RoleId WHERE u.Id=? AND u.IsActive=TRUE AND u.ClientId IS NULL AND r.Code='super_admin'", [actorId])
  assert(actors.length, 'Active global-super-admin audit actor required')
  const results = []
  await db.beginTransaction()
  try {
    for (const id of [...new Set(jdIds)].sort((a, b) => a - b)) {
      const [source] = await db.query('SELECT * FROM recruitment_job_description_versions WHERE Id=?', [id])
      assert(source.length, 'Unknown JD target')
      await db.query('SELECT Id FROM recruitment_requisitions WHERE Id=? FOR UPDATE', [source[0].RequisitionId])
      const [locked] = await db.query('SELECT * FROM recruitment_job_description_versions WHERE Id=? FOR UPDATE', [id])
      assert.deepEqual(locked, source, 'JD changed during inspection')
      const [postings] = await db.query("SELECT Id,PublicSlug FROM recruitment_job_postings WHERE JobDescriptionVersionId=? AND Status IN ('Draft','Published') FOR UPDATE", [id])
      const [positions] = await db.query("SELECT p.Id FROM recruitment_open_positions p WHERE p.ApprovedJobDescriptionVersionId=? AND EXISTS(SELECT 1 FROM recruitment_job_postings j WHERE j.PositionId=p.Id AND j.Status IN ('Draft','Published')) FOR UPDATE", [id])
      const [rules] = await db.query('SELECT Id,SkillName,IsRequired,MinimumYears FROM recruitment_jd_skill_requirements WHERE JobDescriptionVersionId=? AND MinimumYears>0 ORDER BY Id', [id])
      if ((!postings.length && !positions.length) || !rules.length) { results.push({ oldJdId: id, skipped: 'No current duration rules' }); continue }
      assert.equal(source[0].Status, 'Approved', 'Only an already-approved current JD can receive this narrowly approved policy revision')
      const result = { oldJdId: id, title: source[0].Title, rules, postingIds: postings.map(p => p.Id), positionIds: positions.map(p => p.Id), applied: apply }
      results.push(result)
      if (!apply) continue
      const now = new Date()
      const [[version]] = await db.query('SELECT COALESCE(MAX(VersionNumber),0)+1 nextVersion FROM recruitment_job_description_versions WHERE RequisitionId=?', [source[0].RequisitionId])
      async function clone(table, whereColumn, whereValue, overrides) {
        const [schema] = await db.query("SELECT COLUMN_NAME name FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=? AND EXTRA NOT LIKE '%auto_increment%' AND EXTRA NOT LIKE '%GENERATED%' ORDER BY ORDINAL_POSITION", [table])
        assert(schema.length > 0, 'Source table schema must exist: ' + table)
        const values = [], select = schema.map(({ name }) => { if (Object.hasOwn(overrides, name)) { values.push(overrides[name]); return '?' } return quote(name) })
        const [insert] = await db.query(`INSERT INTO ${quote(table)} (${schema.map(c => quote(c.name)).join(',')}) SELECT ${select.join(',')} FROM ${quote(table)} WHERE ${quote(whereColumn)}=? ORDER BY Id`, [...values, whereValue])
        return insert
      }
      const inserted = await clone('recruitment_job_description_versions', 'Id', id, { VersionNumber: version.nextVersion, CreatedByUserId: actorId, ApprovedByUserId: actorId, WorkflowInstanceId: null, CreatedAtUtc: now, UpdatedAtUtc: now, ApprovedAtUtc: now })
      const newId = inserted.insertId; assert(newId > 0)
      for (const table of children) {
        await clone(table, 'JobDescriptionVersionId', id, { JobDescriptionVersionId: newId, ...(table === 'recruitment_jd_skill_requirements' ? { MinimumYears: 0 } : {}) })
        const [oldRows] = await db.query(`SELECT * FROM ${quote(table)} WHERE JobDescriptionVersionId=? ORDER BY Id`, [id])
        const [newRows] = await db.query(`SELECT * FROM ${quote(table)} WHERE JobDescriptionVersionId=? ORDER BY Id`, [newId])
        const comparable = rows => rows.map(({ Id, JobDescriptionVersionId, ...row }) => ({ ...row, ...(table === 'recruitment_jd_skill_requirements' ? { MinimumYears: '0.00' } : {}) }))
        assert.deepEqual(comparable(newRows), comparable(oldRows), 'Unexpected non-duration content change')
      }
      const [[remaining]] = await db.query('SELECT COUNT(*) count FROM recruitment_jd_skill_requirements WHERE JobDescriptionVersionId=? AND MinimumYears>0', [newId])
      assert.equal(remaining.count, 0)
      await db.query("UPDATE recruitment_job_postings SET JobDescriptionVersionId=? WHERE JobDescriptionVersionId=? AND Status IN ('Draft','Published')", [newId, id])
      if (positions.length) await db.query('UPDATE recruitment_open_positions SET ApprovedJobDescriptionVersionId=?,JobDescriptionVersion=? WHERE ApprovedJobDescriptionVersionId=? AND Id IN (?)', [newId, version.nextVersion, id, positions.map(p => p.Id)])
      const [oldAgain] = await db.query('SELECT * FROM recruitment_job_description_versions WHERE Id=?', [id]); assert.deepEqual(oldAgain, source)
      const [slugs] = postings.length ? await db.query('SELECT Id,PublicSlug FROM recruitment_job_postings WHERE Id IN (?) ORDER BY Id', [postings.map(p => p.Id)]) : [[]]
      assert.deepEqual(slugs, [...postings].sort((a, b) => a.Id - b.Id), 'Public URLs must not change')
      const [held] = await db.query(`SELECT DISTINCT a.Id FROM recruitment_candidate_applications a
JOIN recruitment_application_scores score ON score.ApplicationId=a.Id AND score.IsCurrent=TRUE AND score.ResumeId=a.ResumeId AND score.ScoreStatus='NeedsReview'
JOIN recruitment_application_score_skill_matches evidence ON evidence.ApplicationScoreId=score.Id AND evidence.MatchStatus='NeedsReview' AND evidence.MinimumYears>0
JOIN recruitment_application_pipeline_instances flow ON flow.ApplicationId=a.Id AND flow.Status='Active'
JOIN recruitment_application_stage_instances currentStage ON currentStage.Id=flow.CurrentStageInstanceId AND currentStage.Status='Active'
JOIN recruitment_pipeline_stages stage ON stage.Id=currentStage.PipelineStageId AND stage.StageType='ATS'
JOIN recruitment_open_positions position ON position.Id=a.PositionId
LEFT JOIN recruitment_job_postings posting ON posting.Id=a.JobPostingId
WHERE a.JoinedEmployeeId IS NULL AND a.ApplicationType='Application' AND COALESCE(posting.JobDescriptionVersionId,position.ApprovedJobDescriptionVersionId)=?
AND NOT EXISTS(SELECT 1 FROM recruitment_ats_scoring_jobs job WHERE job.ApplicationId=a.Id AND job.Status IN ('Queued','Retry','Processing')) FOR UPDATE`, [newId])
      for (const application of held) await db.query("INSERT INTO recruitment_ats_scoring_jobs(ApplicationId,RequestedByUserId,ForceScore,Status,AvailableAt,CreatedAt,UpdatedAt) VALUES(?,?,TRUE,'Queued',UTC_TIMESTAMP(),UTC_TIMESTAMP(),UTC_TIMESTAMP())", [application.Id, actorId])
      Object.assign(result, { newJdId: newId, version: version.nextVersion, recheckApplicationIds: held.map(a => a.Id) })
      await db.query("INSERT INTO recruitment_audit(EntityType,EntityId,Action,OldValueJson,NewValueJson,ChangedByUserId) VALUES('RecruitmentJobDescription',?,'Skill duration optional policy',?,?,?)", [newId, JSON.stringify({ jdId: id, rules }), JSON.stringify(result), actorId])
    }
    if (apply) await db.commit(); else await db.rollback()
    return results
  } catch (error) { await db.rollback(); throw error }
}
