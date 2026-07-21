export type DynamicFormVersionStatus = 'Draft' | 'Published' | 'Retired'
export type DynamicFormFieldTypeCode =
  | 'TEXT'
  | 'TEXTAREA'
  | 'NUMBER'
  | 'DATE'
  | 'DATETIME'
  | 'EMAIL'
  | 'PHONE'
  | 'SEARCH_SELECT'
  | 'MULTI_SELECT'
  | 'RADIO'
  | 'CHECKBOX'
  | 'UPLOAD'

export type DynamicFormDefinition = {
  id: number
  clientId: number
  clientName: string
  moduleCode: string
  formCode: string
  formName: string
  purposeCode: string
  entityType: string
  status: 'Active' | 'Inactive' | string
  currentPublishedVersionId?: number | null
  createdByUserId?: number
  createdAtUtc?: string
  updatedAtUtc?: string
  versions: DynamicFormVersion[]
}

export type SaveDynamicFormDefinition = Pick<DynamicFormDefinition,
  'id' | 'clientId' | 'moduleCode' | 'formCode' | 'formName' | 'purposeCode' | 'entityType' | 'status'>

export type DynamicFormVersion = {
  id: number
  formDefinitionId: number
  versionNumber: number
  status: DynamicFormVersionStatus
  createdByUserId?: number
  publishedByUserId?: number | null
  publishedAtUtc?: string | null
  createdAtUtc?: string
  sections: DynamicFormSection[]
}

export type DynamicFormSection = {
  id: number
  formVersionId: number
  sectionCode: string
  sectionLabel: string
  description: string
  displayOrder: number
  fields: DynamicFormField[]
}

export type DynamicFormField = {
  id: number
  formVersionId: number
  sectionId: number
  fieldTypeCode: DynamicFormFieldTypeCode
  stableFieldCode: string
  label: string
  placeholder: string
  helpText: string
  isRequired: boolean
  displayOrder: number
  widthColumns: number
  minimumLength?: number | null
  maximumLength?: number | null
  minimumNumber?: number | null
  maximumNumber?: number | null
  minimumDate?: string | null
  maximumDate?: string | null
  attachmentFieldConfigurationId?: number | null
  attachmentConstraints?: PublicAttachmentConstraints | null
  lookupSourceCode: string
  isActive: boolean
  options: DynamicFormFieldOption[]
  semanticCodes: string[]
  validationRules: DynamicFormValidationRule[]
}

export type PublicAttachmentConstraints = {
  allowMultiple: boolean
  maximumFileCount: number
  maximumFileSizeBytes: number
  maximumTotalSizeBytes?: number | null
  allowedExtensions: string[]
  allowedMimeTypes: string[]
}

export type DynamicFormFieldOption = {
  id: number
  fieldId: number
  optionCode: string
  optionLabel: string
  displayOrder: number
  isActive: boolean
}

export type DynamicFormValidationRuleType =
  | 'REQUIRED'
  | 'REGEX'
  | 'EMAIL'
  | 'PHONE'
  | 'DATE'
  | 'MIN_LENGTH'
  | 'MAX_LENGTH'
  | 'MIN_NUMBER'
  | 'MAX_NUMBER'
  | 'MIN_DATE'
  | 'MAX_DATE'
  | 'BOOLEAN_TRUE'
  | 'COMPARE_VALUE'
  | 'COMPARE_FIELD'

export type DynamicFormValidationRule = {
  id: number
  fieldId: number
  ruleType: DynamicFormValidationRuleType | string
  comparisonOperator: string
  compareFieldId?: number | null
  compareFieldCode?: string | null
  textValue?: string | null
  integerValue?: number | null
  decimalValue?: number | null
  dateValue?: string | null
  booleanValue?: boolean | null
  errorMessage: string
  displayOrder: number
}

export type DynamicFormLookupSource = {
  id: number
  sourceCode: string
  sourceName: string
  resolverCode: string
  isClientScoped: boolean
  minimumSearchLength: number
  maximumResults: number
  isActive: boolean
}

export type DynamicLookupOption = { value: string; label: string }

export type RecruitmentAttachmentFieldConfigurationOption = {
  id: number
  clientId: number
  attributeCode: string
  attributeName: string
  fieldKey: string
  fieldLabel: string
  allowMultiple: boolean
  minimumFileCount: number
  maximumFileCount: number
  allowedExtensionsJson: string
  allowedMimeTypesJson: string
  maximumFileSizeBytes: number
  isActive: boolean
}

export type RecruitmentWorkflowOption = {
  id: number
  clientId?: number | null
  code: string
  name: string
  resourceType: string
  isActive: boolean
}

