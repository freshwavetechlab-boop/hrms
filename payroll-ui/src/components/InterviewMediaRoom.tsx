import { useEffect, useRef, useState } from 'react'
import { Button, Space, Tag } from 'antd'
import { Room, RoomEvent, Track, createLocalAudioTrack, type Participant } from 'livekit-client'
import type { InterviewMediaGrant } from '../types/internalInterviews'
import { interviewerVoiceTrackName, isInterviewMicrophone, type InterviewVoicePublisher } from '../services/interviewVoicePlayback'

const microphoneEnabled = (participant: Participant) => [...participant.trackPublications.values()].some(publication => isInterviewMicrophone(publication) && !publication.isMuted)

function ParticipantMedia({ participant, revision }: { participant: Participant; revision: number }) {
  const target = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const elements: HTMLMediaElement[] = []
    for (const publication of participant.trackPublications.values()) {
      if (!publication.track || (participant.isLocal && publication.kind === 'audio')) continue
      const element = publication.track.attach()
      element.autoplay = true
      if (element instanceof HTMLVideoElement) { element.playsInline = true; element.muted = participant.isLocal }
      target.current?.appendChild(element); elements.push(element)
    }
    return () => { for (const publication of participant.trackPublications.values()) for (const element of elements) publication.track?.detach(element); elements.forEach(element => element.remove()) }
  }, [participant, revision])
  return <section className="interview-participant"><div ref={target} /><p>{participant.isLocal ? 'You' : participant.identity === 'candidate' ? 'Candidate' : 'Panel member'} · {participant.isCameraEnabled ? 'Camera on' : 'Camera off'} · {microphoneEnabled(participant) ? 'Mic on' : 'Mic off'}</p></section>
}

export default function InterviewMediaRoom({ grant, onLeave, onEvent, onError, onVoicePublisher }: {
  grant: InterviewMediaGrant; onLeave: () => void; onEvent: (kind: string) => void; onError: (error: string) => void;
  onVoicePublisher: (publisher: InterviewVoicePublisher | undefined) => void;
}) {
  const [room] = useState(() => new Room({ adaptiveStream: true, dynacast: true }))
  const [revision, setRevision] = useState(0)
  const [connection, setConnection] = useState('connecting')
  const [deviceBusy, setDeviceBusy] = useState(false)
  const deviceChanging = useRef(false)
  const callbacks = useRef({ onLeave, onEvent, onError, onVoicePublisher }); callbacks.current = { onLeave, onEvent, onError, onVoicePublisher }
  useEffect(() => {
    let active = true
    const refresh = () => { if (active) setRevision(value => value + 1) }
    const changed = (state: string) => {
      if (!active) return
      setConnection(state); refresh()
      callbacks.current.onVoicePublisher(state === 'connected' ? async track => {
        if (!active || room.state !== 'connected') throw new Error('Call disconnected before interviewer voice could be shared. Rejoin and retry.')
        // SDK supports multiple audio publications. Keep this name separate from the real mic,
        // and publish only under the already allowed microphone source; no broader room grant.
        const publication = await room.localParticipant.publishTrack(track, { source: Track.Source.Microphone, name: interviewerVoiceTrackName, dtx: false })
        if (!active || room.state !== 'connected') { if (publication.track) await room.localParticipant.unpublishTrack(publication.track); throw new Error('Call disconnected while sharing interviewer voice.') }
        return async () => { if (publication.track) await room.localParticipant.unpublishTrack(publication.track) }
      } : undefined)
      if (state === 'reconnecting' || state === 'disconnected') callbacks.current.onEvent('connection-lost')
      if (state === 'connected') callbacks.current.onEvent('reconnected')
    }
    room.on(RoomEvent.ConnectionStateChanged, changed)
    room.on(RoomEvent.ParticipantConnected, refresh).on(RoomEvent.ParticipantDisconnected, refresh)
    room.on(RoomEvent.TrackSubscribed, refresh).on(RoomEvent.TrackUnsubscribed, refresh)
    room.on(RoomEvent.TrackMuted, refresh).on(RoomEvent.TrackUnmuted, refresh)
    room.on(RoomEvent.LocalTrackPublished, refresh).on(RoomEvent.LocalTrackUnpublished, refresh)
    room.on(RoomEvent.MediaDevicesError, () => { callbacks.current.onEvent('device-disconnected'); callbacks.current.onError('Camera/microphone unavailable. Check device permissions and connections.') })
    void room.connect(grant.url, grant.token).then(async () => {
      if (!active) { await room.disconnect(); return }
      await room.localParticipant.enableCameraAndMicrophone(); refresh()
    }).catch(() => { if (active) callbacks.current.onError('Unable to join media. Check camera/microphone permissions and the self-hosted video server, then leave and retry.') })
    return () => { active = false; callbacks.current.onVoicePublisher(undefined); room.removeAllListeners(); void room.disconnect() }
  }, [room, grant.url, grant.token])
  const toggle = async (kind: 'camera' | 'microphone') => {
    if (deviceChanging.current || room.state !== 'connected') return
    deviceChanging.current = true; setDeviceBusy(true)
    try {
      if (kind === 'camera') { const next = !room.localParticipant.isCameraEnabled; await room.localParticipant.setCameraEnabled(next); onEvent(next ? 'camera-on' : 'camera-off') }
      else {
        const publication = [...room.localParticipant.trackPublications.values()].find(isInterviewMicrophone)
        const next = !microphoneEnabled(room.localParticipant)
        if (publication?.track) { if (next) await publication.track.unmute(); else await publication.track.mute() }
        else { const track = await createLocalAudioTrack({ echoCancellation: true }); try { await room.localParticipant.publishTrack(track, { source: Track.Source.Microphone, name: 'microphone' }) } catch (error) { track.stop(); throw error } }
        onEvent(next ? 'microphone-on' : 'microphone-off')
      }
      setRevision(value => value + 1)
    } catch { onError('Device change failed. Check browser permissions.') }
    finally { deviceChanging.current = false; setDeviceBusy(false) }
  }
  return <section className="interview-media-room">
    <Space wrap><Tag color={connection === 'connected' ? 'green' : 'orange'}>{connection}</Tag><Button disabled={connection !== 'connected' || deviceBusy} onClick={() => void toggle('microphone')}>{microphoneEnabled(room.localParticipant) ? 'Mute microphone' : 'Enable microphone'}</Button><Button disabled={connection !== 'connected' || deviceBusy} onClick={() => void toggle('camera')}>{room.localParticipant.isCameraEnabled ? 'Turn camera off' : 'Enable camera'}</Button><Button danger onClick={() => { void room.disconnect(); onLeave() }}>Leave call</Button></Space>
    <div className="interview-video-grid">{[room.localParticipant, ...room.remoteParticipants.values()].map(participant => <ParticipantMedia key={participant.identity || 'local'} participant={participant} revision={revision} />)}</div>
  </section>
}
