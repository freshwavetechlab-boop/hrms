import fs from 'node:fs'
import path from 'node:path'
import { setTimeout as delay } from 'node:timers/promises'
import { reviewRepository, commitAndPush, validateApplications, verifyApplication, requestRedeploy, git } from './deploy-core.mjs'

const [mode, filename] = process.argv.slice(2)
if (mode === '--review') {
  try { console.log(JSON.stringify(reviewRepository(filename))) }
  catch (error) { console.error(error.message); process.exitCode = 1 }
} else if (mode === '--job') {
  await run(JSON.parse(fs.readFileSync(filename, 'utf8').replace(/^\uFEFF/, '')))
} else { console.error('Usage: node deploy.mjs --review <repository> | --job <job.json>'); process.exitCode = 1 }

async function run(job) {
  let context, browser, lockOwned = false
  const state = { JobId: job.Id, WorkerId: process.pid, Status: 'Starting', Message: 'Preparing deployment...', Commit: '', Apps: [] }
  const lock = path.join(job.DataDirectory, 'deploy.lock')
  const log = message => { fs.appendFileSync(job.LogFile, `${new Date().toISOString()} ${message}\n`); console.log(message) }
  const update = (status, message) => {
    Object.assign(state, { Status: status, Message: message })
    const temporary = `${job.StateFile}.${process.pid}.tmp`
    fs.writeFileSync(temporary, JSON.stringify(state, null, 2))
    fs.renameSync(temporary, job.StateFile)
    log(message)
  }
  const cancelled = () => fs.existsSync(job.CancelFile)
  const checkCancel = () => { if (cancelled()) throw new Error(state.Commit ? 'Git push completed; remaining Coolify redeploys cancelled.' : 'Cancelled. Check Git for any commit already made.') }
  try {
    validateApplications(job.Apps)
    const { chromium } = await import('playwright')
    try {
      if (fs.existsSync(lock)) {
        const owner = JSON.parse(fs.readFileSync(lock, 'utf8'))
        let alive = true
        try { process.kill(owner.pid, 0) } catch (error) { if (error.code === 'ESRCH') alive = false; else throw error }
        if (alive) throw new Error('Another deployment is already running. Finish it before starting again.')
        fs.unlinkSync(lock)
      }
      fs.writeFileSync(lock, JSON.stringify({ pid: process.pid, job: job.Id }), { flag: 'wx' })
      lockOwned = true
    } catch (error) { throw new Error(`Deployment lock: ${error.message}`) }
    update('Pushing', `Committing and pushing ${job.Review.branch} to ${job.Review.upstream}...`)
    state.Commit = commitAndPush(job.ProjectRoot, job.Message, job.Review, { log, cancelled })
    update('PreparingCoolify', `Git push verified: ${state.Commit.slice(0, 8)}. Opening Coolify...`)
    checkCancel()
    const options = { headless: false, channel: 'chrome', viewport: { width: 1440, height: 900 } }
    if (job.RememberSession) {
      context = await chromium.launchPersistentContext(path.join(job.DataDirectory, 'coolify-browser'), options)
    } else {
      browser = await chromium.launch(options)
      context = await browser.newContext({ viewport: options.viewport })
    }
    for (const app of job.Apps) {
      checkCancel()
      const page = await context.newPage()
      await page.goto(app.url, { waitUntil: 'domcontentloaded', timeout: 30000 })
      if (/\/(login|two-factor-challenge)(\/|$)/.test(new URL(page.url()).pathname)) {
        update('LoginRequired', 'Log in to Coolify in the opened Chrome window. Redeploy will still require confirmation here.')
        const deadline = Date.now() + 10 * 60 * 1000
        while (/\/(login|two-factor-challenge)(\/|$)/.test(new URL(page.url()).pathname)) {
          checkCancel()
          if (Date.now() > deadline) throw new Error('Coolify login timed out. Git was pushed; deploy manually or try again.')
          await delay(500)
        }
        await page.goto(app.url, { waitUntil: 'domcontentloaded', timeout: 30000 })
      }
      await page.getByRole('button', { name: 'Redeploy', exact: true }).waitFor({ state: 'visible', timeout: 30000 })
      await verifyApplication(page, app)
      state.Apps.push({ Name: app.name, Status: 'Ready', Url: app.url })
      app.page = page
    }
    update('AwaitingApproval', 'Git push completed. All three Coolify apps verified. Click Confirm redeploy to continue, or Cancel.')
    const approvalDeadline = Date.now() + 30 * 60 * 1000
    while (!fs.existsSync(job.ApprovalFile)) {
      checkCancel()
      if (Date.now() > approvalDeadline) throw new Error('Redeploy confirmation timed out. Git push completed; no redeploy requested.')
      await delay(500)
    }
    const approval = JSON.parse(fs.readFileSync(job.ApprovalFile, 'utf8').replace(/^\uFEFF/, ''))
    if (approval.JobId !== job.Id || approval.Commit !== state.Commit || approval.Approved !== true) throw new Error('Invalid deployment confirmation.')
    checkCancel()
    // Catch a competing push during login/approval, before touching Coolify.
    const latest = git(job.ProjectRoot, ['ls-remote', '--exit-code', job.Review.remote, job.Review.ref]).output.split(/\s/)[0]
    if (latest !== state.Commit) throw new Error('Remote branch changed while waiting for confirmation. Review the latest commit before redeploying.')
    // Verify every app again before the first click.
    for (const app of job.Apps) await verifyApplication(app.page, app)
    for (let index = 0; index < job.Apps.length; index++) {
      checkCancel()
      const app = job.Apps[index]
      state.Apps[index].Status = 'Requesting'
      update('Redeploying', `Requesting redeploy: ${app.name}...`)
      try {
        state.Apps[index].Url = await requestRedeploy(app.page, app)
        state.Apps[index].Status = 'Requested'
      } catch (error) { state.Apps[index].Status = 'Unverified'; throw error }
      update('Redeploying', `${app.name}: deployment log page opened; redeploy request verified.`)
    }
    update('Requested', 'All three redeploy requests verified. Build results are in the Coolify deployment links below.')
    // Keep the three deployment logs visible until the user closes this window.
    while (!fs.existsSync(job.CloseFile) && context.pages().some(page => !page.isClosed())) await delay(500)
  } catch (error) {
    update(cancelled() ? 'Cancelled' : 'Failed', error.message)
    process.exitCode = cancelled() ? 0 : 1
  } finally {
    if (context) await context.close().catch(() => {})
    if (browser) await browser.close().catch(() => {})
    if (lockOwned) fs.rmSync(lock, { force: true })
  }
}
