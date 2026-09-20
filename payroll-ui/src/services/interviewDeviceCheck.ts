export type InterviewInputKind = 'camera' | 'microphone'
export type InterviewDevicePreferences = {
  cameraId: string; microphoneId: string; speakerId: string;
  cameraEnabled: boolean; microphoneEnabled: boolean;
}
export const defaultInterviewDevices: InterviewDevicePreferences = {
  cameraId: '', microphoneId: '', speakerId: '', cameraEnabled: true, microphoneEnabled: true,
}
export const supportsInterviewSpeakerSelection = () => typeof HTMLMediaElement !== 'undefined' &&
  'setSinkId' in HTMLMediaElement.prototype && typeof AudioContext !== 'undefined' && 'setSinkId' in AudioContext.prototype

export function interviewDeviceError(error: unknown) {
  const name = error && typeof error === 'object' && 'name' in error ? error.name : ''
  if (name === 'NotAllowedError' || name === 'SecurityError') return 'Permission blocked. Allow this site in browser camera/microphone settings, then retry.'
  if (name === 'NotFoundError') return 'No device found. Connect a camera or microphone, then retry.'
  if (name === 'NotReadableError' || name === 'AbortError') return 'Device is unavailable or in use. Close other camera/microphone apps, then retry.'
  if (name === 'OverconstrainedError') return 'Selected device is unavailable. Choose another device or System default.'
  return 'Device check unavailable. Use HTTPS (or localhost), check browser permissions and retry.'
}

// Browser-local preview only: no recording, network upload, storage or automatic permission request.
// Separate requests let a microphone work even if the camera is denied. Generation guards stop
// streams from late permission dialogs after Cancel, device changes, joining or unmount.
export class InterviewDeviceCheck {
  private streams: Partial<Record<InterviewInputKind, MediaStream>> = {}
  private generation = { camera: 0, microphone: 0 }
  private disposed = false
  private changed: (kind: InterviewInputKind, stream: MediaStream | null, error?: string) => void
  private acquire: (constraints: MediaStreamConstraints) => Promise<MediaStream>
  constructor(changed: (kind: InterviewInputKind, stream: MediaStream | null, error?: string) => void,
    acquire = (constraints: MediaStreamConstraints) => navigator.mediaDevices.getUserMedia(constraints)) {
    this.changed = changed; this.acquire = acquire
  }

  async start(kind: InterviewInputKind, deviceId = '') {
    if (this.disposed) return
    this.stop(kind)
    const generation = this.generation[kind]
    try {
      const device = deviceId ? { deviceId: { exact: deviceId } } : {}
      const stream = await this.acquire(kind === 'camera'
        ? { video: { ...device, width: { ideal: 640 }, height: { ideal: 360 } }, audio: false }
        : { audio: { ...device, echoCancellation: true, noiseSuppression: true }, video: false })
      if (this.disposed || generation !== this.generation[kind]) { stream.getTracks().forEach(track => track.stop()); return }
      this.streams[kind] = stream
      for (const track of stream.getTracks()) track.addEventListener('ended', () => {
        if (this.disposed || this.streams[kind] !== stream) return
        this.stop(kind)
        this.changed(kind, null, 'Device disconnected or permission revoked. Reconnect it and check again.')
      }, { once: true })
      this.changed(kind, stream)
    } catch (error) {
      if (!this.disposed && generation === this.generation[kind]) this.changed(kind, null, interviewDeviceError(error))
    }
  }
  stop(kind: InterviewInputKind) {
    this.generation[kind]++
    const stream = this.streams[kind]; delete this.streams[kind]
    stream?.getTracks().forEach(track => track.stop())
    if (!this.disposed) this.changed(kind, null)
  }
  stopAll() { this.stop('camera'); this.stop('microphone') }
  dispose() { this.disposed = true; this.stopAll() }
}

export function interviewMicrophoneLevel(samples: Float32Array): number {
  if (!samples.length) return 0
  const rms = Math.sqrt(samples.reduce((sum, value) => sum + value * value, 0) / samples.length)
  return Math.min(100, Math.max(0, Math.round(rms * 350)))
}

// No connection to context.destination: preview never echoes the user's microphone.
export function watchInterviewMicrophone(stream: MediaStream, changed: (level: number) => void, failed: () => void) {
  const context = new AudioContext()
  const source = context.createMediaStreamSource(stream), analyser = context.createAnalyser()
  analyser.fftSize = 1024; source.connect(analyser)
  const samples = new Float32Array(analyser.fftSize)
  let stopped = false
  void context.resume().catch(() => { if (!stopped) failed() })
  const timer = window.setInterval(() => {
    if (stopped) return
    if (context.state === 'suspended') { failed(); return }
    analyser.getFloatTimeDomainData(samples); changed(interviewMicrophoneLevel(samples))
  }, 100)
  return () => { stopped = true; window.clearInterval(timer); source.disconnect(); analyser.disconnect(); void context.close().catch(() => {}) }
}

export class InterviewSpeakerCheck {
  private context = new AudioContext()
  private stopped = false
  async play(deviceId: string, ended: () => void) {
    try {
      // Resume from the actual click before awaiting device selection.
      await this.context.resume()
      if (deviceId) {
        const sink = this.context as AudioContext & { setSinkId?: (id: string) => Promise<void> }
        if (!sink.setSinkId) throw new Error('Choose your speaker in system settings; this browser cannot route test audio.')
        await sink.setSinkId(deviceId)
      }
      if (this.stopped) return
      const oscillator = this.context.createOscillator(), gain = this.context.createGain(), now = this.context.currentTime
      oscillator.frequency.value = 440
      gain.gain.setValueAtTime(0, now); gain.gain.linearRampToValueAtTime(0.08, now + 0.05)
      gain.gain.setValueAtTime(0.08, now + 0.65); gain.gain.linearRampToValueAtTime(0, now + 0.8)
      oscillator.connect(gain); gain.connect(this.context.destination)
      oscillator.onended = () => { if (!this.stopped) { this.stop(); ended() } }
      oscillator.start(); oscillator.stop(now + 0.85)
    } catch (error) { this.stop(); throw error }
  }
  stop() { if (!this.stopped) { this.stopped = true; void this.context.close().catch(() => {}) } }
}
