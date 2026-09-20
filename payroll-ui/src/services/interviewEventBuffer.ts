// Payload-free, candidate-reported browser evidence only. Never queues answers, clipboard contents or credentials.
export const interviewBrowserEvents = new Set(['focus', 'blur', 'visibility-hidden', 'visibility-visible', 'fullscreen-exit', 'camera-off', 'camera-on', 'microphone-off', 'microphone-on', 'device-disconnected', 'connection-lost', 'reconnected', 'copy', 'paste', 'page-exit', 'page-reload'])
export type BufferedInterviewEvent = { eventKey: string; kind: string; offsetMs?: number; capturedAt: number }
type Options = {
  key: string; storage?: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>
  send: (event: BufferedInterviewEvent) => Promise<unknown>
  onStatus: (message: string) => void
  now?: () => number; uuid?: () => string
}
const maxAge = 4 * 60 * 60 * 1000
const maxEntries = 100
export class InterviewEventBuffer {
  private options: Options
  private queue: BufferedInterviewEvent[] = []
  private stopped = false
  private sending = false
  private retryAt = 0
  private lost = 0
  private persistent = true
  constructor(options: Options) {
    this.options = options
    try {
      const text = options.storage?.getItem(options.key)
      if (text && text.length <= 32768) {
        const parsed: unknown = JSON.parse(text)
        if (Array.isArray(parsed)) this.queue = parsed.filter((e): e is BufferedInterviewEvent => !!e && typeof e === 'object'
          && typeof e.eventKey === 'string' && /^[0-9a-f-]{36}$/i.test(e.eventKey) && interviewBrowserEvents.has(e.kind)
          && Number.isFinite(e.capturedAt) && e.capturedAt <= this.now() && this.now() - e.capturedAt < maxAge
          && (e.offsetMs === undefined || (Number.isInteger(e.offsetMs) && e.offsetMs >= 0 && e.offsetMs <= 86400000)))
          .slice(-maxEntries).map(e => ({ eventKey: e.eventKey, kind: e.kind, offsetMs: e.offsetMs, capturedAt: e.capturedAt }))
      }
    } catch { this.persistent = false }
    this.persist()
  }
  private now() { return this.options.now?.() ?? Date.now() }
  private persist() {
    try {
      if (!this.options.storage) { this.persistent = false; return }
      if (this.queue.length) this.options.storage.setItem(this.options.key, JSON.stringify(this.queue))
      else this.options.storage.removeItem(this.options.key)
    } catch { this.persistent = false }
  }
  private report() {
    this.options.onStatus(this.lost ? `${this.lost} browser event(s) could not be confirmed. Review-only evidence; no automatic hiring decision.`
      : this.queue.length ? `${this.queue.length} browser event(s) pending sync.${this.persistent ? ' Retrying when connected.' : ' Temporary memory only; browser storage is unavailable.'} No clipboard contents are stored.` : '')
  }
  enqueue(kind: string, offsetMs?: number) {
    if (this.stopped || !interviewBrowserEvents.has(kind)) return
    if (this.queue.length >= maxEntries) { this.lost++; this.report(); return }
    this.queue.push({ eventKey: this.options.uuid?.() ?? crypto.randomUUID(), kind, offsetMs, capturedAt: this.now() })
    this.persist(); this.report()
  }
  async flush() {
    if (this.stopped || this.sending || this.now() < this.retryAt || !this.queue.length) return
    this.sending = true
    try {
      // One event per tick: bounded below the server's per-actor admission limit.
      const event = this.queue[0]
      if (this.now() - event.capturedAt >= maxAge) { this.queue.shift(); this.lost++; return }
      await this.options.send(event)
      if (this.queue[0]?.eventKey === event.eventKey) this.queue.shift()
      this.retryAt = this.now() + 500
    } catch (error) {
      const status = typeof error === 'object' && error ? (error as { status?: number }).status : undefined
      if (status && [400, 401, 403, 404, 409, 410, 413].includes(status)) {
        this.lost += this.queue.length; this.queue = []; this.stopped = true
      } else this.retryAt = this.now() + (status === 429 ? 60000 : 5000)
    } finally { this.sending = false; this.persist(); this.report() }
  }
  close() { this.lost += this.queue.length; this.queue = []; this.stopped = true; this.persist(); this.report() }
  dispose() { this.stopped = true; this.persist() }
}