export type RecruitmentPositionOption = {
  id: number
  clientId: number
  clientName?: string
  requisitionId?: number
  rfrNumber?: string
  positionCode: string
  positionTitle: string
  department: string
  status: string
  jobLocation?: string
  employmentType?: string
  remainingPositions?: number
  approvedJobDescriptionVersionId?: number | null
}

export type RecruitmentOrchestrationLookups = {
  lookupSources: DynamicFormLookupSource[]
  attachmentConfigurations: RecruitmentAttachmentFieldConfigurationOption[]
  attachmentFieldConfigurations: RecruitmentAttachmentFieldConfigurationOption[]
  workflows: RecruitmentWorkflowOption[]
  forms: DynamicFormDefinition[]
  positions: RecruitmentPositionOption[]
  atsScoringProfiles?: Array<{ id: number; profileName: string; positionCategory: string; isActive: boolean }>
  atsProfiles: Array<{ id: number; profileCode: string; profileName: string; isActive: boolean }>
  interviewCompetencies?: Array<{ id: number; competencyCode: string; competencyName: string; isActive: boolean }>
  templates?: Array<{ id: number; templateCode: string; templateName: string; templateType: string; isActive: boolean }>
}

export type RecruitmentPipelineDefinition = {
  id: number
  clientId: number
  clientName: string
  pipelineCode: string
  pipelineName: string
  description: string
  currentPublishedVersionId?: number | null
  isActive: boolean
  createdByUserId?: number
  createdAtUtc?: string
  updatedAtUtc?: string
  versions?: RecruitmentPipelineVersion[]
}

export type RecruitmentPipelineDetail = {
  definition: RecruitmentPipelineDefinition
  versions: RecruitmentPipelineVersion[]
}

export type SaveRecruitmentPipelineDefinition = Pick<RecruitmentPipelineDefinition,
  'id' | 'clientId' | 'pipelineCode' | 'pipelineName' | 'description' | 'isActive'>

export type RecruitmentPipelineVersion = {
  id: number
  pipelineDefinitionId: number
  versionNumber: number
  status: DynamicFormVersionStatus
  createdByUserId?: number
  publishedByUserId?: number | null
  publishedAtUtc?: string | null
  createdAtUtc?: string
  stages: RecruitmentPipelineStage[]
  transitions: RecruitmentPipelineTransition[]
}

export type RecruitmentPipelineStage = {
  id: number
  pipelineVersionId: number
  stageCode: string
  stageName: string
  stageType: string
  stageNumber: number
  displayOrder: number
  slaDurationMinutes: number
  slaWarningMinutes: number
  approvalWorkflowId?: number | null
  requiresApproval: boolean
  calendarEnabled: boolean
  allowSkip: boolean
  isInitial: boolean
  isTerminal: boolean
  isActive: boolean
  actions: RecruitmentPipelineStageAction[]
  atsConfiguration?: RecruitmentStageAtsConfiguration | null
  externalFormConfiguration?: RecruitmentStageExternalFormConfiguration | null
  attachmentRequirements: RecruitmentStageAttachmentRequirement[]
  offerConfiguration?: RecruitmentStageOfferConfiguration | null
  interviewConfiguration?: RecruitmentInterviewStageConfiguration | null
}

export type RecruitmentPipelineStageAction = {
  id: number
  pipelineStageId: number
  triggerEvent: string
  actionCode: string
  executionOrder: number
  isBlocking: boolean
  workflowId?: number | null
  templateId?: number | null
  isActive: boolean
}

export type RecruitmentStageAtsConfiguration = {
  id: number
  pipelineStageId: number
  scoringProfileId?: number | null
  minimumAdvanceScore: number
  maximumRejectScore: number
  autoScoreOnEntry: boolean
  autoAdvance: boolean
  autoReject: boolean
  requireHumanConfirmation: boolean
  advanceOutcomeCode: string
  rejectOutcomeCode: string
}

export type RecruitmentStageExternalFormConfiguration = {
  id: number
  pipelineStageId: number
  formVersionId: number
  submissionRequired: boolean
  allowSaveDraft: boolean
  actionTokenValidityMinutes: number
  actionTokenMaximumUses: number
}

export type RecruitmentStageAttachmentRequirement = {
  id: number
  pipelineStageId: number
  attachmentFieldConfigurationId: number
  isRequired: boolean
  minimumFileCount: number
  maximumFileCount: number
  requiresVerification: boolean
  displayOrder: number
}

export type RecruitmentStageOfferConfiguration = {
  id: number
  pipelineStageId: number
  offerTemplateId?: number | null
  approvalWorkflowId?: number | null
  budgetBasis: string
  maximumVariancePercent: number
  requireApprovalWhenVarianceExceeded: boolean
  varianceApprovalWorkflowId?: number | null
  candidateResponseValidityDays: number
  requireAcceptedOfferToAdvance: boolean
}

