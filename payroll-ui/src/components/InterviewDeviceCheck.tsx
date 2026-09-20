import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Card, Checkbox, Select, Space } from 'antd'
import { InterviewDeviceCheck, InterviewSpeakerCheck, supportsInterviewSpeakerSelection, watchInterviewMicrophone,
  type InterviewDevicePreferences, type InterviewInputKind } from '../services/interviewDeviceCheck'

export function InterviewDeviceSelectors({ preferences, devices, disabled, onSelect }: {
  preferences: InterviewDevicePreferences; devices: MediaDeviceInfo[]; disabled?: boolean;
  onSelect: (kind: MediaDeviceKind, id: string) => void;
}) {
  return <div className="interview-device-selectors">{([
    ['videoinput', 'Camera', preferences.cameraId], ['audioinput', 'Microphone', preferences.microphoneId],
    ['audiooutput', 'Speaker', preferences.speakerId],
  ] as const).map(([kind, label, selected]) => {
    const options = devices.filter(device => device.kind === kind && device.deviceId && device.deviceId !== 'default')
      .map((device, index) => ({ value: device.deviceId, label: device.label || `${label} ${index + 1}` }))
    if (selected && !options.some(option => option.value === selected)) options.push({ value: selected, label: 'Selected device unavailable — choose another' })
    return <label key={kind}><span>{label}</span><Select aria-label={`Interview ${label.toLowerCase()} device`} value={selected}
      disabled={disabled || (kind === 'audiooutput' && !supportsInterviewSpeakerSelection())}
      options={[{ value: '', label: 'System default' }, ...options]} onChange={id => onSelect(kind, id)} /></label>
  })}</div>
}

