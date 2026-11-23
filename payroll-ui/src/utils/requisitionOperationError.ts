export type RequisitionOperationStage = 'linked-jd' | 'save' | 'source-configuration' | 'source-upload' | 'submit' | 'refresh'
export type RequisitionRequestFailure = { error?: string; status?: number; requestId?: string; correlationId?: string; requestSent?: boolean }
export type RequisitionOperationError = { title: string; message: string; diagnostics: string }
type SavedRequestReference = { id?: number; rfrNumber?: string }

const stepNames: Record<RequisitionOperationStage, string> = {
  'linked-jd': 'Linked JD', save: 'Hiring request save', 'source-configuration': 'Source attachment setup',
  'source-upload': 'Source document upload', submit: 'Approval submission', refresh: 'Request list refresh',
}
const endpoints = (stage: RequisitionOperationStage, id: number) => ({
  'linked-jd': 'POST /api/recruitment-orchestration/job-descriptions',
  save: 'POST /api/recruitment/requisitions',
  'source-configuration': 'GET /api/attachment-configurations/effective',
  'source-upload': 'POST /api/attachments',
  submit: id > 0 ? `POST /api/recruitment/requisitions/${id}/submit` : 'POST /api/recruitment/requisitions/{id}/submit',
  refresh: 'GET /api/recruitment/requisitions',
}[stage])

export function requisitionOperationError(stage: RequisitionOperationStage, failure: RequisitionRequestFailure, saved: SavedRequestReference = {}): RequisitionOperationError {
  const raw = String(failure.error || '').trim()
  const network = failure.status === 0 || /failed to fetch|networkerror|network request failed|load failed/i.test(raw)
  const uncertainWrite = ['linked-jd', 'save', 'submit'].includes(stage) && failure.requestSent !== false && (network || Boolean(failure.status && (failure.status >= 200 && failure.status < 300 || failure.status >= 500)))
  const reference = saved.rfrNumber?.trim() || (saved.id ? `#${saved.id}` : '')
  const savedPrefix = reference ? `Hiring request ${reference} is saved.` : 'The hiring request is saved.'
  const detail = failure.requestSent === false ? raw || 'Correct the linked JD fields before saving.'
    : network ? /timed? ?out|timeout/i.test(raw) ? 'The API did not confirm a result before the request timed out.' : 'The API connection ended before a response was received.'
      : raw || 'The API did not return a usable result.'
  let title: string, message: string
  if (stage === 'linked-jd') {
    title = failure.requestSent === false ? 'Linked JD needs correction' : uncertainWrite ? 'Linked JD save was not confirmed' : 'Linked JD could not be saved'
    message = `${detail} The hiring-request save has not started. Your field values remain in this editor. ${uncertainWrite ? 'Check the linked JD status before retrying.' : 'Review the JD fields or the error below before retrying.'}`
  } else if (stage === 'save') {
    title = uncertainWrite ? 'Hiring-request save was not confirmed' : 'Hiring request could not be saved'
    message = `${detail} Your field values remain in this editor. ${uncertainWrite ? 'Check the request list before retrying because the server may have saved the request. Do not create another request.' : 'Correct the reported issue, then save this same request again.'}`
  } else if (stage === 'source-configuration' || stage === 'source-upload') {
    title = 'Request saved; source document needs attention'
    message = `${savedPrefix} ${detail} The source file is still selected in this editor. Refresh the attachment status, then retry this same request. Submission has not started.`
  } else if (stage === 'submit') {
    title = uncertainWrite ? 'Request saved; submission was not confirmed' : 'Request saved; submission needs attention'
    message = `${savedPrefix} ${detail} ${uncertainWrite ? 'Check this request’s current approval status before retrying submission.' : 'Correct the reported issue, then submit this same request.'} Do not create a duplicate request.`
  } else {
    title = 'Request saved; list refresh failed'
    message = `${savedPrefix} ${detail} Refresh the request list to see its latest status. You do not need to save another copy.`
  }
  const requestId = [failure.requestId, failure.correlationId].find(value => typeof value === 'string' && /^[A-Za-z0-9_.:-]{1,150}$/.test(value))
  const diagnostics = [stepNames[stage], failure.requestSent === false ? 'Request not sent' : endpoints(stage, Number(saved.id || 0)), failure.status ? `HTTP ${failure.status}` : network ? 'No HTTP response' : '', requestId ? `Reference: ${requestId}` : ''].filter(Boolean).join(' · ')
  return { title, message, diagnostics }
}
