import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

function load(relative, imports = {}) {
  const exports = {}
  const code = ts.transpileModule(readFileSync(new URL(relative, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('exports', 'require', code)(exports, name => { if (imports[name]) return imports[name]; throw new Error(`Unexpected import: ${name}`) })
  return exports
}
const util = load('../src/utils/attendancePunch.ts')
const device = load('../src/services/attendancePunchDevice.ts', { '../utils/attendancePunch': util })
const location = { latitude: 19.076, longitude: 72.8777, accuracyMeters: 12, capturedAt: '2026-11-13T04:30:00.000Z' }
const request = () => util.buildBrowserPunch({ requestId: 'same-request-id', action: 'CheckIn', location, deviceId: 'web-device-id', cameraFrameCaptured: true, reason: '', platform: 'Synthetic OS' })

test('marking attendance requires the actual self and mark grants and a linked employee', () => {
  const user = { employeeId: 7, mustChangePassword: false, permissions: ['ESS.SELF', 'ess.attendance.mark'] }
  assert.equal(util.canMarkAttendance(user), true)
  for (const change of [{ employeeId: undefined }, { mustChangePassword: true }, { permissions: ['ess.self'] }, { permissions: ['settings.manage'] }]) assert.equal(util.canMarkAttendance({ ...user, ...change }), false)
})

test('GPS requests high accuracy, zero cached age and a bounded timeout', async () => {
  let options
  const now = Date.now()
  const reading = await util.acquirePunchLocation({ getCurrentPosition: (success, _failure, supplied) => { options = supplied; success({ coords: { latitude: 19.076, longitude: 72.8777, accuracy: 12.1 }, timestamp: now }) } })
  assert.deepEqual(options, { enableHighAccuracy: true, maximumAge: 0, timeout: 20000 })
  assert.equal(reading.accuracyMeters, 13)
  assert.equal(reading.latitude, 19.076)
  assert.ok(Date.now() - new Date(reading.capturedAt).getTime() < 1000)
})

test('GPS rejects non-finite, invalid, unrepresentable and cached readings instead of guessing', () => {
  const now = Date.now(), position = { coords: { latitude: 19.076, longitude: 72.8777, accuracy: 10 }, timestamp: now }
  for (const coords of [{ accuracy: 2147483647.1 }, { accuracy: -1 }, { accuracy: NaN }, { latitude: 91 }, { longitude: -181 }, { latitude: 0, longitude: 0 }]) assert.throws(() => util.locationFromPosition({ ...position, coords: { ...position.coords, ...coords } }, now))
  assert.throws(() => util.locationFromPosition({ ...position, timestamp: now - 60001 }, now), /old GPS/)
  assert.throws(() => util.locationFromPosition({ ...position, timestamp: now + 60001 }, now), /old GPS/)
  assert.equal(util.locationFromPosition({ ...position, coords: { ...position.coords, longitude: 0 } }, now).longitude, 0)
})

test('a fresh 241-metre reading reaches server policy unchanged rather than being rejected locally', async () => {
  const now = Date.now()
  const reading = util.locationFromPosition({ coords: { latitude: 19.076, longitude: 72.8777, accuracy: 241 }, timestamp: now }, now)
  const dto = util.buildBrowserPunch({ requestId: 'coarse-reading', action: 'CheckIn', location: reading, deviceId: 'web-device-id', cameraFrameCaptured: true, reason: '' })
  assert.equal(dto.accuracyMeters, 241)
  assert.equal(dto.latitude, 19.076)
  assert.equal(dto.longitude, 72.8777)
  let sent
  const api = load('../src/services/attendancePunchService.ts', { './essApi': { essFetch: async (_path, options) => {
    sent = JSON.parse(options.body)
    return new Response(JSON.stringify({ allowed: false, punchRecorded: false, status: 'LocationAccuracyTooLow', message: 'Configured geofence requires a precise reading.', requiresReason: false }), { status: 400 })
  } } })
  const result = await api.recordBrowserPunch(dto)
  assert.equal(sent.accuracyMeters, 241)
  assert.equal('confirmationPassword' in sent, false)
  assert.equal(result.punchRecorded, false)
  assert.equal(result.status, 'LocationAccuracyTooLow', 'the configured fence remains authoritative')
})

test('denied GPS and timeouts surface actionable device messages', async () => {
  for (const [code, message] of [[1, /permission was denied/], [2, /location services/], [3, /GPS timed out/]]) await assert.rejects(() => util.acquirePunchLocation({ getCurrentPosition: (_success, failure) => failure({ code }) }), message)
  await assert.rejects(() => util.acquirePunchLocation(null), /secure HTTPS/)
})

test('camera access requests a front-facing picture without microphone access', async () => {
  let constraints
  const fakeStream = { getTracks: () => [] }
  assert.equal(await device.openPunchCamera({ getUserMedia: async options => { constraints = options; return fakeStream } }), fakeStream)
  assert.equal(constraints.audio, false)
  assert.deepEqual(constraints.video.facingMode, { ideal: 'user' })
  await assert.rejects(() => device.openPunchCamera(null), /secure HTTPS/)
})

test('camera capture needs an actual ready video frame and draws it into the snapshot', () => {
  const live = { readyState: 2, videoWidth: 1280, videoHeight: 720 }
  const draws = [], canvas = { width: 0, height: 0, getContext: () => ({ drawImage: (...args) => draws.push(args) }), toDataURL: () => 'data:image/jpeg;base64,' + 'a'.repeat(140) }
  assert.ok(device.capturePunchCameraFrame(live, () => canvas).startsWith('data:image/jpeg;base64,'))
  assert.equal(canvas.width, 640); assert.equal(canvas.height, 360)
  assert.equal(draws[0][0], live)
  assert.throws(() => device.capturePunchCameraFrame({ ...live, readyState: 1 }, () => canvas), /live camera picture/)
  assert.throws(() => device.capturePunchCameraFrame({ ...live, videoWidth: 0 }, () => canvas), /live camera picture/)
  assert.throws(() => device.capturePunchCameraFrame(live, () => ({ ...canvas, toDataURL: () => 'data:,' })), /empty picture/)
})

test('camera cleanup stops every track, including after a disconnected device', () => {
  const stopped = []
  device.stopPunchCamera({ getTracks: () => [{ stop: () => { stopped.push(1); throw new Error('disconnected') } }, { stop: () => stopped.push(2) }] })
  assert.deepEqual(stopped, [1, 2])
  device.stopPunchCamera(null)
})

test('browser payload requires camera, uses the existing session and never carries a password or biometric claim', () => {
  const base = { requestId: 'id', action: 'CheckIn', location, deviceId: 'web-device-id', cameraFrameCaptured: true, reason: '  approved duty  ' }
  const dto = util.buildBrowserPunch(base)
  assert.equal(dto.biometricConfirmed, false)
  assert.equal(dto.cameraCaptureConfirmed, true)
  assert.equal('confirmationPassword' in dto, false)
  assert.equal('password' in dto, false)
  assert.equal(dto.networkType, 'Browser')
  assert.equal(dto.reason, 'approved duty')
  assert.equal(dto.capturedAt, location.capturedAt)
  assert.throws(() => util.buildBrowserPunch({ ...base, cameraFrameCaptured: false }), /camera/)
  assert.throws(() => util.buildBrowserPunch({ ...base, location: { ...location, latitude: Infinity } }), /GPS/)
  for (const accuracyMeters of [-1, NaN, Infinity, 241.5, 2147483648]) assert.throws(() => util.buildBrowserPunch({ ...base, location: { ...location, accuracyMeters } }), /GPS/)
})

test('the persisted browser identifier contains only random technical IDs and is scoped by account and client', () => {
  const values = new Map(), storage = { getItem: key => values.get(key) || null, setItem: (key, value) => values.set(key, value) }
  assert.equal(util.browserDeviceId(storage, '1:55:7', () => 'abcdefgh-1234'), 'web-abcdefgh-1234')
  assert.equal(util.browserDeviceId(storage, '1:55:7', () => assert.fail('must retain the existing device ID')), 'web-abcdefgh-1234')
  assert.equal(util.browserDeviceId(storage, '1:56:7', () => 'ijklmnop-1234'), 'web-ijklmnop-1234')
  assert.equal(values.size, 2)
  assert.ok([...values.values()].every(value => value.startsWith('web-')))
})

test('saved GPS map links use only valid coordinates and hardware placeholder coordinates are unavailable', () => {
  assert.equal(util.validMapLocation(location), true)
  assert.equal(util.validMapLocation({ latitude: 0, longitude: 0 }), false)
  assert.equal(util.validMapLocation({ latitude: 91, longitude: 72 }), false)
  assert.equal(util.mapsLocationUrl(location), 'https://www.google.com/maps/search/?api=1&query=19.076%2C72.8777')
})

test('outside-fence reason and server retry hints remain structured', async () => {
  const api = load('../src/services/attendancePunchService.ts', { './essApi': { essFetch: async () => new Response(JSON.stringify({ allowed: false, punchRecorded: false, status: 'ReasonRequired', message: 'Enter a reason', requiresReason: true }), { status: 400 }) } })
  const reason = await api.recordBrowserPunch(request())
  assert.equal(util.resultNeedsReason(reason), true)
  const limited = load('../src/services/attendancePunchService.ts', { './essApi': { essFetch: async () => new Response(JSON.stringify({ allowed: false, punchRecorded: false, status: 'RateLimited', message: 'Retry later', requiresReason: false }), { status: 429, headers: { 'Retry-After': '30' } }) } })
  assert.equal((await limited.recordBrowserPunch(request())).retryAfterSeconds, 30)
})

test('network failures do not automatically resend; explicit retry retains request ID and captured location', async () => {
  const calls = []
  const api = load('../src/services/attendancePunchService.ts', { './essApi': { essFetch: async (path, options) => { calls.push({ path, options }); if (calls.length === 1) throw new TypeError('connection closed'); return new Response(JSON.stringify({ allowed: true, punchRecorded: true, status: 'IdempotentReplay', message: 'Already recorded', requiresReason: false, idempotentReplay: true }), { status: 201 }) } } })
  const dto = request()
  await assert.rejects(() => api.recordBrowserPunch(dto), error => error.uncertain === true)
  assert.equal(calls.length, 1, 'no automatic retry or offline queue')
  assert.equal((await api.recordBrowserPunch(dto)).idempotentReplay, true)
  assert.equal(calls[0].options.body, calls[1].options.body)
  assert.equal(calls[1].path, '/api/ess/attendance/punch/browser')
  assert.equal(JSON.parse(calls[1].options.body).biometricConfirmed, false)
  assert.equal('confirmationPassword' in JSON.parse(calls[1].options.body), false)
})

test('server errors and unreadable success are uncertain while permission denials are explicit', async () => {
  for (const [status, body, uncertain] of [[500, '{}', true], [201, 'not-json', true], [403, '{}', false], [401, '{}', false]]) {
    const api = load('../src/services/attendancePunchService.ts', { './essApi': { essFetch: async () => new Response(body, { status }) } })
    await assert.rejects(() => api.recordBrowserPunch(request()), error => error.uncertain === uncertain)
  }
})

test('today state blocks pending approval and saved-punch logs are read without caching', async () => {
  const calls = [], replies = [{ attendanceDate: '2026-11-13T00:00:00+05:30', nextExpectedAction: 'WaitForApproval', approvalPending: true }, [{ id: 1, action: 'CheckIn', capturedAt: '2026-11-13T10:00:00+05:30', latitude: 19.076, longitude: 72.8777, accuracyMeters: 12, decision: 'Accepted', validationStatus: 'NoGeoFenceConfigured' }]]
  const api = load('../src/services/attendancePunchService.ts', { './essApi': { essFetch: async (path, options) => { calls.push({ path, options }); return new Response(JSON.stringify(replies.shift())) } } })
  assert.equal((await api.getAttendanceToday()).nextExpectedAction, 'WaitForApproval')
  assert.equal((await api.getSavedAttendancePunches('2026-11-13'))[0].latitude, 19.076)
  assert.deepEqual(calls.map(call => call.options.cache), ['no-store', 'no-store'])
  assert.equal(calls[1].path, '/api/ess/attendance/punches?date=2026-11-13')
  await assert.rejects(() => api.getSavedAttendancePunches('invalid'), /valid attendance date/)
})

// Service-worker isolation is exercised against real synthetic requests in
// pwa-install.test.mjs, including API calls, POST punches and offline behavior.
