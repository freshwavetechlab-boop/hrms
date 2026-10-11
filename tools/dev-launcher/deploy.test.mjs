import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import http from 'node:http'
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'
import { test } from 'node:test'
import { chromium } from 'playwright'
import { git, reviewRepository, commitAndPush, validateApplications, verifyApplication, requestRedeploy } from './deploy-core.mjs'

function repository(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'frevo-deploy-test-'))
  const root = path.join(directory, 'work')
  const remote = path.join(directory, 'remote.git')
  fs.mkdirSync(root)
  t.after(() => {
    const target = path.resolve(directory)
    assert.ok(target.startsWith(path.resolve(os.tmpdir()) + path.sep) && path.basename(target).startsWith('frevo-deploy-test-'))
    fs.rmSync(target, { recursive: true, force: true })
  })
  git(directory, ['init', '--bare', remote])
  git(root, ['init', '-b', 'main'])
  git(root, ['config', 'user.name', 'Launcher Test'])
  git(root, ['config', 'user.email', 'launcher@example.invalid'])
  git(root, ['config', 'commit.gpgsign', 'false'])
  git(root, ['config', 'core.hooksPath', path.join(directory, 'no-hooks')])
  fs.writeFileSync(path.join(root, 'tracked.txt'), 'original\n')
  fs.writeFileSync(path.join(root, '.gitignore'), '.env\n')
  git(root, ['add', '.'])
  git(root, ['commit', '-m', 'Initial fixture'])
  git(root, ['remote', 'add', 'origin', remote])
  git(root, ['push', '-u', 'origin', 'main'])
  return { directory, root, remote }
}

test('commits reviewed files, treats the message literally, excludes ignored files and verifies a local push', t => {
  const { root } = repository(t)
  fs.writeFileSync(path.join(root, 'tracked.txt'), 'changed\n')
  fs.writeFileSync(path.join(root, 'new file.txt'), 'new\n')
  fs.writeFileSync(path.join(root, '.env'), 'test-secret-not-for-git\n')
  const message = 'Update `literal` $(no-execution) "quotes"\n\nSecond line'
  const commit = commitAndPush(root, message, reviewRepository(root))
  assert.equal(git(root, ['log', '-1', '--format=%B']).output, message)
  assert.equal(git(root, ['rev-parse', 'origin/main']).output, commit)
  assert.match(git(root, ['ls-tree', '-r', '--name-only', 'HEAD']).output, /new file.txt/)
  assert.doesNotMatch(git(root, ['ls-tree', '-r', '--name-only', 'HEAD']).output, /\.env/)
})

test('a clean repository does not create an empty commit', t => {
  const { root } = repository(t)
  const reviewed = reviewRepository(root)
  assert.equal(commitAndPush(root, 'No new changes', reviewed), reviewed.head)
  assert.equal(git(root, ['rev-list', '--count', 'HEAD']).output, '1')
})

test('content changes after review abort before staging or pushing', t => {
  const { root } = repository(t)
  fs.writeFileSync(path.join(root, 'tracked.txt'), 'first\n')
  const review = reviewRepository(root)
  fs.writeFileSync(path.join(root, 'tracked.txt'), 'second\n')
  assert.throws(() => commitAndPush(root, 'Changed', review), /changed after review/)
  assert.equal(git(root, ['diff', '--cached', '--name-only']).output, '')
  assert.equal(git(root, ['rev-parse', 'origin/main']).output, review.head)
})

test('an upstream that advanced is rejected without force pushing', t => {
  const { directory, root, remote } = repository(t)
  const other = path.join(directory, 'other')
  git(directory, ['clone', '-b', 'main', remote, other])
  git(other, ['config', 'user.name', 'Other fixture'])
  git(other, ['config', 'user.email', 'other@example.invalid'])
  git(other, ['config', 'commit.gpgsign', 'false'])
  fs.writeFileSync(path.join(other, 'other.txt'), 'remote advance\n')
  git(other, ['add', '.'])
  git(other, ['commit', '-m', 'Advance remote'])
  git(other, ['push'])
  const advanced = git(other, ['rev-parse', 'HEAD']).output
  fs.writeFileSync(path.join(root, 'tracked.txt'), 'local change\n')
  assert.throws(() => commitAndPush(root, 'Local change', reviewRepository(root)), /reject|fetch first|non-fast-forward/i)
  assert.equal(git(root, ['ls-remote', 'origin', 'refs/heads/main']).output.split(/\s/)[0], advanced)
})

