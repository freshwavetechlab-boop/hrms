import assert from 'node:assert/strict'
import path from 'node:path'

// Additive browser checks reused by the existing isolated interview UI harness.
// Chrome's synthetic media devices are explicit; no webcam, microphone, DB or media service claim.
export async function installInterviewDeviceFixture(context) {
  await context.addInitScript(() => {
    const original = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices)
    window.interviewDevices = { mode: 'normal', calls: [], tracks: [], release: null }
    navigator.mediaDevices.getUserMedia = async constraints => {
      const state = window.interviewDevices
      state.calls.push(constraints)
      if (constraints.video && state.mode === 'deny-camera') throw new DOMException('Synthetic denial', 'NotAllowedError')
      if (constraints.audio && state.mode === 'deny-microphone') throw new DOMException('Synthetic denial', 'NotAllowedError')
      if (constraints.video && state.mode === 'missing-camera') throw new DOMException('Synthetic absent device', 'NotFoundError')
      if (constraints.video && state.mode === 'busy-camera') throw new DOMException('Synthetic busy device', 'NotReadableError')
      if (constraints.video && state.mode === 'pending-camera') await new Promise(resolve => { state.release = resolve })
      const stream = await original(constraints)
      state.tracks.push(...stream.getTracks())
      return stream
    }
  })
}

