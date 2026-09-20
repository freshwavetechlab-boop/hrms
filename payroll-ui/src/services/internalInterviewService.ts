import { apiRequest, apiUrl, getJson, getJsonResult, postJson } from './apiClient'
import type { InternalInterviewConfiguration, InternalInterviewContext, InternalInterviewEvent, InternalInterviewView, InterviewMediaGrant, InterviewQuestion } from '../types/internalInterviews'

export const internalInterviewRoot = '/api/recruitment/internal-interviews'
export const getInternalInterviewCapabilities = () => getJson(`${internalInterviewRoot}/capabilities`, { enabled: false, canManage: false, acceptingNewSessions: false }, { loader: false, toast: false })
export const getInternalInterviewContext = (id: number) => getJsonResult(`${internalInterviewRoot}/${id}/context`, null as InternalInterviewContext | null, { loader: false, toast: false })
export const getInterviewQuestions = (positionId: number) => getJsonResult(`${internalInterviewRoot}/questions?positionId=${positionId}`, [] as InterviewQuestion[], { loader: false, toast: false })
export const saveInterviewQuestion = (question: InterviewQuestion) => postJson(`${internalInterviewRoot}/questions`, question, null as InterviewQuestion | null, { loader: false })
export const configureInternalInterview = (id: number, configuration: InternalInterviewConfiguration) => postJson(`${internalInterviewRoot}/${id}/configuration`, configuration, null as InternalInterviewView | null, { loader: false })
export const issueInterviewLink = (id: number) => postJson(`${internalInterviewRoot}/${id}/link`, {}, null as { url: string; expiresAtUtc: string } | null, { loader: false })

export class InterviewSessionError extends Error {
  readonly status: number
  constructor(message: string, status: number) { super(message); this.status = status }
}

export class InterviewSessionClient {
  readonly id: number
  private token?: string
  constructor(id: number, token?: string) { this.id = id; this.token = token }
  get root() { return this.token === undefined ? `${internalInterviewRoot}/${this.id}` : `/api/public/internal-interviews/${this.id}` }
  private async request<T>(path: string, body?: unknown): Promise<T> {
    const headers = new Headers({ 'Content-Type': 'application/json' })
    if (this.token !== undefined) headers.set('X-Interview-Token', this.token)
    const options = { method: body === undefined ? 'GET' : 'POST', headers, body: body === undefined ? undefined : JSON.stringify(body) }
    // Candidate authorization is independent of admin cookies/token; never log out a signed-in HR user.
    const response = this.token === undefined
      ? await apiRequest(this.root + path, { ...options, loader: false, timeoutMs: path === '/command' ? 45000 : 15000 })
      : await fetch(apiUrl(this.root + path), { ...options, credentials: 'omit', signal: AbortSignal.timeout(15000) })
    const data = await response.json().catch(() => ({}))
    if (!response.ok) throw new InterviewSessionError(data.error || `Interview request failed (${response.status}).`, response.status)
    return data as T
  }
  status() { return this.request<InternalInterviewView>('') }
  events(after = 0) { return this.request<InternalInterviewEvent[]>(`/events?after=${after}`) }
  consent(recording: boolean, transcription: boolean, noticeVersion: string) { return this.request<InternalInterviewView>('/consent', { recording, transcription, noticeVersion }) }
  command(action: string, revision: number, sectionSkill?: string) { return this.request<InternalInterviewView>('/command', { action, revision, sectionSkill }) }
  resetSchedule(revision: number) { return this.request<InternalInterviewView>('/schedule-reset', { revision }) }
  join() { return this.request<InterviewMediaGrant>('/join', {}) }
  retryAi() { return this.request<{ message: string }>('/ai-retry', {}) }
  recordings() { return this.request<InterviewRecording[]>('/recordings') }
  async recordingSource(recordingId: string) {
    if (this.token !== undefined) throw new Error('Recording replay requires a panel/HR login.')
    const url = apiUrl(`${this.root}/recordings/${encodeURIComponent(recordingId)}`)
    // Native video sends the existing HttpOnly login cookie, not an access token in the URL.
    // Probe one byte using that exact cookie path; a legacy bearer-only session must sign in again.
    const response = await fetch(url, { credentials: 'include', headers: { Range: 'bytes=0-0' }, signal: AbortSignal.timeout(15000) })
    await response.body?.cancel()
    if ([401, 403].includes(response.status)) throw new Error('Sign in to HRMS again and allow the API login cookie to replay this protected recording.')
    if (response.status !== 206) throw new Error('Protected recording byte-range access could not be verified. Check recording availability and the API/proxy range configuration.')
    return url
  }
  async transcribe(questionId: number, audio: Blob, signal?: AbortSignal) {
    if (this.token === undefined) throw new Error('Candidate authorization is required for voice input.')
    const response = await fetch(apiUrl(`${this.root}/speech/transcribe/${questionId}`), { method: 'POST', credentials: 'omit',
      headers: { 'X-Interview-Token': this.token, 'Content-Type': audio.type.split(';')[0] }, body: audio, signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(100000)]) : AbortSignal.timeout(100000) })
    const data = await response.json().catch(() => ({}))
    if (!response.ok) throw new InterviewSessionError(data.error || 'Voice parsing failed. No answer was submitted.', response.status)
    return data as { text: string; draft: boolean }
  }
  async speak(eventId: number, signal?: AbortSignal) {
    if (this.token === undefined) throw new Error('Candidate authorization is required for local voice.')
    const response = await fetch(apiUrl(`${this.root}/speech/question/${eventId}`), { credentials: 'omit', headers: { 'X-Interview-Token': this.token }, signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(100000)]) : AbortSignal.timeout(100000) })
    if (!response.ok) { const data = await response.json().catch(() => ({})); throw new InterviewSessionError(data.error || 'Local voice is unavailable. Read the question on screen.', response.status) }
    return response.blob()
  }
  event(kind: string, text = '', questionEventId?: number, offsetMs?: number, eventKey: string = crypto.randomUUID()) {
    return this.request<InternalInterviewEvent>('/events', { eventKey, kind, text, questionEventId, offsetMs })
  }
}
export type InterviewRecording = { id: string; interviewId: number; status: string; contentType: string; sizeBytes: number; startedAtUtc: string; endedAtUtc?: string }