test('validates distinct application URLs and rejects credentials, wrong routes and mixed origins', () => {
  const apps = ['admin', 'api', 'ess'].map(name => ({ name, url: `http://localhost:8000/project/p/environment/e/application/${name}` }))
  validateApplications(apps)
  assert.throws(() => validateApplications([apps[0], apps[0], apps[2]]), /different applications/)
  assert.throws(() => validateApplications([{ ...apps[0], url: 'http://user:pass@localhost:8000/project/p/environment/e/application/admin' }, ...apps.slice(1)]), /Configuration/)
  assert.throws(() => validateApplications([{ ...apps[0], url: 'http://localhost:8000/login' }, ...apps.slice(1)]), /Configuration/)
})

async function mockCoolify(t) {
  const requests = []
  const server = http.createServer((request, response) => {
    const url = new URL(request.url, 'http://localhost')
    if (url.pathname === '/request-deploy') {
      requests.push(url.searchParams.get('app'))
      response.writeHead(302, { Location: `/project/p/environment/e/application/${url.searchParams.get('app')}/deployment/test-${requests.length}` })
      return response.end()
    }
    const app = url.pathname.split('/')[6]
    response.setHeader('Content-Type', 'text/html')
    if (url.pathname.includes('/deployment/')) return response.end('<h1>Deployment queued</h1>')
    const name = app === 'wrong' ? 'different-app' : `hrms:${app}`
    const duplicate = app === 'duplicate' ? '<button>Redeploy</button>' : ''
    const action = app === 'unverified' ? 'void 0' : `location.href='/request-deploy?app=${app}'`
    response.end(`<label for="name">Name</label><input id="name" value="${name}"><button onclick="if(confirm('Redeploy this application?')) { ${action} }">Redeploy</button>${duplicate}`)
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  t.after(() => new Promise(resolve => server.close(resolve)))
  return { origin: `http://127.0.0.1:${server.address().port}`, requests }
}

test('Playwright verifies the app, clicks once and confirms the resulting deployment URL', async t => {
  const { origin, requests } = await mockCoolify(t)
  const browser = await chromium.launch({ channel: 'chrome', headless: true })
  t.after(() => browser.close())
  const page = await browser.newPage()
  const app = { name: 'hrms:admin', url: `${origin}/project/p/environment/e/application/admin` }
  await page.goto(app.url)
  await verifyApplication(page, app)
  assert.equal(requests.length, 0)
  const url = await requestRedeploy(page, app, { timeout: 3000 })
  assert.match(url, /\/deployment\/test-1$/)
  assert.deepEqual(requests, ['admin'])
  for (const id of ['wrong', 'duplicate']) {
    const invalid = { name: `hrms:${id}`, url: `${origin}/project/p/environment/e/application/${id}` }
    await page.goto(invalid.url)
    await assert.rejects(requestRedeploy(page, invalid, { timeout: 500 }), /name must be|single enabled/)
    assert.equal(requests.length, 1)
  }
  const uncertain = { name: 'hrms:unverified', url: `${origin}/project/p/environment/e/application/unverified` }
  await page.goto(uncertain.url)
  await assert.rejects(requestRedeploy(page, uncertain, { timeout: 500 }), /not verified.*Do not retry/)
})

test('a failed Git push stops the coordinator before opening Coolify', async t => {
  const { directory, root, remote } = repository(t)
  fs.writeFileSync(path.join(root, 'tracked.txt'), 'change\n')
  const reviewed = reviewRepository(root)
  fs.renameSync(remote, `${remote}-offline`)
  const { origin, requests } = await mockCoolify(t)
  const job = {
    Id: 'failed-push-fixture', ProjectRoot: root, DataDirectory: directory, Message: 'Test failed push', Review: reviewed,
    Apps: ['admin', 'api', 'ess'].map(name => ({ name: `hrms:${name}`, url: `${origin}/project/p/environment/e/application/${name}` })),
    StateFile: path.join(directory, 'state.json'), LogFile: path.join(directory, 'deploy.log'),
    ApprovalFile: path.join(directory, 'approval.json'), CancelFile: path.join(directory, 'cancel.json'), CloseFile: path.join(directory, 'close.json')
  }
  const jobFile = path.join(directory, 'job.json')
  fs.writeFileSync(jobFile, JSON.stringify(job))
  const child = spawn(process.execPath, [fileURLToPath(new URL('./deploy.mjs', import.meta.url)), '--job', jobFile], { windowsHide: true, stdio: 'ignore' })
  const exit = await new Promise(resolve => child.on('exit', resolve))
  assert.equal(exit, 1)
  assert.equal(JSON.parse(fs.readFileSync(job.StateFile)).Status, 'Failed')
  assert.deepEqual(requests, [])
})

test('coordinator pushes, waits for approval, supports cancellation, then requests all three exactly once', async t => {
  const { directory, root } = repository(t)
  fs.writeFileSync(path.join(root, 'tracked.txt'), 'change\n')
  const { origin, requests } = await mockCoolify(t)
  const job = {
    Id: 'approval-fixture', ProjectRoot: root, DataDirectory: directory, Message: 'Test approval boundary', Review: reviewRepository(root),
    Apps: ['admin', 'api', 'ess'].map(name => ({ name: `hrms:${name}`, url: `${origin}/project/p/environment/e/application/${name}` })),
    StateFile: path.join(directory, 'state.json'), LogFile: path.join(directory, 'deploy.log'),
    ApprovalFile: path.join(directory, 'approval.json'), CancelFile: path.join(directory, 'cancel.json'), CloseFile: path.join(directory, 'close.json')
  }
  const jobFile = path.join(directory, 'job.json')
  fs.writeFileSync(jobFile, JSON.stringify(job))
  const child = spawn(process.execPath, [fileURLToPath(new URL('./deploy.mjs', import.meta.url)), '--job', jobFile], { windowsHide: true, stdio: 'ignore' })
  const exited = new Promise(resolve => child.on('exit', resolve))
  t.after(() => { if (child.exitCode === null) child.kill() })
  let state
  const deadline = Date.now() + 30000
  while (Date.now() < deadline) {
    if (fs.existsSync(job.StateFile)) {
      state = JSON.parse(fs.readFileSync(job.StateFile))
      if (['AwaitingApproval', 'Failed'].includes(state.Status)) break
    }
    await delay(100)
  }
  assert.equal(state?.Status, 'AwaitingApproval', state?.Message)
  assert.equal(state.Apps.length, 3)
  assert.deepEqual(requests, [])
  assert.equal(git(root, ['rev-parse', 'origin/main']).output, state.Commit)
  fs.writeFileSync(job.CancelFile, '{}')
  await exited
  assert.equal(JSON.parse(fs.readFileSync(job.StateFile)).Status, 'Cancelled')
  assert.deepEqual(requests, [])
  assert.equal(git(root, ['rev-parse', 'origin/main']).output, state.Commit)
  // A fresh attempt on the already-pushed, clean repository must ask again.
  const approvedJob = {
    ...job, Id: 'approved-fixture', Review: reviewRepository(root),
    StateFile: path.join(directory, 'approved-state.json'), ApprovalFile: path.join(directory, 'approved-approval.json'),
    CancelFile: path.join(directory, 'approved-cancel.json'), CloseFile: path.join(directory, 'approved-close.json')
  }
  const approvedFile = path.join(directory, 'approved-job.json')
  fs.writeFileSync(approvedFile, JSON.stringify(approvedJob))
  const approvedChild = spawn(process.execPath, [fileURLToPath(new URL('./deploy.mjs', import.meta.url)), '--job', approvedFile], { windowsHide: true, stdio: 'ignore' })
  const approvedExit = new Promise(resolve => approvedChild.on('exit', resolve))
  t.after(() => { if (approvedChild.exitCode === null) approvedChild.kill() })
  const waitState = async wanted => {
    const deadline = Date.now() + 30000
    while (Date.now() < deadline) {
      if (fs.existsSync(approvedJob.StateFile)) {
        const current = JSON.parse(fs.readFileSync(approvedJob.StateFile))
        if (current.Status === 'Failed') throw new Error(current.Message)
        if (current.Status === wanted) return current
      }
      await delay(100)
    }
    throw new Error(`Timed out waiting for ${wanted}`)
  }
  const ready = await waitState('AwaitingApproval')
  assert.deepEqual(requests, [])
  fs.writeFileSync(approvedJob.ApprovalFile, JSON.stringify({ JobId: approvedJob.Id, Commit: ready.Commit, Approved: true }))
  const requested = await waitState('Requested')
  assert.deepEqual(requests, ['admin', 'api', 'ess'])
  assert.ok(requested.Apps.every(app => app.Status === 'Requested' && app.Url.includes('/deployment/')))
  fs.writeFileSync(approvedJob.CloseFile, '{}')
  assert.equal(await approvedExit, 0)
})