export type RecruitmentInterviewStageConfiguration = {
  id: number
  pipelineStageId: number
  roundNumber: number
  interviewType: string
  defaultDurationMinutes: number
  minimumPanelCount: number
  minimumPassingScore: number
  feedbackRequired: boolean
  calendarEnabled: boolean
  allowReschedule: boolean
  competencies: RecruitmentInterviewStageCompetency[]
}

export type RecruitmentInterviewStageCompetency = {
  id: number
  interviewStageConfigurationId: number
  competencyId: number
  competencyName: string
  weightPercent: number
  minimumScore: number
  displayOrder: number
}

export type RecruitmentPipelineTransition = {
  id: number
  pipelineVersionId: number
  fromStageId: number
  toStageId: number
  fromStageCode: string
  toStageCode: string
  outcomeCode: string
  actionLabel: string
  approvalWorkflowId?: number | null
  requiresReason: boolean
  isActive: boolean
  displayOrder: number
  rules: RecruitmentPipelineTransitionRule[]
}

export type RecruitmentPipelineTransitionRule = {
  id: number
  transitionId: number
  ruleType: string
  comparisonOperator: string
  textValue?: string | null
  integerValue?: number | null
  decimalValue?: number | null
  booleanValue?: boolean | null
  isMandatory: boolean
  errorMessage: string
  displayOrder: number
}

export type RecruitmentPipelineBoard = {
  positionId: number
  jobPostingId?: number | null
  positionCode: string
  positionTitle: string
  pipelineVersionId: number
  lanes: RecruitmentPipelineBoardLane[]
}

export type RecruitmentPipelineBoardLane = {
  stageId: number
  stageCode: string
  stageName: string
  stageType: string
  displayOrder: number
  slaDurationMinutes: number
  slaWarningMinutes: number
  applications: RecruitmentPipelineBoardCard[]
}

export type RecruitmentPipelineBoardCard = {
  applicationId: number
  applicationCode: string
  candidateId: number
  candidateName: string
  candidateEmail: string
  atsScore?: number | null
  enteredAtUtc: string
  dueAtUtc?: string | null
  elapsedSeconds: number
  remainingSeconds: number
  pausedDurationSeconds: number
  isSlaWarning: boolean
  isSlaBreached: boolean
  stageStatus: string
  pendingBlockingActionCount: number
  failedActionCount: number
}

export type RecruitmentPipelineTransitionResult = {
  requestId: number
  applicationId: number
  status: string
  workflowInstanceId?: number | null
  currentStageInstanceId?: number | null
  message: string
}

export type RecruitmentApplicationStageInstance = {
  id: number
  applicationPipelineInstanceId: number
  applicationId: number
  pipelineStageId: number
  stageCode: string
  stageName: string
  status: string
  outcomeCode: string
  enteredAtUtc: string
  dueAtUtc?: string | null
  exitedAtUtc?: string | null
  activeDurationSeconds: number
  pausedDurationSeconds: number
  isSlaBreached: boolean
}

export type RecruitmentJobPosting = {
  id: number
  clientId: number
  positionId: number
  jobDescriptionVersionId: number
  applicationFormVersionId?: number | null
  publicSlug: string
  publicTitle: string
  status: string
  opensAtUtc?: string | null
  closesAtUtc?: string | null
  maximumApplications?: number | null
  applicationCount: number
  searchEngineVisible: boolean
  publishedAtUtc?: string | null
  createdAtUtc?: string
  updatedAtUtc?: string
  positionCode: string
  positionTitle: string
  clientName: string
}

export type SaveRecruitmentJobPosting = {
  id: number
  positionId: number
  jobDescriptionVersionId: number
  applicationFormVersionId?: number | null
  publicTitle: string
  opensAtUtc?: string | null
  closesAtUtc?: string | null
  maximumApplications?: number | null
  searchEngineVisible: boolean
}

export type RecruitmentJobDescriptionVersion = {
  id: number
  requisitionId: number
  clientId: number
  versionNumber: number
  title: string
  summary: string
  rolePurpose: string
  status: string
  workflowInstanceId?: number | null
  createdByUserId?: number
  approvedByUserId?: number | null
  approvedAtUtc?: string | null
  createdAtUtc?: string
  updatedAtUtc?: string
  responsibilities: RecruitmentJdResponsibility[]
  skills: RecruitmentJdSkillRequirement[]
  qualifications: RecruitmentJdQualificationRequirement[]
  certifications: RecruitmentJdCertificationRequirement[]
  languages: RecruitmentJdLanguageRequirement[]
  benefits: RecruitmentJdBenefit[]
}

