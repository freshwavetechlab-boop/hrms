import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const load = name => ts.transpileModule(readFileSync(new URL(`../src/services/${name}.ts`, import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText
const voice = vm.createContext({ exports: {} }); vm.runInContext(load('interviewVoicePlayback'), voice)
const sandbox = vm.createContext({ exports: {}, require: () => voice.exports }); vm.runInContext(load('interviewRoomDevices'), sandbox)
const { startInterviewRoomDevices, switchInterviewRoomDevice } = sandbox.exports
function fixture(cameraFail = false) {
  const calls = []
  const room = { localParticipant: {
    setCameraEnabled: async value => { calls.push(['camera', value]); if (cameraFail) throw Error('Permission blocked') },
    setMicrophoneEnabled: async value => { calls.push(['microphone', value]) },
    trackPublications: new Map([
      ['human', { source: 'microphone', trackName: 'microphone', track: { setDeviceId: async id => { calls.push(['human-mic-device', id]); return !!id } } }],
      ['tts', { source: 'microphone', trackName: 'hrms-interviewer-voice', track: { setDeviceId: async () => { throw Error('Must not modify AI voice') } } }],
    ]),
  }, switchActiveDevice: async (...args) => { calls.push(['device', ...args]); return !!args[1] } }
  return { calls, room }
}
test('join with both inputs off never requests or enables camera/microphone', async () => {
  const f = fixture()
  await startInterviewRoomDevices(f.room, { cameraEnabled: false, microphoneEnabled: false, speakerId: '' })
  assert.deepEqual(f.calls, [])
})
test('camera rejection retains independent microphone and selected speaker results', async () => {
  const f = fixture(true)
  const results = await startInterviewRoomDevices(f.room, { cameraEnabled: true, microphoneEnabled: true, speakerId: 'headset' })
  assert.equal(results[0].status, 'rejected'); assert.equal(results[1].status, 'fulfilled'); assert.equal(results[2].status, 'fulfilled')
  assert.deepEqual(f.calls.at(-1), ['device', 'audiooutput', 'headset'])
})
test('microphone selection targets only human mic and never restarts AI TTS', async () => {
  const f = fixture(); await switchInterviewRoomDevice(f.room, 'audioinput', 'mic-2')
  assert.equal(f.calls.length, 1); assert.equal(f.calls[0][0], 'human-mic-device'); assert.equal(f.calls[0][1].exact, 'mic-2')
})
test('switching a not-yet-enabled mic does not request permission or enable it', async () => {
  const f = fixture(); f.room.localParticipant.trackPublications.delete('human')
  await switchInterviewRoomDevice(f.room, 'audioinput', 'mic-2'); assert.equal(f.calls.length, 0)
})
test('system-default routing allows SDK physical-ID differences', async () => {
  const f = fixture()
  await switchInterviewRoomDevice(f.room, 'videoinput', ''); await switchInterviewRoomDevice(f.room, 'audioinput', '')
  assert.deepEqual(f.calls[0], ['device', 'videoinput', '', false]); assert.deepEqual(f.calls[1], ['human-mic-device', ''])
})
test('failed explicit device switch remains an error, not a successful preference update', async () => {
  const f = fixture(); f.room.switchActiveDevice = async () => false
  await assert.rejects(switchInterviewRoomDevice(f.room, 'audiooutput', 'missing'), /unavailable/)
})
