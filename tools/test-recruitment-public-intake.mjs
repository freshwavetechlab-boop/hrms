import assert from 'node:assert/strict'

// Reuse the configured form, parser, queue and public portal: no intercepted responses.
export async function verifyPublicIntake({ admin, call, ok, step, db, seed, freshPage, ui, expect }) {
  const slug = 'f905f905f905f905f905f905f905f905'
  await seed('attachment_attributes', { id: 9905, client_id: 901, attribute_code: 'RESUME', attribute_name: 'Resume' })
  await seed('attachment_field_configurations', { id: 9905, client_id: 901, attachment_attribute_id: 9905, module_code: 'RECRUITMENT', form_code: 'CANDIDATE_APPLICATION', field_key: 'RESUME', field_label: 'Resume', owner_can_upload: true, owner_can_replace: true, allowed_extensions_json: '["txt"]', allowed_mime_types_json: '["text/plain"]', maximum_file_size_bytes: 1000000 })
  const form = ok(await call(admin, '/api/recruitment-orchestration/forms', { clientId: 901, formCode: 'TEST_PUBLIC_INTAKE', formName: 'Test public application', requiresEmailVerification: true }))
  const version = ok(await call(admin, `/api/recruitment-orchestration/forms/${form.id}/versions`, { sections: [{ sectionCode: 'PERSONAL', sectionLabel: 'Personal details', fields: [
    { stableFieldCode: 'RESUME', label: 'Resume', fieldTypeCode: 'UPLOAD', isRequired: true, semanticCodes: ['RESUME'], attachmentFieldConfigurationId: 9905, displayOrder: 1 },
    ...[['FIRST_NAME', 'First name'], ['LAST_NAME', 'Last name'], ['EMAIL', 'Email'], ['PHONE', 'Phone'], ['HIGHEST_QUALIFICATION', 'Qualification']].map(([code, label], i) => ({ stableFieldCode: code, label, fieldTypeCode: 'TEXT', isRequired: code === 'FIRST_NAME', semanticCodes: [code], displayOrder: i + 2 }))
  ] }] }))
  ok(await call(admin, `/api/recruitment-orchestration/form-versions/${version.id}/publish`, {}))
  // Separate opening avoids adding a third candidate to the completed offer cohort.
  await seed('recruitment_requisitions', { Id: 905, ClientId: 901, RfrNumber: 'TEST-RFR-905', PositionTitle: 'Public Architect', Status: 'Approved', RequestedByUserId: 9001 })
  await seed('recruitment_open_positions', { Id: 905, ClientId: 901, RequisitionId: 905, PositionCode: 'TEST-POS-905', PositionTitle: 'Public Architect', NumberOfPositions: 1, ApprovedPositions: 1, RemainingPositions: 1, RecruiterUserId: 9001 })
  await db.query('UPDATE recruitment_requisitions SET OpenPositionId=905 WHERE Id=905')
  await seed('recruitment_position_pipeline_assignments', { PositionId: 905, PipelineVersionId: 904, AssignedByUserId: 9001 })
  await seed('recruitment_job_description_versions', { Id: 9905, ClientId: 901, RequisitionId: 905, VersionNumber: 1, Title: 'Public Architect', Summary: 'Build Kubernetes services', Status: 'Approved', CreatedByUserId: 9001 })
  await seed('recruitment_jd_skill_requirements', { JobDescriptionVersionId: 9905, SkillName: 'Kubernetes', IsRequired: true, MinimumYears: 0 })
  await seed('recruitment_job_postings', { Id: 905, ClientId: 901, PositionId: 905, JobDescriptionVersionId: 9905, ApplicationFormVersionId: version.id, PublicSlug: slug, PublicTitle: 'Public Architect', Status: 'Published', AutoRunAts: true, EnableResumeParsing: true, EnableAiParsing: false, RequireEmailOtp: false, CreatedByUserId: 9001, PublishedAtUtc: new Date() })
  await seed('recruitment_position_pipeline_assignments', { PositionId: 905, JobPostingId: 905, PipelineVersionId: 902, AssignedByUserId: 9001 })
  const guest = await freshPage()
  await guest.goto(ui + '/careers/' + slug)
  await guest.getByPlaceholder('name@example.com').fill('public.fixture@example.invalid')
  await guest.getByPlaceholder('Your mobile number').fill('9847035999')
  await guest.locator('.public-consent input').check()
  await guest.locator('form.public-start-form button[type=submit]').click()
  await expect(guest.getByRole('heading', { name: 'Application details' })).toBeVisible()
  assert.equal(await guest.getByRole('heading', { name: 'Check your inbox' }).count(), 0)
  await step(guest, 'Public job OTP-off overrides form OTP-on; candidate opens actual form')
  await guest.locator('input[type=file]').first().setInputFiles({ name: 'public-fixture.txt', mimeType: 'text/plain', buffer: Buffer.from('AARAV TESTWARD\npublic.fixture@example.invalid | +91 9847035999\nEXPERIENCE SUMMARY\nSoftware Architect with 7 years of experience designing reliable systems.\nSKILLS\nKubernetes, Java, AWS, Docker\nEDUCATION\nB.Tech in Computer Science\n') })
  await expect(guest.getByText(/Resume parsed\. Please review/)).toBeVisible({ timeout: 60000 })
  await guest.getByRole('button', { name: /Submit application/ }).click()
  await expect(guest.getByText('Application submitted', { exact: true })).toBeVisible({ timeout: 60000 })
  await step(guest, 'Fresh external resume parsed and saved with APP acknowledgement')
  await expect.poll(async () => {
    const [[row]] = await db.query("SELECT s.ScoreStatus,s.TotalScore FROM recruitment_candidate_applications a JOIN recruitment_application_scores s ON s.ApplicationId=a.Id AND s.ResumeId=a.ResumeId AND s.IsCurrent=TRUE WHERE a.PositionId=905 ORDER BY s.Id DESC LIMIT 1")
    return Boolean(row && ['Completed', 'Scored'].includes(row.ScoreStatus) && Number(row.TotalScore) >= 75)
  }, { timeout: 90000, intervals: [1500] }).toBe(true)
  const [[saved]] = await db.query('SELECT a.Id,a.ApplicationCode,p.PipelineVersionId FROM recruitment_candidate_applications a JOIN recruitment_application_pipeline_instances p ON p.ApplicationId=a.Id WHERE a.PositionId=905')
  assert.equal(saved.PipelineVersionId, 902)
  await expect.poll(async () => {
    const [[row]] = await db.query('SELECT CurrentStage FROM recruitment_candidate_applications WHERE Id=?', [saved.Id])
    return row?.CurrentStage
  }, { timeout: 90000, intervals: [1500] }).toBe('Interview / Panel Assessment')
  await admin.goto(ui + '/recruitment/interview-queue')
  await expect(admin.getByText(/^Aarav Testward$/i).first()).toBeVisible()
  await step(guest, 'New external applicant auto-scored under v2 and appears in Schedule Interviews')
}