export async function verifyInterviewDeviceChecks(page, expect, checks, output) {
  const check = page.getByRole('button', { name: 'Check camera & microphone', exact: true })
  const stop = page.getByRole('button', { name: 'Stop device check', exact: true })
  const activeTracks = () => page.evaluate(() => window.interviewDevices.tracks.filter(track => track.readyState === 'live').length)
  const mode = value => page.evaluate(value => { window.interviewDevices.mode = value }, value)
  const sensitiveRequests = []
  const listener = request => { if (/\/join$|\/speech\/|\/recordings/.test(new URL(request.url()).pathname)) sensitiveRequests.push(request.url()) }
  page.on('request', listener)
  await page.bringToFront()
  await page.evaluate(() => {
    const banner = document.createElement('div'); banner.id = 'interview-device-test-banner'
    banner.textContent = 'TEST MODE · Camera/audio checks · Synthetic browser devices · No production data'
    banner.style.cssText = 'position:fixed;bottom:8px;left:8px;right:8px;z-index:99999;background:#17385b;color:white;padding:12px;border-radius:10px;text-align:center;pointer-events:none'
    document.body.appendChild(banner)
  })
  await expect(check).toBeVisible()
  assert.equal(await page.evaluate(() => window.interviewDevices.calls.length), 0)
  checks.push('pre-join camera/audio controls do not automatically request device permissions')
  await check.click()
  await expect(page.getByText('Camera preview ready — check your framing.', { exact: true })).toBeVisible()
  await expect.poll(activeTracks).toBe(2)
  const video = page.getByLabel('Your local camera preview')
  assert.equal(await video.evaluate(element => element.muted), true)
  assert((await video.evaluate(element => element.videoWidth)) > 0)
  await expect(page.getByText('Microphone signal detected.', { exact: true })).toBeVisible({ timeout: 15000 })
  const level = Number(await page.getByRole('meter', { name: 'Microphone input level' }).getAttribute('aria-valuenow'))
  assert(level >= 0 && level <= 100)
  await page.getByRole('button', { name: 'Test speaker', exact: true }).click()
  await expect(page.getByText(/Did you hear it\?/)).toBeVisible()
  await expect(page.getByText('Speaker confirmed by you.', { exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: 'I heard the test sound', exact: true }).click()
  await expect(page.getByText('Speaker confirmed by you.', { exact: true })).toBeVisible()
  await page.locator('.interview-device-check').screenshot({ path: path.join(output, 'camera-audio-check.png') })
  checks.push('synthetic camera renders real frames; muted preview, bounded live mic meter and manual-only speaker confirmation')
  await stop.click(); await expect.poll(activeTracks).toBe(0)
  await expect(video).toHaveJSProperty('srcObject', null)
  await mode('deny-camera'); await check.click()
  await expect(page.getByText(/Permission blocked\./)).toBeVisible(); await expect.poll(activeTracks).toBe(1)
  await expect(page.getByText('Microphone signal detected.', { exact: true })).toBeVisible({ timeout: 15000 })
  await stop.click(); await expect.poll(activeTracks).toBe(0)
  await mode('deny-microphone'); await check.click()
  await expect(page.getByText(/Permission blocked\./)).toBeVisible()
  await expect(page.getByText('Camera preview ready — check your framing.', { exact: true })).toBeVisible()
  await expect.poll(activeTracks).toBe(1)
  await stop.click()
  checks.push('camera/microphone permission failures are independent, actionable and retryable')
  for (const [value, text] of [['missing-camera', 'No device found.'], ['busy-camera', 'Device is unavailable or in use.']]) {
    await mode(value); await check.click()
    await expect(page.getByText(text, { exact: false })).toBeVisible(); await stop.click()
  }
  checks.push('missing and busy hardware have separate messages')
  await mode('pending-camera'); await check.click()
  await expect(page.getByText(/Waiting for browser permissions/)).toBeVisible()
  await stop.click(); await mode('normal')
  const beforeLateStream = await page.evaluate(() => window.interviewDevices.tracks.length)
  await page.evaluate(() => window.interviewDevices.release())
  await expect.poll(() => page.evaluate(() => window.interviewDevices.tracks.length)).toBeGreaterThan(beforeLateStream)
  await expect.poll(activeTracks).toBe(0)
  await check.click(); await expect.poll(activeTracks).toBe(2)
  await page.evaluate(() => {
    const camera = window.interviewDevices.tracks.find(track => track.kind === 'video' && track.readyState === 'live')
    camera.stop(); camera.dispatchEvent(new Event('ended'))
  })
  await expect(page.getByText(/Device disconnected or permission revoked/)).toBeVisible()
  await expect.poll(activeTracks).toBe(1); await stop.click()
  checks.push('cancel invalidates late permission replies; simulated device removal clears preview and permits retry')
  await page.getByRole('checkbox', { name: 'Join with camera on', exact: true }).uncheck()
  await check.click(); await expect.poll(activeTracks).toBe(1)
  assert.equal(await page.evaluate(() => window.interviewDevices.tracks.filter(track => track.readyState === 'live')[0].kind), 'audio')
  await page.getByRole('checkbox', { name: 'Join with microphone on', exact: true }).uncheck()
  await expect.poll(activeTracks).toBe(0); await expect(check).toBeDisabled()
  await page.getByRole('checkbox', { name: 'Join with camera on', exact: true }).check()
  await page.getByRole('checkbox', { name: 'Join with microphone on', exact: true }).check()
  const selected = await page.evaluate(async () => {
    const all = await navigator.mediaDevices.enumerateDevices()
    return { camera: all.find(device => device.kind === 'videoinput')?.toJSON(), microphone: all.find(device => device.kind === 'audioinput' && device.deviceId !== 'default')?.toJSON() }
  })
  for (const [kind, device] of Object.entries(selected)) {
    assert(device.deviceId)
    const select = page.getByRole('combobox', { name: `Interview ${kind} device` })
    await page.locator('.ant-select').filter({ has: select }).locator('.ant-select-selector').click()
    // Target the device label, not an old dropdown still fading out from the previous selector.
    await page.locator('.ant-select-dropdown').getByText(device.label, { exact: true }).click()
  }
  await check.click(); await expect.poll(activeTracks).toBe(2)
  const calls = await page.evaluate(() => window.interviewDevices.calls.slice(-2))
  assert.equal(calls.find(call => call.video).video.deviceId.exact, selected.camera.deviceId)
  assert.equal(calls.find(call => call.audio).audio.deviceId.exact, selected.microphone.deviceId)
  checks.push('pre-join camera/microphone off respected; selected devices use exact IDs')
  await page.setViewportSize({ width: 390, height: 844 })
  await page.locator('.interview-device-check').screenshot({ path: path.join(output, 'camera-audio-mobile.png') })
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false)
  await page.setViewportSize({ width: 1366, height: 900 })
  await stop.click(); await expect.poll(activeTracks).toBe(0)
  assert.deepEqual(sensitiveRequests, [])
  page.off('request', listener)
  await page.evaluate(() => document.getElementById('interview-device-test-banner')?.remove())
  checks.push('device checks fit mobile and make no join/speech/recording request; Stop releases tracks')
  await check.click(); await expect.poll(activeTracks).toBe(2)
  await page.evaluate(() => { location.hash = 'access=invalid' })
  await expect(page.getByText('The interview link is invalid or expired.', { exact: true })).toBeVisible()
  await expect.poll(activeTracks).toBe(0)
  await page.evaluate(() => { location.hash = 'access=synthetic-link' })
  await expect(check).toBeVisible(); assert.equal(await activeTracks(), 0)
  checks.push('candidate link change/unmount releases all preview tracks; re-entry never auto-captures')
}
