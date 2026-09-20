import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const code = ts.transpileModule(readFileSync(new URL('../src/services/interviewDeviceCheck.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText
const sandbox = vm.createContext({ exports: {}, Error })
vm.runInContext(code, sandbox)
const { InterviewDeviceCheck, interviewDeviceError, interviewMicrophoneLevel } = sandbox.exports
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r }); return { promise, resolve } }
function stream() {
  const track = { stops: 0, callbacks: {}, stop() { this.stops++ }, addEventListener(kind, callback) { this.callbacks[kind] = callback } }
  return { getTracks: () => [track], track }
}
test('no capture without an explicit start; exact devices and separate permission requests', async () => {
  const calls = [], audio = stream(), video = stream()
  const check = new InterviewDeviceCheck(() => {}, async constraints => { calls.push(constraints); return constraints.video ? video : audio })
  assert.equal(calls.length, 0)
  await check.start('camera', 'camera-2'); await check.start('microphone', 'microphone-2')
  assert.equal(calls[0].video.deviceId.exact, 'camera-2'); assert.equal(calls[0].audio, false)
  assert.equal(calls[1].audio.deviceId.exact, 'microphone-2'); assert.equal(calls[1].video, false)
  assert.equal(calls[1].audio.echoCancellation, true)
  check.dispose(); assert.equal(audio.track.stops, 1); assert.equal(video.track.stops, 1)
})
test('denying the camera does not discard a working microphone', async () => {
  const updates = [], audio = stream()
  const check = new InterviewDeviceCheck((...args) => updates.push(args), async constraints => {
    if (constraints.video) throw { name: 'NotAllowedError', message: 'private device name' }
    return audio
  })
  await check.start('microphone'); await check.start('camera')
  assert.equal(audio.track.stops, 0)
  assert.match(updates.at(-1)[2], /Permission blocked/)
  assert(!updates.at(-1)[2].includes('private device name'))
  check.dispose()
})
for (const action of ['stopAll', 'dispose']) test(`${action} invalidates an unresolved permission request and stops its late stream`, async () => {
  const gate = deferred(), audio = stream(), updates = []
  const check = new InterviewDeviceCheck((...args) => updates.push(args), () => gate.promise)
  const pending = check.start('microphone'); check[action](); const count = updates.length
  gate.resolve(audio); await pending
  assert.equal(audio.track.stops, 1); assert.equal(updates.length, count)
})
test('an old permission request cannot replace a newer device selection', async () => {
  const old = stream(), current = stream(), gate = deferred(), updates = []; let count = 0
  const check = new InterviewDeviceCheck((...args) => updates.push(args), () => ++count === 1 ? gate.promise : Promise.resolve(current))
  const pending = check.start('camera', 'old'); await check.start('camera', 'new')
  gate.resolve(old); await pending
  assert.equal(old.track.stops, 1); assert.equal(current.track.stops, 0); assert.equal(updates.at(-1)[1], current)
  check.dispose()
})
test('ended hardware clears preview and reports a retryable condition, not candidate suitability', async () => {
  const camera = stream(), updates = []
  const check = new InterviewDeviceCheck((...args) => updates.push(args), async () => camera)
  await check.start('camera'); camera.track.callbacks.ended()
  assert.equal(updates.at(-1)[1], null); assert.match(updates.at(-1)[2], /disconnected/)
  assert.equal(camera.track.stops, 1)
  check.dispose(); assert.equal(camera.track.stops, 1)
})
test('no automatic inference from silence; input level is bounded amplitude only', () => {
  assert.equal(interviewMicrophoneLevel(new Float32Array()), 0)
  assert.equal(interviewMicrophoneLevel(new Float32Array([0, 0])), 0)
  assert.equal(interviewMicrophoneLevel(new Float32Array([0.1, -0.1])), 35)
  assert.equal(interviewMicrophoneLevel(new Float32Array([1, -1])), 100)
})
test('device failure categories are useful without exposing raw browser errors', () => {
  assert.match(interviewDeviceError({ name: 'NotFoundError' }), /No device/)
  assert.match(interviewDeviceError({ name: 'NotReadableError' }), /in use/)
  assert.match(interviewDeviceError({ name: 'OverconstrainedError' }), /Selected device/)
  assert.match(interviewDeviceError(new Error('private diagnostics')), /HTTPS/)
})
