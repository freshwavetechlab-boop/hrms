export type InterviewVoicePublisher = (track: MediaStreamTrack) => Promise<() => Promise<void>>
export const interviewerVoiceTrackName = 'hrms-interviewer-voice'
export const isInterviewMicrophone = (publication: { source: string; trackName: string }) => publication.source === 'microphone' && publication.trackName !== interviewerVoiceTrackName

// One bounded local speech clip, heard locally and optionally shared in the existing room.
// No new provider, microphone permission, room grant or public media URL.
export class InterviewVoicePlayback {
  private readonly context: AudioContext
  private readonly output: MediaStreamAudioDestinationNode
  private source?: AudioBufferSourceNode
  private unpublish?: () => Promise<void>
  private disposed = false
  private started = false

  constructor() {
    if (typeof AudioContext === 'undefined') throw new Error('Local voice playback is unavailable in this browser. Read the question on screen.')
    this.context = new AudioContext()
    this.output = this.context.createMediaStreamDestination()
  }
  resume() { return this.context.resume() } // call from the user gesture before awaiting synthesis
  async play(blob: Blob, publish: InterviewVoicePublisher | undefined, onEnded: () => void): Promise<boolean> {
    if (this.started || this.disposed) return false
    this.started = true
    try {
      const buffer = await this.context.decodeAudioData(await blob.arrayBuffer())
      if (this.disposed) return false
      if (publish) {
        const release = await publish(this.output.stream.getAudioTracks()[0])
        if (this.disposed) { await release(); return false }
        this.unpublish = release
      }
      this.source = this.context.createBufferSource()
      this.source.buffer = buffer
      this.source.connect(this.context.destination) // candidate speaker
      this.source.connect(this.output) // panel + consented room recording, if joined
      this.source.onended = () => { this.stop(); onEnded() }
      this.source.start()
      return !!publish
    } catch (error) { this.stop(); throw error }
  }
  stop() {
    if (this.disposed) return
    this.disposed = true
    if (this.source) { this.source.onended = null; try { this.source.stop() } catch { /* not started */ }; this.source.disconnect() }
    this.output.stream.getTracks().forEach(track => track.stop())
    this.output.disconnect()
    const release = this.unpublish; this.unpublish = undefined
    if (release) void release().catch(() => {}) // disconnected room will also remove its tracks
    void this.context.close().catch(() => {})
  }
}