export type SaveRecruitmentJobDescriptionVersion = Pick<RecruitmentJobDescriptionVersion,
  'id' | 'requisitionId' | 'title' | 'summary' | 'rolePurpose' | 'responsibilities' | 'skills' |
  'qualifications' | 'certifications' | 'languages' | 'benefits'>

export type RecruitmentJdResponsibility = {
  id: number
  jobDescriptionVersionId: number
  responsibilityText: string
  displayOrder: number
}

export type RecruitmentJdSkillRequirement = {
  id: number
  jobDescriptionVersionId: number
  skillId?: number | null
  skillName: string
  isRequired: boolean
  minimumYears: number
  minimumProficiency: string
  weightPercent: number
  displayOrder: number
}

export type RecruitmentJdQualificationRequirement = {
  id: number
  jobDescriptionVersionId: number
  qualificationName: string
  specialization: string
  isMandatory: boolean
  displayOrder: number
}

export type RecruitmentJdCertificationRequirement = {
  id: number
  jobDescriptionVersionId: number
  certificationName: string
  isMandatory: boolean
  displayOrder: number
}

export type RecruitmentJdLanguageRequirement = {
  id: number
  jobDescriptionVersionId: number
  languageName: string
  proficiency: string
  isMandatory: boolean
  displayOrder: number
}

export type RecruitmentJdBenefit = {
  id: number
  jobDescriptionVersionId: number
  benefitName: string
  description: string
  displayOrder: number
}

export type RecruitmentPositionPipelineAssignment = {
  id: number
  positionId: number
  jobPostingId?: number | null
  pipelineVersionId: number
  isActive: boolean
  assignedByUserId?: number
  assignedAtUtc?: string
}

export type AssignRecruitmentPipelineRequest = {
  positionId: number
  jobPostingId?: number | null
  pipelineVersionId: number
}

export type PublicRecruitmentJob = {
  postingId: number
  publicSlug: string
  publicTitle: string
  positionCode: string
  positionTitle: string
  clientName: string
  department: string
  jobLocation: string
  employmentType: string
  workMode: string
  summary: string
  rolePurpose: string
  closesAtUtc?: string | null
  applicationForm?: DynamicFormVersion | null
  responsibilities: Array<{ id: number; responsibilityText: string; displayOrder: number }>
  skills: Array<{ id: number; skillName: string; isRequired: boolean; minimumYears: number; minimumProficiency: string; weightPercent: number; displayOrder: number }>
  qualifications: Array<{ id: number; qualificationName: string; specialization: string; isMandatory: boolean; displayOrder: number }>
}

export type StartPublicApplicationRequest = {
  email: string
  phone: string
  idempotencyKey: string
  consentAccepted: boolean
}

export type PublicApplicationSession = {
  sessionToken: string
  submissionId: number
  expiresAtUtc: string
  status: string
}

export type PublicFormValue = {
  fieldId: number
  textValue?: string | null
  integerValue?: number | null
  decimalValue?: number | null
  dateValue?: string | null
  dateTimeValue?: string | null
  booleanValue?: boolean | null
  selectedOptionIds?: number[]
  selectedOptionValues?: string[]
}

export type PublicUploadedFile = {
  fieldId: number
  attachmentPublicId?: string
  publicId?: string
  originalFileName: string
  fileSizeBytes?: number
  uploadedAtUtc?: string
}

export type RecruitmentCandidateActionSession = {
  id: number
  clientId: number
  applicationId: number
  candidateId: number
  pipelineStageInstanceId?: number | null
  formVersionId?: number | null
  formSubmissionId?: number | null
  offerId?: number | null
  purposeCode: string
  status: string
  instructions: string
  expiresAtUtc: string
  maximumUses: number
  useCount: number
  createdByUserId: number
  createdAtUtc: string
  completedAtUtc?: string | null
  revokedAtUtc?: string | null
  candidateName: string
  positionTitle: string
  actionToken: string
}

export type PublicCandidateActionContext = {
  purposeCode: string
  purpose: 'DocumentRequest' | 'OfferResponse' | 'ProfileUpdate'
  candidateName: string
  positionTitle: string
  organizationName: string
  expiresAtUtc: string
  status: string
  instructions: string
  message: string
  allowSaveDraft: boolean
  form?: DynamicFormVersion | null
  existingValues: PublicFormValue[]
  uploadedFiles: PublicUploadedFile[]
  offer?: PublicCandidateOffer | null
}

export type PublicCandidateOffer = {
  id: number
  offerNumber: string
  offeredCtc: number
  currency: string
  proposedJoiningDate: string
  expiryDate?: string | null
  offerLetterAttachmentPublicId?: string | null
  documentUrl?: string | null
  status: string
}

export type CandidateActionDecision = 'Accepted' | 'Rejected' | 'Negotiation'
