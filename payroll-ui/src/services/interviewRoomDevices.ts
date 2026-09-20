import type { Room } from 'livekit-client'
import type { InterviewDevicePreferences } from './interviewDeviceCheck'
import { isInterviewMicrophone } from './interviewVoicePlayback'

export function startInterviewRoomDevices(room: Room, selected: InterviewDevicePreferences) {
  // Separate results: a denied camera must not discard a functioning microphone.
  return Promise.allSettled([
    selected.cameraEnabled ? room.localParticipant.setCameraEnabled(true) : Promise.resolve(),
    selected.microphoneEnabled ? room.localParticipant.setMicrophoneEnabled(true) : Promise.resolve(),
    selected.speakerId ? room.switchActiveDevice('audiooutput', selected.speakerId) : Promise.resolve(),
  ])
}

export async function switchInterviewRoomDevice(room: Room, kind: MediaDeviceKind, id: string) {
  if (kind === 'audioinput') {
    // LiveKit's room-wide audioinput change also visits the separate TTS publication.
    // Change only the human mic. Its SDK setDeviceId preserves mute/pending-device state.
    const publication = [...room.localParticipant.trackPublications.values()].find(isInterviewMicrophone)
    if (publication?.track) {
      const switched = await publication.track.setDeviceId(id ? { exact: id } : '')
      if (id && !switched) throw new Error('Selected microphone unavailable')
    }
  } else {
    const switched = await room.switchActiveDevice(kind, id, !!id)
    // Empty device ID selects system default; its physical ID need not equal an empty string.
    if (id && !switched) throw new Error('Selected device unavailable')
  }
}
