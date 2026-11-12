import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync, existsSync } from 'node:fs'
import { runInNewContext } from 'node:vm'
import ts from 'typescript'

const exports = {}
const source = readFileSync(new URL('../src/utils/pwaInstall.ts', import.meta.url), 'utf8')
const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
new Function('exports', code)(exports)
const { createInstallController, connectInstallEvents, installPlatform } = exports
const prompt = (outcome = 'accepted') => Object.assign(new Event('beforeinstallprompt', { cancelable: true }), { prompt: async () => {}, userChoice: Promise.resolve({ outcome }) })

test('iOS including desktop-mode iPad gets usable Safari guidance, distinct from other mobile and desktop browsers', () => {
  assert.equal(installPlatform({ userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) Version/18.0 Mobile/15 Safari/604.1' }), 'ios-safari')
  assert.equal(installPlatform({ userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X) Version/18.0 Safari/605.1', platform: 'MacIntel', maxTouchPoints: 5 }), 'ios-safari')
  assert.equal(installPlatform({ userAgent: 'Mozilla/5.0 (iPhone) CriOS/130 Mobile/15 Safari/604.1' }), 'ios-browser')
  assert.equal(installPlatform({ userAgent: 'Mozilla/5.0 (iPhone) FxiOS/130 Mobile/15 Safari/604.1' }), 'ios-browser')
  assert.equal(installPlatform({ userAgent: 'Mozilla/5.0 (Linux; Android 15) Chrome/130 Mobile Safari/537.3' }), 'android')
  assert.equal(installPlatform({ userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X) Version/18 Safari/605' }), 'mac-safari')
  assert.equal(installPlatform({ userAgent: 'Mozilla/5.0 (Windows NT) Chrome/130 Safari/537.3 Edg/130' }), 'desktop')
})

test('only the native browser event enables installation, and accepting is not falsely reported as installed', async () => {
  const controller = createInstallController()
  assert.equal(await controller.requestInstall(), 'unavailable')
  const event = prompt()
  controller.capture(event)
  assert.equal(event.defaultPrevented, true)
  assert.equal(controller.getSnapshot().canPrompt, true)
  assert.equal(await controller.requestInstall(), 'accepted')
  assert.deepEqual(controller.getSnapshot(), { installed: false, canPrompt: false, pending: false, accepted: true })
  assert.equal(await controller.requestInstall(), 'unavailable', 'the browser event must only be used once')
  controller.setInstalled(true)
  assert.deepEqual(controller.getSnapshot(), { installed: true, canPrompt: false, pending: false, accepted: false })
})

test('duplicate clicks cannot consume the same prompt twice and listener state remains stable until changed', async () => {
  const controller = createInstallController()
  const notifications = []
  const stop = controller.subscribe(() => notifications.push(controller.getSnapshot()))
  const initial = controller.getSnapshot()
  assert.equal(controller.getSnapshot(), initial)
  let complete, calls = 0
  controller.capture(Object.assign(prompt(), { prompt: async () => { calls++ }, userChoice: new Promise(resolve => { complete = resolve }) }))
  const pending = controller.requestInstall()
  assert.equal(controller.getSnapshot().pending, true)
  assert.equal(await controller.requestInstall(), 'unavailable')
  complete({ outcome: 'dismissed' })
  assert.equal(await pending, 'dismissed')
  assert.equal(calls, 1)
  assert.equal(controller.getSnapshot().installed, false)
  assert.equal(controller.getSnapshot().accepted, false)
  const count = notifications.length
  stop()
  controller.setInstalled(true)
  assert.equal(notifications.length, count)
})

test('rejected native prompts recover without retaining a spent prompt or claiming installation', async () => {
  const controller = createInstallController()
  controller.capture(Object.assign(prompt(), { prompt: async () => { throw new Error('browser declined') } }))
  assert.equal(await controller.requestInstall(), 'failed')
  assert.deepEqual(controller.getSnapshot(), { installed: false, canPrompt: false, pending: false, accepted: false })
  controller.capture(prompt('dismissed'))
  assert.equal(await controller.requestInstall(), 'dismissed')
})

test('standalone and appinstalled events hide installation, survive page restores, and detach cleanly', async () => {
  const browser = new EventTarget(), media = new EventTarget()
  media.matches = false
  browser.matchMedia = () => media
  const controller = createInstallController()
  const disconnect = connectInstallEvents(controller, browser, {})
  browser.dispatchEvent(prompt())
  assert.equal(controller.getSnapshot().canPrompt, true)
  browser.dispatchEvent(new Event('appinstalled'))
  browser.dispatchEvent(new Event('pageshow'))
  assert.equal(controller.getSnapshot().installed, true)
  assert.equal(await controller.requestInstall(), 'unavailable')
  disconnect()
  const standalone = createInstallController()
  media.matches = true
  connectInstallEvents(standalone, browser, {})()
  assert.equal(standalone.getSnapshot().installed, true)
  const ios = createInstallController()
  media.matches = false
  connectInstallEvents(ios, browser, { standalone: true })()
  assert.equal(ios.getSnapshot().installed, true)
  const detached = createInstallController()
  connectInstallEvents(detached, browser, {})()
  browser.dispatchEvent(prompt())
  assert.equal(detached.getSnapshot().canPrompt, false)
})

function workerHarness({ offline = false, cached = null, privateResponse = false, quotaError = false } = {}) {
  const handlers = {}, writes = [], deleted = [], readRequests = [], networkRequests = []
  const cache = {
    addAll: async () => {},
    match: async request => { readRequests.push(request); return cached },
    put: async (...args) => { writes.push(args); if (quotaError) throw new Error('quota full') },
  }
  const caches = { open: async () => cache, keys: async () => ['other-app-cache', 'frevo-one-ess-v3', 'frevo-one-ess-v4'], delete: async key => deleted.push(key) }
  const self = { location: { origin: 'https://ess.example.test' }, addEventListener: (name, handler) => { handlers[name] = handler }, skipWaiting: async () => {}, clients: { claim: async () => {} } }
  const fetch = async request => { networkRequests.push(request); if (offline) throw new Error('offline'); return new Response('app asset', { headers: privateResponse ? { 'Cache-Control': 'private, no-store' } : {} }) }
  runInNewContext(readFileSync(new URL('../public/sw.js', import.meta.url), 'utf8'), { self, caches, fetch, URL, Response })
  const dispatch = async request => {
    let response
    const work = []
    handlers.fetch({ request, respondWith: value => { response = value }, waitUntil: value => work.push(value) })
    const result = await response
    await Promise.all(work)
    return result
  }
  return { handlers, dispatch, writes, deleted, readRequests, networkRequests }
}
const request = (path, options = {}) => new Request(`https://ess.example.test${path}`, options)

test('worker bypasses every API, punch, authorization request, private download and other origin', async () => {
  const worker = workerHarness()
  assert.deepEqual(Object.keys(worker.handlers).sort(), ['activate', 'fetch', 'install'], 'no background-sync or replay handlers are registered')
  for (const entry of [request('/api/ess/punch', { method: 'POST', body: '{}' }), request('/api/ess/profile'), request('/API/ess/profile'), request('/api'), request('/assets/app-abcdefgh.js', { headers: { Authorization: 'Bearer synthetic' } }), request('/assets/photo-private.jpg'), request('/download/payslip.pdf'), request('/assets/app-abcdefgh.js?token=private'), new Request('https://api.other.test/assets/app-abcdefgh.js')]) assert.equal(await worker.dispatch(entry), undefined)
  assert.equal(worker.networkRequests.length, 0)
  assert.equal(worker.writes.length, 0)
})

test('worker caches only frontend assets and respects server no-store/private responses and quota failures', async () => {
  const worker = workerHarness()
  const asset = request('/assets/index-abcdefgh.js')
  assert.equal(await (await worker.dispatch(asset)).text(), 'app asset')
  assert.equal(worker.writes.length, 1)
  const privateWorker = workerHarness({ privateResponse: true })
  assert.equal((await privateWorker.dispatch(asset)).status, 200)
  assert.equal(privateWorker.writes.length, 0)
  const quota = workerHarness({ quotaError: true })
  assert.equal((await quota.dispatch(asset)).status, 200)
})

test('offline navigation returns this app shell, absent assets fail safely, and cache cleanup leaves other apps alone', async () => {
  const offline = workerHarness({ offline: true, cached: new Response('shell') })
  const navigation = { ...request('/'), url: 'https://ess.example.test/', method: 'GET', headers: new Headers(), mode: 'navigate' }
  assert.equal(await (await offline.dispatch(navigation)).text(), 'shell')
  assert.equal(offline.readRequests[0], '/index.html')
  const missing = workerHarness({ offline: true })
  assert.equal((await missing.dispatch(navigation)).type, 'error')
  assert.equal((await missing.dispatch(request('/assets/index-abcdefgh.js'))).type, 'error')
  let activation
  missing.handlers.activate({ waitUntil: value => { activation = value } })
  await activation
  assert.deepEqual(missing.deleted, ['frevo-one-ess-v3'])
})

test('manifest has stable identity, an in-scope start, real PNG sizes and a separate maskable safe-zone icon', () => {
  const manifest = JSON.parse(readFileSync(new URL('../public/manifest.webmanifest', import.meta.url), 'utf8'))
  assert.equal(manifest.id, '/')
  assert.equal(manifest.scope, '/')
  assert.ok(manifest.start_url.startsWith(manifest.scope))
  assert.equal(manifest.display, 'standalone')
  assert.notEqual(manifest.orientation, 'portrait-primary', 'desktop and tablet layouts must not be locked to portrait')
  for (const size of [192, 512]) {
    const icon = manifest.icons.find(icon => icon.sizes === `${size}x${size}` && icon.purpose === 'any')
    assert.ok(icon)
    const png = readFileSync(new URL(`../public${icon.src}`, import.meta.url))
    assert.equal(png.readUInt32BE(16), size)
    assert.equal(png.readUInt32BE(20), size)
  }
  const maskable = manifest.icons.find(icon => icon.purpose === 'maskable')
  assert.ok(maskable && !manifest.icons.some(icon => icon.purpose === 'any' && icon.src === maskable.src))
  assert.ok(existsSync(new URL(`../public${maskable.src}`, import.meta.url)))
})
