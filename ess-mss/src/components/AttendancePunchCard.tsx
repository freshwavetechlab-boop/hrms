import { useCallback, useEffect, useRef, useState } from 'react'
import { Alert, Button, Drawer, Input, Space } from 'antd'
import { ArrowRightOutlined, CameraOutlined, CheckCircleFilled, ClockCircleOutlined, EnvironmentOutlined, LockOutlined, LoginOutlined, LogoutOutlined, ReloadOutlined, SafetyCertificateOutlined } from '@ant-design/icons'
import type { User } from '../types'
import { getPunchWorkspace, PunchRequestError, recordBrowserPunch } from '../services/attendancePunchService'
import { capturePunchCameraFrame, getFreshPunchLocation, getPunchDeviceDetails, openPunchCamera, stopPunchCamera } from '../services/attendancePunchDevice'
import { attendanceClock, buildBrowserPunch, cameraErrorMessage, canMarkAttendance, mapsLocationUrl, resultNeedsReason, validMapLocation } from '../utils/attendancePunch'
import type { AttendanceToday, BrowserPunchRequest, PunchAction, PunchLocation, SavedAttendancePunch } from '../utils/attendancePunch'
import './AttendancePunchCard.css'

export function AttendancePunchCard({ user, onRecorded }: { user: User; onRecorded: () => Promise<void> }) {
  if (!canMarkAttendance(user)) return null
  return <AttendancePunchSession key={`${user.id}:${user.clientId || 0}:${user.employeeId || 0}`} user={user} onRecorded={onRecorded} />
}

