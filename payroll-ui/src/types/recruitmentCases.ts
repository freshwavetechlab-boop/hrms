export type RecruitmentWorkOrderLine = {
  id: number
  workOrderId: number
  lineNumber: number
  positionName: string
  payBandLevelCode: string
  numberOfPositions: number
  location: string
  division: string
  requisitionId?: number | null
  positionId?: number | null
  status: string
  createdAtUtc?: string
  updatedAtUtc?: string
}

export type RecruitmentWorkOrder = {
  id: number
  clientId: number
  clientName: string
  workOrderNumber: string
  receivedAtUtc: string
  receivedFrom: string
  subject: string
  remarks: string
  status: string
  overallSlaMinutes: number
  dueAtUtc?: string | null
  lineCount: number
  openCaseCount: number
  createdByUserId?: number
  createdAtUtc?: string
  updatedAtUtc?: string
  lines: RecruitmentWorkOrderLine[]
}

export type SaveRecruitmentWorkOrder = Omit<RecruitmentWorkOrder,
  'clientName' | 'dueAtUtc' | 'lineCount' | 'openCaseCount' | 'createdByUserId' | 'createdAtUtc' | 'updatedAtUtc' | 'lines'> & {
    lines: Array<Omit<RecruitmentWorkOrderLine, 'workOrderId' | 'createdAtUtc' | 'updatedAtUtc'>>
  }

export type RecruitmentHiringCasePause = {
  id: number
  positionStageInstanceId: number
  reason: string
  pausedByUserId: number
  pausedByName: string
  pausedAtUtc: string
  resumedByUserId?: number | null
  resumedByName: string
  resumedAtUtc?: string | null
  durationSeconds: number
}

export type RecruitmentHiringCaseStage = {
  id: number
  hiringCaseId: number
  pipelineStageId: number
  stageCode: string
  stageName: string
  displayOrder: number
  stakeholderCode: string
  targetOffsetMinutes?: number | null
  allowPause: boolean
  pauseBehavior: string
  requiresApproval: boolean
  approvalWorkflowId?: number | null
  isTerminal: boolean
  status: string
  enteredAtUtc?: string | null
  dueAtUtc?: string | null
  completedAtUtc?: string | null
  pausedDurationSeconds: number
  isPaused: boolean
  isSlaBreached: boolean
  pauseHistory: RecruitmentHiringCasePause[]
  processDocumentRequirements: import('./recruitmentOrchestration').RecruitmentStageProcessDocumentRequirement[]
}

export type RecruitmentHiringCase = {
  id: number
  clientId: number
  clientName: string
  workOrderId: number
  workOrderNumber: string
  workOrderLineId: number
  requisitionId?: number | null
  positionId?: number | null
  positionName: string
  payBandLevelCode: string
  division: string
  pipelineVersionId: number
  pipelineName: string
  slaAnchorAtUtc: string
  overallDueAtUtc?: string | null
  currentStageInstanceId?: number | null
  currentStageName: string
  currentStakeholderCode: string
  status: string
  advanceStatus: string
  advanceRequestId?: number | null
  advanceMessage: string
  startedAtUtc: string
  updatedAtUtc: string
  stages: RecruitmentHiringCaseStage[]
}

export type RecruitmentProcessDocument = {
  id: number
  clientId: number
  hiringCaseId?: number | null
  applicationId?: number | null
  interviewId?: number | null
  pipelineStageId?: number | null
  documentType: string
  versionNumber: number
  templateId?: number | null
  attachmentPublicId?: string | null
  hasFinalSignedAttachment: boolean
  status: string
  workflowInstanceId?: number | null
  createdByUserId: number
  signedByUserId?: number | null
  signedAtUtc?: string | null
  createdAtUtc: string
  updatedAtUtc: string
}

export type SaveRecruitmentProcessDocument = Omit<RecruitmentProcessDocument,
  'versionNumber' | 'hasFinalSignedAttachment' | 'createdByUserId' | 'signedByUserId' | 'signedAtUtc' | 'createdAtUtc' | 'updatedAtUtc'>

export type RecruitmentProfileSubmissionBatchItem = {
  id: number
  batchId: number
  applicationId: number
  candidateId: number
  candidateName: string
  applicationScoreId?: number | null
  atsScore?: number | null
  readinessStatus: string
  missingFields: string
}

export type RecruitmentProfileBatchNotificationDelivery = {
  id: number
  batchId: number
  stageActionId: number
  recipientType: string
  recipientEmail: string
  notificationQueueId: number
  createdAtUtc: string
}

export type RecruitmentProfileSubmissionBatch = {
  id: number
  clientId: number
  hiringCaseId: number
  batchNumber: string
  status: string
  createdByUserId: number
  approvedByUserId?: number | null
  approvedAtUtc?: string | null
  forwardedByUserId?: number | null
  forwardedAtUtc?: string | null
  createdAtUtc: string
  updatedAtUtc: string
  items: RecruitmentProfileSubmissionBatchItem[]
  deliveries: RecruitmentProfileBatchNotificationDelivery[]
}