export default function InterviewDevicePreflight({ preferences, onChange }: {
  preferences: InterviewDevicePreferences; onChange: (value: InterviewDevicePreferences) => void;
}) {
  const [devices, setDevices] = useState<MediaDeviceInfo[]>([])
  const [streams, setStreams] = useState<Partial<Record<InterviewInputKind, MediaStream>>>({})
  const [errors, setErrors] = useState<Partial<Record<InterviewInputKind, string>>>({})
  const [checking, setChecking] = useState(false)
  const [level, setLevel] = useState(0), [heardInput, setHeardInput] = useState(false)
  const [meterError, setMeterError] = useState(false), [cameraReady, setCameraReady] = useState(false)
  const [speakerState, setSpeakerState] = useState<'idle' | 'playing' | 'confirm' | 'heard' | 'error'>('idle')
  const [listError, setListError] = useState(false)
  const checker = useRef<InterviewDeviceCheck | null>(null), speaker = useRef<InterviewSpeakerCheck | null>(null)
  const video = useRef<HTMLVideoElement>(null), active = useRef(true), attempt = useRef(0)
  const current = useRef(preferences); current.current = preferences
  const enumerate = async () => {
    try { const items = await navigator.mediaDevices?.enumerateDevices(); if (active.current) { setDevices(items || []); setListError(!items) } }
    catch { if (active.current) setListError(true) }
  }
  useEffect(() => {
    active.current = true
    checker.current = new InterviewDeviceCheck((kind, stream, error) => {
      setStreams(values => ({ ...values, [kind]: stream || undefined }))
      setErrors(values => ({ ...values, [kind]: error }))
      if (kind === 'camera') setCameraReady(false)
    })
    void enumerate()
    const changed = () => { void enumerate(); speaker.current?.stop(); speaker.current = null; setSpeakerState('idle') }
    navigator.mediaDevices?.addEventListener('devicechange', changed)
    return () => { active.current = false; attempt.current++; checker.current?.dispose(); speaker.current?.stop(); navigator.mediaDevices?.removeEventListener('devicechange', changed) }
  }, [])
  useEffect(() => {
    const element = video.current
    if (!element) return
    element.srcObject = streams.camera || null
    if (streams.camera) void element.play().catch(() => { if (active.current) setErrors(values => ({ ...values, camera: 'Preview could not play. Retry the camera check.' })) })
    return () => { element.srcObject = null }
  }, [streams.camera])
  useEffect(() => {
    setLevel(0); setHeardInput(false); setMeterError(false)
    if (!streams.microphone) return
    try { return watchInterviewMicrophone(streams.microphone, value => { setLevel(value); if (value > 2) setHeardInput(true) }, () => setMeterError(true)) }
    catch { setMeterError(true) }
  }, [streams.microphone])
  const check = async () => {
    const generation = ++attempt.current; setChecking(true)
    const selected = current.current
    await Promise.all([
      selected.cameraEnabled ? checker.current?.start('camera', selected.cameraId) : checker.current?.stop('camera'),
      selected.microphoneEnabled ? checker.current?.start('microphone', selected.microphoneId) : checker.current?.stop('microphone'),
    ])
    if (active.current && generation === attempt.current) { setChecking(false); void enumerate() }
  }
  const stop = () => { attempt.current++; checker.current?.stopAll(); speaker.current?.stop(); speaker.current = null; setSpeakerState('idle'); setChecking(false) }
  const select = (kind: MediaDeviceKind, id: string) => {
    const field = kind === 'videoinput' ? 'cameraId' : kind === 'audioinput' ? 'microphoneId' : 'speakerId'
    onChange({ ...current.current, [field]: id }); stop()
  }
  const toggle = (kind: InterviewInputKind, enabled: boolean) => {
    onChange({ ...current.current, [`${kind}Enabled`]: enabled }); stop()
  }
  const testSpeaker = async () => {
    speaker.current?.stop(); setSpeakerState('playing')
    let test: InterviewSpeakerCheck | undefined
    try {
      test = new InterviewSpeakerCheck(); speaker.current = test
      await test.play(current.current.speakerId, () => { if (active.current && speaker.current === test) setSpeakerState('confirm') })
    } catch { if (active.current && (!test || speaker.current === test)) setSpeakerState('error') }
  }
  return <Card title="Camera & audio check" className="interview-device-check">
    <p>This preview stays on your device. It is not recorded or sent to the panel. Checks are optional; tell the panel if you need another way to participate.</p>
    <InterviewDeviceSelectors preferences={preferences} devices={devices} onSelect={select} />
    {!supportsInterviewSpeakerSelection() && <small>Speaker selection is controlled by your browser/system settings on this device.</small>}
    {listError && <Alert type="warning" message="Device list unavailable. Allow permissions on HTTPS (or localhost), then check again." />}
    <div className="interview-device-preview-grid">
      <section><div className="interview-camera-preview"><video ref={video} muted autoPlay playsInline aria-label="Your local camera preview" onLoadedData={() => setCameraReady(!!video.current?.srcObject)} />{!cameraReady && <span>Camera preview is not running</span>}</div>
        <p role="status">{errors.camera || (cameraReady ? 'Camera preview ready — check your framing.' : 'Camera not checked.')}</p></section>
      <section><h3>Microphone check</h3><p>Speak normally and watch the level move. Use headphones to reduce echo.</p>
        <div role="meter" aria-label="Microphone input level" aria-valuemin={0} aria-valuemax={100} aria-valuenow={level} className="interview-microphone-meter"><span style={{ width: `${level}%` }} /></div>
        <p role="status">{errors.microphone || (meterError ? 'Microphone connected, but level meter unavailable. Check again or ask the panel to confirm audio.' : streams.microphone ? heardInput ? 'Microphone signal detected.' : 'Microphone connected. Speak to check the input level.' : 'Microphone not checked.')}</p>
        <Space wrap><Button onClick={() => void testSpeaker()} disabled={speakerState === 'playing'}>Test speaker</Button>{speakerState === 'confirm' && <Button onClick={() => setSpeakerState('heard')}>I heard the test sound</Button>}</Space>
        <p role="status">{speakerState === 'playing' ? 'Playing a short test tone…' : speakerState === 'heard' ? 'Speaker confirmed by you.' : speakerState === 'confirm' ? 'Did you hear it? Confirm or check output volume/device and retry.' : speakerState === 'error' ? 'Test audio could not play. Check browser permission and system output, then retry.' : 'Speaker not checked.'}</p>
      </section>
    </div>
    <Space wrap><Checkbox checked={preferences.cameraEnabled} onChange={event => toggle('camera', event.target.checked)}>Join with camera on</Checkbox><Checkbox checked={preferences.microphoneEnabled} onChange={event => toggle('microphone', event.target.checked)}>Join with microphone on</Checkbox></Space>
    <Space wrap className="interview-device-check-actions"><Button type="primary" onClick={() => void check()} disabled={checking || (!preferences.cameraEnabled && !preferences.microphoneEnabled)}>Check camera & microphone</Button><Button onClick={stop} disabled={!checking && !streams.camera && !streams.microphone && speakerState !== 'playing'}>Stop device check</Button>{checking && <span role="status">Waiting for browser permissions… You can cancel with Stop device check.</span>}</Space>
  </Card>
}