function AttendancePunchSession({ user, onRecorded }: { user: User; onRecorded: () => Promise<void> }) {
  const permitted = canMarkAttendance(user)
  const scope = `${user.id}:${user.clientId || 0}:${user.employeeId || 0}`
  const [today, setToday] = useState<AttendanceToday | null>(null), [punches, setPunches] = useState<SavedAttendancePunch[]>([])
  const [clock, setClock] = useState(() => new Date())
  const [loading, setLoading] = useState(true), [error, setError] = useState(''), [logError, setLogError] = useState(''), [success, setSuccess] = useState('')
  const [open, setOpen] = useState(false), [action, setAction] = useState<PunchAction>('CheckIn'), [online, setOnline] = useState(navigator.onLine)
  const [location, setLocation] = useState<PunchLocation | null>(null), [locating, setLocating] = useState(false)
  const [photo, setPhoto] = useState(''), [cameraRunning, setCameraRunning] = useState(false), [cameraStarting, setCameraStarting] = useState(false), [cameraReady, setCameraReady] = useState(false)
  const [password, setPassword] = useState(''), [reason, setReason] = useState(''), [needsReason, setNeedsReason] = useState(false)
  const [submitting, setSubmitting] = useState(false), [pendingRetry, setPendingRetry] = useState(false), [retryAfter, setRetryAfter] = useState(0), [proofError, setProofError] = useState('')
  const video = useRef<HTMLVideoElement>(null), stream = useRef<MediaStream | null>(null)
  const cameraVersion = useRef(0), gpsVersion = useRef(0), loadVersion = useRef(0), drawerOpen = useRef(false), sending = useRef(false)
  // An uncertain result keeps only the request with an empty password in memory.
  // Retrying uses the same id and coordinates; nothing is queued or persisted.
  const attempt = useRef<BrowserPunchRequest | null>(null)

  const stopCamera = useCallback(() => {
    cameraVersion.current++
    stopPunchCamera(stream.current); stream.current = null
    if (video.current) video.current.srcObject = null
    setCameraRunning(false); setCameraReady(false); setCameraStarting(false)
  }, [])

  const applyWorkspace = useCallback((snapshot: Awaited<ReturnType<typeof getPunchWorkspace>>, version: number) => {
    if (version !== loadVersion.current) return
    setToday(snapshot.today); setPunches(snapshot.punches); setLogError(snapshot.logError); setClock(new Date()); setError(''); setLoading(false)
  }, [])
  const failWorkspace = useCallback((failure: unknown, version: number) => {
    if (version !== loadVersion.current) return
    setToday(null); setError(failure instanceof Error ? failure.message : 'Unable to load attendance.'); setLoading(false)
  }, [])
  const refreshToday = useCallback(async () => {
    const version = ++loadVersion.current
    setLoading(true); setError('')
    try { applyWorkspace(await getPunchWorkspace(), version) } catch (failure) { failWorkspace(failure, version) }
  }, [applyWorkspace, failWorkspace])

  const disposeSession = useCallback(() => { loadVersion.current++; gpsVersion.current++; cameraVersion.current++; drawerOpen.current = false; stopPunchCamera(stream.current); stream.current = null }, [])
  useEffect(() => {
    const version = ++loadVersion.current
    void getPunchWorkspace().then(snapshot => applyWorkspace(snapshot, version)).catch(failure => failWorkspace(failure, version))
    return disposeSession
  }, [applyWorkspace, failWorkspace, disposeSession])
  useEffect(() => {
    const update = () => setOnline(navigator.onLine)
    window.addEventListener('online', update); window.addEventListener('offline', update)
    return () => { window.removeEventListener('online', update); window.removeEventListener('offline', update) }
  }, [])
  useEffect(() => {
    const timer = window.setInterval(() => setClock(new Date()), 30000)
    return () => window.clearInterval(timer)
  }, [])
  useEffect(() => {
    if (retryAfter <= 0) return
    const timer = setTimeout(() => setRetryAfter(value => Math.max(0, value - 1)), 1000)
    return () => clearTimeout(timer)
  }, [retryAfter])

  const refreshLocation = async () => {
    if (pendingRetry || sending.current) return
    const version = ++gpsVersion.current
    setLocating(true); setProofError('')
    try {
      const reading = await getFreshPunchLocation()
      if (drawerOpen.current && version === gpsVersion.current) setLocation(reading)
    } catch (failure) { if (drawerOpen.current && version === gpsVersion.current) { setLocation(null); setProofError(failure instanceof Error ? failure.message : 'Unable to obtain GPS.') } }
    finally { if (drawerOpen.current && version === gpsVersion.current) setLocating(false) }
  }

  const startCamera = async () => {
    if (sending.current) return
    stopCamera(); setPhoto(''); setProofError(''); setCameraStarting(true)
    const version = cameraVersion.current
    try {
      const next = await openPunchCamera()
      if (!drawerOpen.current || version !== cameraVersion.current) { stopPunchCamera(next); return }
      stream.current = next
      if (!video.current) throw new Error('Camera preview is not ready. Open the camera again.')
      video.current.srcObject = next
      await video.current.play()
      if (drawerOpen.current && version === cameraVersion.current) { setCameraRunning(true); setCameraReady(video.current.readyState >= 2 && video.current.videoWidth > 0) }
    } catch (failure) { if (drawerOpen.current && version === cameraVersion.current) { stopCamera(); setProofError(cameraErrorMessage(failure)) } }
    finally { if (drawerOpen.current && version === cameraVersion.current) setCameraStarting(false) }
  }

  const capturePhoto = () => {
    try {
      if (!video.current || !stream.current) throw new Error('Open the camera and wait for its live picture first.')
      setPhoto(capturePunchCameraFrame(video.current)); stopCamera(); setProofError('')
    } catch (failure) { setProofError(failure instanceof Error ? failure.message : 'Unable to capture the camera picture.') }
  }

  const openPunch = () => {
    const next = pendingRetry && attempt.current ? attempt.current.action : today?.nextExpectedAction
    if (!permitted || !online || !['CheckIn', 'CheckOut'].includes(next || '')) return
    setAction(next as PunchAction); setOpen(true); drawerOpen.current = true; setPassword(''); setPhoto(''); setProofError(''); setSuccess('')
    if (pendingRetry && attempt.current) setLocation(attempt.current)
    else { setReason(''); setNeedsReason(false); setLocation(null); void refreshLocation() }
  }

  const closeProof = () => {
    if (sending.current) return
    drawerOpen.current = false; gpsVersion.current++; stopCamera(); setOpen(false); setPassword(''); setPhoto(''); setLocation(null); setLocating(false); setProofError('')
    if (!pendingRetry) { attempt.current = null; setReason(''); setNeedsReason(false) }
  }

  const submit = async () => {
    if (sending.current || !permitted) return
    if (!navigator.onLine) { setProofError('You are offline. Reconnect before confirming attendance. Punches are not queued offline.'); return }
    if (!photo || !password || needsReason && !reason.trim()) { setProofError(!photo ? 'Capture a photo with the camera first.' : !password ? 'Enter your ESS password to confirm attendance.' : 'Enter the reason requested for this punch.'); return }
    const confirmationPassword = password
    setPassword(''); setSubmitting(true); sending.current = true; setProofError('')
    try {
      let request: BrowserPunchRequest
      if (pendingRetry && attempt.current) request = { ...attempt.current, confirmationPassword, cameraCaptureConfirmed: true }
      else {
        // Acquire another fresh reading at the actual submit, after the user
        // has captured their camera picture and entered the confirmation.
        setLocating(true)
        const reading = await getFreshPunchLocation()
        setLocation(reading); setLocating(false)
        const details = getPunchDeviceDetails(scope)
        request = buildBrowserPunch({ requestId: attempt.current?.clientRequestId || crypto.randomUUID(), action, location: reading, cameraFrameCaptured: !!photo, password: confirmationPassword, reason, ...details })
        attempt.current = { ...request, confirmationPassword: '' }
      }
      const wasPending = pendingRetry
      const result = await recordBrowserPunch(request)
      setPassword(''); setPendingRetry(wasPending && !result.punchRecorded && ['PasswordConfirmationRequired', 'PasswordConfirmationRateLimited'].includes(result.status))
      if (result.punchRecorded) {
        attempt.current = null; drawerOpen.current = false; gpsVersion.current++; stopCamera(); setOpen(false); setPhoto(''); setLocation(null); setReason(''); setNeedsReason(false); setSuccess(`${action === 'CheckIn' ? 'Punch In' : 'Punch Out'} ${result.idempotentReplay ? 'was already recorded. The saved location is unchanged.' : 'recorded successfully.'}`)
        await refreshToday()
        try { await onRecorded() } catch { setLogError('Attendance is recorded. Refresh the attendance history to see the latest entry.') }
      } else {
        setNeedsReason(resultNeedsReason(result)); setProofError(result.message || 'This punch was not recorded. Review the details and retry.')
        if (result.retryAfterSeconds) setRetryAfter(Math.min(3600, Math.max(0, Math.ceil(result.retryAfterSeconds))))
        if (!resultNeedsReason(result) && !['PasswordConfirmationRequired', 'PasswordConfirmationRateLimited'].includes(result.status)) void refreshToday()
      }
    } catch (failure) {
      const uncertain = !!attempt.current && (pendingRetry || failure instanceof PunchRequestError && failure.uncertain)
      setPendingRetry(uncertain); setProofError(failure instanceof Error ? failure.message : 'Unable to confirm this punch.')
    } finally { sending.current = false; setPassword(''); setSubmitting(false); setLocating(false) }
  }

  if (!permitted) return null
  const next = today?.nextExpectedAction
  const ready = next === 'CheckIn' || next === 'CheckOut'
  const buttonLabel = pendingRetry ? 'Retry pending punch' : today?.approvalPending || next === 'WaitForApproval' ? 'Awaiting approval' : next === 'Completed' ? 'Attendance complete' : next === 'Unavailable' ? 'Punch unavailable' : next === 'CheckOut' ? 'Punch Out' : 'Punch In'
  const checkedIn = next === 'CheckOut'
  const completed = next === 'Completed'
  const awaiting = !!today?.approvalPending || next === 'WaitForApproval'
  const stateLabel = loading ? 'Updating attendance' : error ? 'Unable to load' : awaiting ? 'Awaiting approval' : completed ? 'Workday complete' : checkedIn ? 'Punched in' : ready ? 'Not punched in' : 'Punch unavailable'
  const dateLabel = new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short', year: 'numeric', weekday: 'short', timeZone: 'Asia/Kolkata' }).format(today ? new Date(`${today.attendanceDate.slice(0, 10)}T12:00:00+05:30`) : clock)
  const currentTime = new Intl.DateTimeFormat('en-IN', { hour: '2-digit', minute: '2-digit', hour12: true, timeZone: 'Asia/Kolkata' }).format(clock)
  const displayTime = (value?: string | null) => value ? attendanceClock(/^\d{1,2}:/.test(value) ? `2000-01-01T${value}` : value) : '—'
  const lastLocation = [...punches].reverse().find(validMapLocation)
  const disablePunch = !online || loading || submitting || !pendingRetry && (!ready || awaiting)
  return <>
    <section className="ess-punch-card" aria-label="Punch attendance" aria-busy={loading}>
      <div className="ess-punch-heading">
        <div className="ess-punch-identity"><span className="ess-punch-heading-icon"><ClockCircleOutlined /></span><div><h3>Today’s attendance</h3><span>{dateLabel}</span></div></div>
        <div className="ess-punch-heading-actions"><span className={`ess-punch-state ${awaiting ? 'is-pending' : checkedIn || completed ? 'is-positive' : ''}`} role="status"><i />{stateLabel}</span><Button type="text" icon={<ReloadOutlined />} aria-label="Refresh attendance" title="Refresh attendance" onClick={() => void refreshToday()} loading={loading} disabled={submitting} /></div>
      </div>
      <div className="ess-punch-workday">
        <div className="ess-punch-clock"><span className="ess-punch-overline">INDIA STANDARD TIME</span><strong>{currentTime}</strong><span>{awaiting ? 'Your request is with your approver.' : completed ? 'You’re all done for this workday.' : checkedIn ? 'Your attendance is in progress.' : 'Ready when you are. Have a great day.'}</span></div>
        <div className="ess-punch-timings"><div><span><LoginOutlined />Punch In</span><b>{displayTime(today?.checkInTime)}</b></div><div><span><LogoutOutlined />Punch Out</span><b>{displayTime(today?.checkOutTime)}</b></div></div>
        <div className="ess-punch-action"><Button type="primary" size="large" className={checkedIn ? 'is-checkout' : ''} icon={completed ? <CheckCircleFilled /> : checkedIn ? <LogoutOutlined /> : <LoginOutlined />} disabled={disablePunch} onClick={openPunch}>{buttonLabel}{ready && <ArrowRightOutlined />}</Button><span><SafetyCertificateOutlined /> Location & camera verification</span></div>
      </div>
      {!online && <Alert type="warning" showIcon message="You are offline. Reconnect to mark attendance; punches are not queued offline." />}
      {error && <Alert type="error" showIcon message={error} />}{success && <Alert type="success" showIcon message={success} />}
      {pendingRetry && !open && <Alert type="warning" showIcon message="The last submission was not confirmed. Retry the pending punch to check its result without creating a duplicate." />}
      {today && !ready && !pendingRetry && <p className="ess-punch-note">{today.approvalPending || next === 'WaitForApproval' ? 'An attendance request is waiting for approval.' : next === 'Completed' ? 'Punch In and Punch Out are complete for this attendance date.' : 'Attendance is unavailable for punching. Contact HR if a correction is needed.'}</p>}
      <div className="ess-punch-location-strip"><EnvironmentOutlined /><span>{lastLocation ? <>Latest location saved <a href={mapsLocationUrl(lastLocation)} target="_blank" rel="noopener noreferrer">View on map <ArrowRightOutlined /></a></> : 'Your location is captured when you punch.'}</span><span className={`ess-punch-connection ${online ? '' : 'is-offline'}`}><i />{online ? 'Online' : 'Offline'}</span></div>
      {!!punches.length && <details className="ess-punch-saved"><summary>Saved punches and locations ({punches.length})</summary><ul>{punches.map(punch => <li key={punch.id}><span className={`ess-punch-log-icon ${punch.action === 'CheckOut' ? 'is-out' : ''}`}>{punch.action === 'CheckIn' ? <LoginOutlined /> : <LogoutOutlined />}</span><span><b>{punch.action === 'CheckIn' ? 'Punch In' : 'Punch Out'}</b><time>{attendanceClock(punch.capturedAt)}</time></span>{validMapLocation(punch) ? <a href={mapsLocationUrl(punch)} target="_blank" rel="noopener noreferrer"><EnvironmentOutlined /> View saved location <small>Accuracy {punch.accuracyMeters} m</small></a> : <span>Location not available</span>}</li>)}</ul></details>}
      {logError && <p className="ess-punch-note" role="status">{logError}</p>}
    </section>
    <Drawer title={<span className="ess-punch-drawer-title">{action === 'CheckIn' ? <LoginOutlined /> : <LogoutOutlined />}{action === 'CheckIn' ? 'Punch In' : 'Punch Out'} attendance</span>} open={open} width="min(500px, 100vw)" className="ess-punch-drawer" closable={!submitting} maskClosable={!submitting} onClose={closeProof} footer={<div className="ess-punch-footer"><Button onClick={closeProof} disabled={submitting}>Cancel</Button><Button type="primary" icon={<CheckCircleFilled />} onClick={() => void submit()} loading={submitting} disabled={!online || !photo || !password || locating || retryAfter > 0 || needsReason && !reason.trim()}>{retryAfter > 0 ? `Retry in ${retryAfter}s` : pendingRetry ? 'Retry same punch' : 'Confirm attendance'}</Button></div>}>
      <div className="ess-punch-intro"><span className="ess-punch-overline">A QUICK, SECURE CHECK</span><h3>Let’s confirm it’s you</h3><p>Verify your location, take a picture and confirm your attendance.</p></div>
      {proofError && <Alert type="error" showIcon message={proofError} />}
      {pendingRetry && <Alert type="warning" showIcon message="This retries the previous submission with the same request ID and captured location. Your password is required again." />}
      <section className="ess-punch-step"><div className="ess-punch-step-head"><span className={`ess-punch-step-number ${location ? 'is-done' : ''}`}>{location ? <CheckCircleFilled /> : '1'}</span><div><h3>Verify location</h3><p>Precise GPS from your device</p></div>{location && <span className="ess-punch-verified">Ready</span>}</div><div className={`ess-punch-gps ${location ? 'is-ready' : ''}`}><EnvironmentOutlined /><div><b>{location ? 'Location captured' : locating ? 'Finding your location…' : 'Location required'}</b><span>{location ? `GPS accuracy: ${location.accuracyMeters} m` : 'Allow precise location in your browser.'}</span></div><Button type="text" size="small" aria-label={location ? 'Refresh GPS' : 'Get GPS location'} loading={locating} disabled={submitting || pendingRetry} onClick={() => void refreshLocation()} icon={<ReloadOutlined />} /></div></section>
      <section className="ess-punch-step"><div className="ess-punch-step-head"><span className={`ess-punch-step-number ${photo ? 'is-done' : ''}`}>{photo ? <CheckCircleFilled /> : '2'}</span><div><h3>Camera picture</h3><p>A fresh picture for this punch</p></div>{photo && <span className="ess-punch-verified">Ready</span>}</div><div className="ess-punch-camera">{!photo && !cameraRunning && !cameraStarting && <div className="ess-punch-camera-placeholder"><CameraOutlined /><b>A quick camera check</b><span>Keep your face in the frame</span></div>}{photo && <img src={photo} alt="Captured camera preview" />}<video ref={video} autoPlay muted playsInline aria-label="Live camera preview" hidden={!cameraRunning && !cameraStarting} onLoadedData={() => setCameraReady(!!video.current && video.current.readyState >= 2 && video.current.videoWidth > 0)} /></div><Space wrap className="ess-punch-camera-actions"><Button icon={<CameraOutlined />} onClick={() => void startCamera()} loading={cameraStarting} disabled={submitting}>{photo ? 'Retake photo' : 'Open camera'}</Button>{cameraRunning && <Button type="primary" onClick={capturePhoto} disabled={!cameraReady || submitting}>Capture photo</Button>}</Space><p className="ess-punch-note"><LockOutlined /> Your camera preview is cleared when you close this window.</p></section>
      {needsReason && <label className="ess-punch-input"><span>Reason for this punch</span><Input.TextArea aria-label="Punch reason" maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} disabled={submitting} /></label>}
      <section className="ess-punch-step"><div className="ess-punch-step-head"><span className={`ess-punch-step-number ${password ? 'is-done' : ''}`}>{password ? <CheckCircleFilled /> : '3'}</span><div><h3>Confirm it’s you</h3><p>Use your employee portal password</p></div></div><label className="ess-punch-input"><span>Confirm ESS password</span><Input.Password prefix={<LockOutlined />} placeholder="Enter your password" aria-label="Confirm ESS password" autoComplete="current-password" value={password} onChange={event => setPassword(event.target.value)} disabled={submitting} /></label></section>
    </Drawer>
  </>
}
