import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const source = ts.transpileModule(readFileSync(new URL('../src/services/interviewVoicePlayback.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r }); return { promise, resolve } }
function fixture(decode) {
  let context
  class AudioContext {
    constructor() { context = this; this.destination = {}; this.closes = 0; this.stops = 0; this.starts = 0; this.connections = []; this.track = { stop: () => this.stops++ } }
    resume() { return Promise.resolve() }
    close() { this.closes++; return Promise.resolve() }
    createMediaStreamDestination() { return { stream: { getTracks: () => [this.track], getAudioTracks: () => [this.track] }, disconnect() {} } }
    decodeAudioData() { return decode ? decode.promise : Promise.resolve({ duration: 1 }) }
    createBufferSource() { const node = { connect: destination => this.connections.push(destination), disconnect() {}, start: () => this.starts++, stop() {}, onended: null }; this.node = node; return node }
  }
  const sandbox = vm.createContext({ exports: {}, AudioContext, Error })
  vm.runInContext(source, sandbox)
  return { playback: new sandbox.exports.InterviewVoicePlayback(), audio: () => context, api: sandbox.exports }
}
const clip = { arrayBuffer: async () => new ArrayBuffer(16) }

test('local-only question playback is explicitly not room delivery and cleanup is idempotent', async () => {
  const f = fixture(); let ended = 0
  assert.equal(await f.playback.play(clip, undefined, () => ended++), false)
  assert.equal(f.audio().starts, 1); assert.equal(f.audio().connections.length, 2)
  f.audio().node.onended(); f.playback.stop()
  assert.equal(ended, 1); assert.equal(f.audio().closes, 1); assert.equal(f.audio().stops, 1)
})
test('room publish acknowledgement precedes audio start; end unpublishes only this clip', async () => {
  const gate = deferred(), f = fixture(); let released = 0
  const pending = f.playback.play(clip, async track => { assert.equal(track, f.audio().track); await gate.promise; return async () => released++ }, () => {})
  await new Promise(r => setImmediate(r)); assert.equal(f.audio().starts, 0)
  gate.resolve(); assert.equal(await pending, true)
  f.playback.stop(); await new Promise(r => setImmediate(r))
  assert.equal(f.audio().starts, 1); assert.equal(released, 1)
})
test('question change while decoding cannot start or publish stale speech', async () => {
  const decode = deferred(), f = fixture(decode); let published = 0
  const pending = f.playback.play(clip, async () => { published++; return async () => {} }, () => {})
  f.playback.stop(); decode.resolve({ duration: 1 })
  assert.equal(await pending, false); assert.equal(published, 0); assert.equal(f.audio().starts, 0)
})
test('late publication after unmount is released and never starts old audio', async () => {
  const gate = deferred(), f = fixture(); let released = 0
  const pending = f.playback.play(clip, async () => { await gate.promise; return async () => released++ }, () => {})
  await new Promise(r => setImmediate(r)); f.playback.stop(); gate.resolve()
  assert.equal(await pending, false); assert.equal(released, 1); assert.equal(f.audio().starts, 0)
})
test('publication failure is visible instead of claiming the question was recorded', async () => {
  const f = fixture()
  await assert.rejects(f.playback.play(clip, async () => { throw Error('Disconnected') }, () => {}), /Disconnected/)
  assert.equal(f.audio().starts, 0); assert.equal(f.audio().closes, 1); assert.equal(f.audio().stops, 1)
})
test('a clip cannot be started twice and TTS does not become the candidate microphone', async () => {
  const f = fixture(); await f.playback.play(clip, undefined, () => {})
  assert.equal(await f.playback.play(clip, undefined, () => {}), false); assert.equal(f.audio().starts, 1)
  assert.equal(f.api.isInterviewMicrophone({ source: 'microphone', trackName: f.api.interviewerVoiceTrackName }), false)
  assert.equal(f.api.isInterviewMicrophone({ source: 'microphone', trackName: 'microphone' }), true)
  assert.equal(f.api.isInterviewMicrophone({ source: 'camera', trackName: 'camera' }), false)
  f.playback.stop()
})
