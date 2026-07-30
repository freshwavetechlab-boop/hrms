export type CommunicationChannel = 'Email' | 'Sms' | 'WhatsApp'
export type CommunicationSelectionMode = 'SelectedEmployees' | 'AllFiltered'
export type CommunicationCampaignStatus = 'Draft' | 'Queued' | 'Processing' | 'Sent' | 'PartiallySent' | 'Failed'
export type CommunicationRecipientStatus = 'Pending' | 'Queued' | 'Processing' | 'Sent' | 'Delivered' | 'Read' | 'Failed' | 'Excluded'
export type CommunicationConversationStatus = 'Open' | 'Closed'

export type EmployeeCommunicationRecipient = {
  employeeId: number
  clientId: number
  clientName: string
  employeeCode: string
  employeeName: string
  workEmail: string
  mobile: string
  department: string
  designation: string
  workLocationId: number
  workLocationName: string
  isActive: boolean
}

export type EmployeeCommunicationRecipientResult = {
  items: EmployeeCommunicationRecipient[]
  total: number
  emailReadyCount: number
  mobileReadyCount: number
}

export type CommunicationTemplate = {
  id: number
  clientId?: number | null
  clientName?: string
  channel: CommunicationChannel
  code: string
  name: string
  subjectTemplate: string
  bodyTemplate: string
  providerTemplateCode?: string | null
  languageCode?: string | null
  isHtml: boolean
  isActive: boolean
  createdAtUtc?: string
  updatedAtUtc?: string
  variables?: CommunicationTemplateVariable[]
}

export type CommunicationTemplateVariable = {
  id: number
  templateId: number
  position: number
  variableKey: string
  label: string
  sourceCode: string
  isRequired: boolean
  fallbackValue: string
}

export type EmployeeCommunicationSelection = {
  clientId: number
  draftId?: number | null
  channel: CommunicationChannel
  templateId?: number | null
  subject: string
  body: string
  selectionMode: CommunicationSelectionMode
  employeeIds: number[]
  toEmployeeIds: number[]
  ccEmployeeIds: number[]
  bccEmployeeIds: number[]
  excludedEmployeeIds: number[]
  search: string
  workLocationIds: number[]
  departments: string[]
  designations: string[]
}

export type EmployeeCommunicationPreviewRecipient = {
  employeeId: number
  recipientType: 'To' | 'Cc' | 'Bcc'
  employeeCode: string
  employeeName: string
  destination: string
  isEligible: boolean
  exclusionReason: string
  subjectPreview: string
  bodyPreview: string
}

export type EmployeeCommunicationPreview = {
  selectedCount: number
  eligibleCount: number
  excludedCount: number
  missingDestinationCount: number
  duplicateDestinationCount: number
  sampleSubject: string
  sampleBody: string
  recipients: EmployeeCommunicationPreviewRecipient[]
  warnings: string[]
  canSend: boolean
}

export type EmployeeCommunicationCampaign = {
  id: number
  clientId: number
  clientName?: string
  channel: CommunicationChannel
  templateId?: number | null
  templateName?: string | null
  selectionMode: CommunicationSelectionMode
  subjectSnapshot: string
  bodySnapshot: string
  totalSelected: number
  totalEligible: number
  totalExcluded: number
  totalQueued?: number
  totalSent: number
  totalDelivered: number
  totalRead: number
  totalFailed: number
  status: CommunicationCampaignStatus
  createdByUserId?: number
  createdByName?: string
  createdAtUtc: string
  startedAtUtc?: string | null
  completedAtUtc?: string | null
  recipients?: EmployeeCommunicationCampaignRecipient[]
}

export type EmployeeCommunicationCampaignRecipient = {
  id: number
  campaignId: number
  employeeId: number
  recipientType: 'To' | 'Cc' | 'Bcc'
  employeeCode: string
  employeeName: string
  destination: string
  renderedSubject: string
  renderedBody: string
  status: CommunicationRecipientStatus
  providerMessageId?: string | null
  retryCount: number
  errorCode?: string | null
  errorMessage?: string | null
  exclusionReason?: string | null
  queuedAtUtc?: string | null
  sentAtUtc?: string | null
  deliveredAtUtc?: string | null
  readAtUtc?: string | null
  createdAtUtc?: string | null
  attempts?: CommunicationDeliveryAttempt[]
  events?: CommunicationDeliveryEvent[]
}

export type CommunicationDeliveryAttempt = {
  id: number
  attemptNumber: number
  httpStatusCode?: number | null
  providerRequestId?: string | null
  isSuccess?: boolean
  errorCode?: string | null
  errorMessage?: string | null
  attemptedAtUtc: string
}

export type CommunicationDeliveryEvent = {
  id: number
  eventStatus: CommunicationRecipientStatus
  occurredAtUtc: string
}

export type EmployeeCommunicationCampaignPage = {
  items: EmployeeCommunicationCampaign[]
  total: number
  page: number
  pageSize: number
}

export type EmployeeConversation = {
  id: number
  clientId: number
  employeeId: number
  employeeName: string
  employeeCode: string
  channel: CommunicationChannel
  destination: string
  lastMessagePreview: string
  lastMessageAtUtc?: string | null
  unreadCount: number
  status: CommunicationConversationStatus
  assignedUserId?: number | null
  assignedUserName?: string | null
}

export type EmployeeConversationMessage = {
  id: number
  conversationId: number
  campaignRecipientId?: number | null
  direction: 'Outbound' | 'Inbound'
  messageType: 'Text' | 'Html' | 'Media'
  subject?: string
  body: string
  status: CommunicationRecipientStatus
  providerMessageId?: string | null
  errorMessage?: string | null
  sentAtUtc?: string | null
  deliveredAtUtc?: string | null
  readAtUtc?: string | null
  receivedAtUtc?: string | null
  createdAtUtc: string
  attachments: CommunicationMessageAttachment[]
}

export type CommunicationMessageAttachment = {
  id: number
  messageId: number
  entityAttachmentId: number
  publicId: string
  fileName: string
  contentType: string
  fileSizeBytes: number
}

export type EmployeeCommunicationDraft = {
  id: number
  clientId: number
  channel: CommunicationChannel
  status: 'Draft' | 'Consumed'
  createdByUserId: number
  createdAtUtc: string
  updatedAtUtc: string
  consumedAtUtc?: string | null
}

export type EmployeeConversationDetail = EmployeeConversation & {
  messages: EmployeeConversationMessage[]
}

export type EmployeeConversationPage = {
  items: EmployeeConversation[]
  total: number
}
