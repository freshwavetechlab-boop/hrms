// Synthetic configuration only. Actions are exercised through the real API by the portal batch.
export async function seedHiringFlow(seed, db) {
  await seed('recruitment_work_orders', { Id: 904, ClientId: 901, WorkOrderNumber: 'TEST-SOFTWARE-WO', ReceivedAtUtc: new Date(), CreatedByUserId: 9001 })
  await seed('recruitment_work_order_lines', { Id: 904, WorkOrderId: 904, LineNumber: 1, PositionName: 'Software Architect', RequisitionId: 901, PositionId: 901 })
  await db.query('UPDATE recruitment_requisitions SET WorkOrderId=904,WorkOrderLineNumber=1 WHERE Id=901')
  await db.query('UPDATE recruitment_open_positions SET BudgetAvailable=TRUE,BudgetAmount=1000000,SalaryMax=1100000,ApprovedPositions=1 WHERE Id=901')
  await seed('recruitment_pipeline_definitions', { Id: 904, ClientId: 901, PipelineCode: 'TEST-FULL-HIRING', PipelineName: 'Full hiring workflow', CurrentPublishedVersionId: 904, CreatedByUserId: 9001 })
  await seed('recruitment_pipeline_versions', { Id: 904, PipelineDefinitionId: 904, VersionNumber: 1, Status: 'Published', ScopeType: 'Position', CreatedByUserId: 9001 })
  for (const [id, resource, label] of [[902, 'RecruitmentPipelineTransition', 'Approve HR hiring stage'], [903, 'RecruitmentOffer', 'Approve negotiated offer']]) {
    await seed('workflowmasters', { Id: id, Code: `TEST_APPROVAL_${id}`, Name: label, ClientId: 901, ResourceType: resource, IsActive: true })
    await seed('workflowstages', { Id: id, WorkflowId: id, StageOrder: 1, Name: label, ApproverType: 'Specific User', ApproverUserId: 9004 })
  }
  await seed('recruitment_templates', { Id: 904, ClientId: 901, TemplateType: 'Offer Letter', TemplateCode: 'TEST_OFFER', TemplateName: 'Isolated test offer', SubjectTemplate: 'TEST OFFER — NOT FOR ISSUE', BodyTemplate: '<p>Isolated test offer for {{CandidateName}}. CTC: {{OfferedCtc}}.</p>', IsActive: true })
  await seed('recruitment_position_pipeline_instances', { Id: 904, ClientId: 901, RequisitionId: 901, WorkOrderId: 904, WorkOrderLineId: 904, PositionId: 901, PipelineVersionId: 904, CurrentStageInstanceId: 9041, StartedByUserId: 9001 })
  const stages = [
    ['PROFILE_REVIEW', 'Screening', 'Profile Review & Shortlisting'],
    ['PROFILE_SHARING', 'Screening', 'Sharing of Profiles for Interview'],
    ['INTERVIEW_PANEL_ASSESSMENT', 'Interview', 'Interview / Panel Assessment'],
    ['SIGNING_MOM', 'Approval', 'Signing of MoM'],
    ['NEGOTIATION_AND_MOM_TO_HR', 'HR', 'Negotiation and MoM to HR'],
    ['HR_DIVISION_APPROVAL', 'Approval', 'HR Division Approval'],
    ['OFFER_ISSUANCE', 'Offer', 'Offer issuance'],
    ['CONVEY_JOINING_DATE', 'Joining', 'Conveying joining date'],
  ]
  for (const [index, [code, type, name]] of stages.entries()) {
    await seed('recruitment_pipeline_stages', { Id: 9041 + index, PipelineVersionId: 904, StageNumber: index + 1, DisplayOrder: index + 1, StageCode: code, StageType: type, StageName: name, CardScope: 'Position', IsInitial: index === 0, IsTerminal: index === stages.length - 1, RequiresApproval: index === 5, ApprovalWorkflowId: index === 5 ? 902 : null })
    await seed('recruitment_position_stage_instances', { Id: 9041 + index, PositionPipelineInstanceId: 904, PipelineStageId: 9041 + index, Status: index === 0 ? 'Active' : 'Pending', EnteredAtUtc: index === 0 ? new Date() : null })
  }
  for (let index = 0; index < stages.length - 1; index++) await seed('recruitment_pipeline_transitions', { PipelineVersionId: 904, FromStageId: 9041 + index, ToStageId: 9042 + index, OutcomeCode: 'ADVANCE', ActionLabel: 'Move manually', DisplayOrder: 1 })
  await seed('recruitment_stage_process_document_requirements', { PipelineStageId: 9044, DocumentType: 'MOM', IsRequired: true, RequiresSignature: true })
  // Offer is not a completed hire. Acceptance is a separate governed transition.
  for (const v of [901, 902]) {
    await db.query('UPDATE recruitment_pipeline_stages SET IsTerminal=FALSE WHERE Id=?', [v * 10 + 4])
    await seed('recruitment_pipeline_stages', { Id: v * 10 + 5, PipelineVersionId: v, StageNumber: 5, DisplayOrder: 5, StageCode: 'ACCEPTED', StageType: 'Joining', StageName: 'Offer accepted / Pre-boarding', IsTerminal: true })
    await seed('recruitment_pipeline_transitions', { PipelineVersionId: v, FromStageId: v * 10 + 4, ToStageId: v * 10 + 5, OutcomeCode: 'ACCEPTED', ActionLabel: 'Move manually' })
    await seed('recruitment_stage_offer_configurations', { PipelineStageId: v * 10 + 4, OfferTemplateId: 904, BudgetBasis: 'ApprovedPerPosition', MaximumVariancePercent: 0, RequireApprovalWhenVarianceExceeded: false, ApprovalWorkflowId: 903, CandidateResponseValidityDays: 7 })
  }
}
