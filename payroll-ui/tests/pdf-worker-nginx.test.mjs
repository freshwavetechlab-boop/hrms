import test from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

// Exercise the actual release artifact and Nginx configuration, without pulling
// images or contacting production. Run after npm run build where Docker and an
// existing nginx:alpine image are available.
const uiRoot = fileURLToPath(new URL('..', import.meta.url))
const docker = (...args) => spawnSync('docker', args, { encoding: 'utf8', timeout: 30_000, windowsHide: true })
const image = 'nginx:alpine'
const dockerReady = docker('image', 'inspect', image)

test('production Nginx serves the bundled PDF worker as JavaScript and preserves other asset routes', {
  skip: dockerReady.status !== 0 ? 'Docker and a locally available nginx:alpine image are required; no image is pulled.' : false,
  timeout: 60_000,
}, async () => {
  const dist = path.join(uiRoot, 'dist')
  const assets = fs.readdirSync(path.join(dist, 'assets'))
  const worker = assets.find(file => /^pdf\.worker.*\.mjs$/.test(file))
  assert.ok(worker, 'The built release must contain its PDF.js module worker.')
  const script = assets.find(file => file.endsWith('.js'))
  const style = assets.find(file => file.endsWith('.css'))
  const font = assets.find(file => file.endsWith('.ttf') || file.endsWith('.woff2'))
  assert.ok(script && style && font, 'Build the release before checking its asset routes.')

  const started = docker('run', '--detach', '--rm', '--publish', '127.0.0.1::80',
    '--mount', `type=bind,source=${dist},target=/usr/share/nginx/html,readonly`,
    '--mount', `type=bind,source=${path.join(uiRoot, 'nginx.conf')},target=/etc/nginx/conf.d/default.conf,readonly`, image)
  assert.equal(started.status, 0, started.stderr || started.error?.message)
  const container = started.stdout.trim()
  try {
    const port = docker('port', container, '80/tcp')
    assert.equal(port.status, 0, port.stderr)
    const origin = `http://${port.stdout.trim().split(/\r?\n/)[0]}`
    let ready = false
    for (let attempt = 0; attempt < 40; attempt++) {
      try { ready = (await fetch(origin, { signal: AbortSignal.timeout(500) })).ok } catch { /* Nginx is starting. */ }
      if (ready) break
      await new Promise(resolve => setTimeout(resolve, 100))
    }
    assert.ok(ready, 'Nginx must become reachable.')

    const response = await fetch(`${origin}/assets/${worker}`)
    assert.equal(response.status, 200)
    assert.match(response.headers.get('content-type') || '', /^(?:application|text)\/javascript(?:;|$)/i)
    assert.match(response.headers.get('cache-control') || '', /max-age=31536000.*immutable/)
    assert.equal((await response.arrayBuffer()).byteLength, fs.statSync(path.join(dist, 'assets', worker)).size)

    for (const [file, mime] of [[script, /javascript/], [style, /^text\/css/], [font, /(?:font|application\/octet-stream)/]]) {
      const asset = await fetch(`${origin}/assets/${file}`)
      assert.equal(asset.status, 200, file)
      assert.match(asset.headers.get('content-type') || '', mime, file)
      assert.deepEqual(Buffer.from(await asset.arrayBuffer()), fs.readFileSync(path.join(dist, 'assets', file)), file)
    }
    assert.equal((await fetch(`${origin}/assets/missing-pdf-worker.mjs`)).status, 404)
    const route = await fetch(`${origin}/reports/payroll-reports?report=payslip-register`)
    assert.equal(route.status, 200)
    assert.match(route.headers.get('content-type') || '', /^text\/html/)
    assert.match(route.headers.get('cache-control') || '', /no-store/)
  } finally {
    docker('rm', '--force', container)
  }
})
