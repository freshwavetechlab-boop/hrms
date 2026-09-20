import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Space, Tag } from 'antd'
import { Room, RoomEvent, Track, createLocalAudioTrack, type Participant } from 'livekit-client'
import type { InterviewMediaGrant } from '../types/internalInterviews'
import { interviewerVoiceTrackName, isInterviewMicrophone, type InterviewVoicePublisher } from '../services/interviewVoicePlayback'
import { InterviewDeviceSelectors } from './InterviewDeviceCheck'
import type { InterviewDevicePreferences } from '../services/interviewDeviceCheck'
import { startInterviewRoomDevices, switchInterviewRoomDevice } from '../services/interviewRoomDevices'

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

export default function InterviewMediaRoom({ grant, devices, onDevicesChange, onLeave, onEvent, onError, onVoicePublisher }: {
  grant: InterviewMediaGrant; onLeave: () => void; onEvent: (kind: string) => void; onError: (error: string) => void;
  devices: InterviewDevicePreferences; onDevicesChange: (devices: InterviewDevicePreferences) => void;
  onVoicePublisher: (publisher: InterviewVoicePublisher | undefined) => void;
}) {
  const [room] = useState(() => new Room({ adaptiveStream: true, dynacast: true,
    videoCaptureDefaults: devices.cameraId ? { deviceId: { exact: devices.cameraId } } : undefined,
    audioCaptureDefaults: { echoCancellation: true, noiseSuppression: true, ...(devices.microphoneId ? { deviceId: { exact: devices.microphoneId } } : {}) },
  }))
  const initialDevices = useRef(devices)
  const [availableDevices, setAvailableDevices] = useState<MediaDeviceInfo[]>([])
  const [audioBlocked, setAudioBlocked] = useState(false)
  const [revision, setRevision] = useState(0)
  const [connection, setConnection] = useState('connecting')
  const [deviceBusy, setDeviceBusy] = useState(false)
  const deviceChanging = useRef(false)
  const callbacks = useRef({ onLeave, onEvent, onError, onVoicePublisher, onDevicesChange, devices }); callbacks.current = { onLeave, onEvent, onError, onVoicePublisher, onDevicesChange, devices }
  useEffect(() => {
    let active = true
    const refresh = () => { if (active) setRevision(value => value + 1) }
    const enumerate = () => { void navigator.mediaDevices?.enumerateDevices().then(items => { if (active) setAvailableDevices(items) }).catch(() => {}) }
    const playback = () => { if (active) setAudioBlocked(!room.canPlaybackAudio) }
    navigator.mediaDevices?.addEventListener('devicechange', enumerate); enumerate()
    room.on(RoomEvent.AudioPlaybackStatusChanged, playback)
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
      // Respect pre-join mute/device choices; a camera failure must not prevent audio.
      const selected = initialDevices.current
      const results = await startInterviewRoomDevices(room, selected)
      if (!active) { await room.disconnect(); return }
      if (results.some(result => result.status === 'rejected')) callbacks.current.onError('Connected, but a selected device could not start. Check permissions/device settings; other available devices remain connected.')
      callbacks.current.onEvent(room.localParticipant.isCameraEnabled ? 'camera-on' : 'camera-off')
      callbacks.current.onEvent(microphoneEnabled(room.localParticipant) ? 'microphone-on' : 'microphone-off')
      playback(); enumerate(); refresh()
    }).catch(() => { if (active) callbacks.current.onError('Unable to join media. Check camera/microphone permissions and the self-hosted video server, then leave and retry.') })
    return () => { active = false; callbacks.current.onVoicePublisher(undefined); navigator.mediaDevices?.removeEventListener('devicechange', enumerate); room.removeAllListeners(); void room.disconnect() }
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
        else { const track = await createLocalAudioTrack({ echoCancellation: true, noiseSuppression: true, ...(devices.microphoneId ? { deviceId: { exact: devices.microphoneId } } : {}) }); try { await room.localParticipant.publishTrack(track, { source: Track.Source.Microphone, name: 'microphone' }) } catch (error) { track.stop(); throw error } }
        onEvent(next ? 'microphone-on' : 'microphone-off')
      }
      setRevision(value => value + 1)
      onDevicesChange({ ...devices, cameraEnabled: room.localParticipant.isCameraEnabled, microphoneEnabled: microphoneEnabled(room.localParticipant) })
    } catch { onError('Device change failed. Check browser permissions.') }
    finally { deviceChanging.current = false; setDeviceBusy(false) }
  }
  const selectDevice = async (kind: MediaDeviceKind, id: string) => {
    if (deviceChanging.current || room.state !== 'connected') return
    deviceChanging.current = true; setDeviceBusy(true)
    try {
      await switchInterviewRoomDevice(room, kind, id)
      callbacks.current.onDevicesChange({ ...callbacks.current.devices, [kind === 'videoinput' ? 'cameraId' : kind === 'audioinput' ? 'microphoneId' : 'speakerId']: id })
      setRevision(value => value + 1)
    } catch { onError('Device change failed. Check permissions and choose an available device. No device check guarantees network audio quality.') }
    finally { deviceChanging.current = false; setDeviceBusy(false) }
  }
  return <section className="interview-media-room">
    {audioBlocked && <Alert type="warning" message="Browser blocked call audio" action={<Button onClick={() => void room.startAudio().catch(() => onError('Call audio is still blocked. Check browser sound permissions.'))}>Enable call audio</Button>} />}
    <Space wrap><Tag color={connection === 'connected' ? 'green' : 'orange'}>{connection}</Tag><Button disabled={connection !== 'connected' || deviceBusy} onClick={() => void toggle('microphone')}>{microphoneEnabled(room.localParticipant) ? 'Mute microphone' : 'Enable microphone'}</Button><Button disabled={connection !== 'connected' || deviceBusy} onClick={() => void toggle('camera')}>{room.localParticipant.isCameraEnabled ? 'Turn camera off' : 'Enable camera'}</Button><Button danger onClick={() => { void room.disconnect(); onLeave() }}>Leave call</Button></Space>
    <InterviewDeviceSelectors preferences={devices} devices={availableDevices} disabled={connection !== 'connected' || deviceBusy} onSelect={(kind, id) => void selectDevice(kind, id)} />
    <div className="interview-video-grid">{[room.localParticipant, ...room.remoteParticipants.values()].map(participant => <ParticipantMedia key={participant.identity || 'local'} participant={participant} revision={revision} />)}</div>
  </section>
}
