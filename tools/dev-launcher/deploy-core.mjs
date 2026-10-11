import fs from 'node:fs'
import path from 'node:path'
import { createHash } from 'node:crypto'
import { spawnSync } from 'node:child_process'

export function git(root, args, { allowed = [0], log = () => {} } = {}) {
  const result = spawnSync('git', args, { cwd: root, encoding: 'utf8', windowsHide: true, timeout: 300000, maxBuffer: 64 * 1024 * 1024 })
  if (result.error) throw result.error
  if (!allowed.includes(result.status)) throw new Error(result.stderr.trim() || result.stdout.trim() || `Git exited ${result.status}.`)
  if (result.stderr.trim()) log(result.stderr.trim())
  return { output: result.stdout.trim(), raw: result.stdout, code: result.status }
}

export function reviewRepository(root) {
  const top = git(root, ['rev-parse', '--show-toplevel']).output
  const normalize = value => path.resolve(value).replaceAll('\\', '/').toLowerCase()
  if (normalize(top) !== normalize(root)) throw new Error('Choose the Git repository root, not an individual project folder.')
  const branch = git(root, ['symbolic-ref', '--quiet', '--short', 'HEAD']).output
  const head = git(root, ['rev-parse', 'HEAD']).output
  const remote = git(root, ['config', '--get', `branch.${branch}.remote`]).output
  const ref = git(root, ['config', '--get', `branch.${branch}.merge`]).output
  if (remote === '.' || !ref.startsWith('refs/heads/')) throw new Error('The branch must track a remote Git branch.')
  for (const name of ['MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-merge', 'rebase-apply']) {
    const file = git(root, ['rev-parse', '--git-path', name]).output
    if (fs.existsSync(path.resolve(root, file))) throw new Error('Finish the current merge/rebase/cherry-pick before deploying.')
  }
  const status = git(root, ['status', '--short', '--untracked-files=all']).output
  const fingerprint = createHash('sha256').update(branch).update(head).update(remote).update(ref).update(status)
  fingerprint.update(git(root, ['diff', 'HEAD', '--binary', '--no-ext-diff']).raw)
  const untracked = git(root, ['ls-files', '--others', '--exclude-standard', '-z']).raw.split('\0').filter(Boolean)
  for (const file of untracked) {
    fingerprint.update(file)
    const absolute = path.join(root, file)
    fingerprint.update(fs.lstatSync(absolute).isSymbolicLink() ? fs.readlinkSync(absolute) : fs.readFileSync(absolute))
  }
  return { root: top, branch, head, remote, ref, upstream: `${remote}/${ref.slice('refs/heads/'.length)}`, status, fingerprint: fingerprint.digest('hex') }
}

export function assertReview(current, reviewed) {
  if (current.fingerprint !== reviewed.fingerprint) throw new Error('Files or Git branch changed after review. Refresh the file list and try again.')
}

export function commitAndPush(root, message, reviewed, { log = () => {}, cancelled = () => false } = {}) {
  const checkCancel = () => { if (cancelled()) throw new Error('Cancelled. Any commit already made stays in Git.') }
  assertReview(reviewRepository(root), reviewed)
  if (!message?.trim()) throw new Error('Enter a commit message.')
  checkCancel()
  git(root, ['add', '--all', '--', '.'], { log })
  const staged = git(root, ['diff', '--cached', '--quiet'], { allowed: [0, 1] }).code === 1
  if (staged) {
    checkCancel()
    const messageFile = path.join(git(root, ['rev-parse', '--absolute-git-dir']).output, `frevo-commit-${process.pid}.txt`)
    try {
      fs.writeFileSync(messageFile, message.trim(), 'utf8')
      log(git(root, ['commit', '--file', messageFile], { log }).output)
    } finally { fs.rmSync(messageFile, { force: true }) }
  } else log('No changed files; pushing the current commit.')
  if (git(root, ['status', '--porcelain', '--untracked-files=all']).output) throw new Error('Files changed during commit. Review them before pushing.')
  const afterCommit = reviewRepository(root)
  if (afterCommit.branch !== reviewed.branch || afterCommit.remote !== reviewed.remote || afterCommit.ref !== reviewed.ref) {
    throw new Error('Git branch or upstream changed during commit. Review it before pushing.')
  }
  checkCancel()
  const commit = git(root, ['rev-parse', 'HEAD']).output
  // Explicit refspec, no force push, no automatic pull/merge.
  git(root, ['push', reviewed.remote, `HEAD:${reviewed.ref}`], { log })
  const remoteHead = git(root, ['ls-remote', '--exit-code', reviewed.remote, reviewed.ref]).output.split(/\s/)[0]
  if (remoteHead !== commit) throw new Error('Remote branch does not match the pushed commit. Check Git before redeploying.')
  return commit
}

export function validateApplications(apps) {
  if (!Array.isArray(apps) || apps.length !== 3) throw new Error('Configure exactly three Coolify application URLs.')
  const origins = new Set()
  const urls = new Set()
  for (const app of apps) {
    const url = new URL(app.url)
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash ||
      !/^\/project\/[^/]+\/environment\/[^/]+\/application\/[^/]+\/?$/.test(url.pathname)) {
      throw new Error(`Paste the Coolify Configuration page URL for ${app.name}.`)
    }
    if (!app.name?.trim()) throw new Error('Every app needs its expected Coolify name.')
    origins.add(url.origin)
    urls.add(url.href.replace(/\/$/, ''))
  }
  if (origins.size !== 1 || urls.size !== 3) throw new Error('Use three different applications on the same Coolify instance.')
}

export async function verifyApplication(page, app) {
  const expected = new URL(app.url)
  const actual = new URL(page.url())
  if (actual.origin !== expected.origin || actual.pathname.replace(/\/$/, '') !== expected.pathname.replace(/\/$/, '')) {
    throw new Error(`Not on the configured page for ${app.name}.`)
  }
  const name = page.locator('input[id="name"], input[name="name"], input[wire\\:model="application.name"], input[wire\\:model\\.defer="application.name"]')
  if (await name.count() !== 1 || (await name.inputValue()).trim() !== app.name) {
    throw new Error(`Coolify application name must be ${app.name}. Check the URL/name in the deployment window.`)
  }
  const button = page.getByRole('button', { name: 'Redeploy', exact: true })
  if (await button.count() !== 1 || !await button.isVisible() || !await button.isEnabled()) {
    throw new Error(`A single enabled Redeploy button was not found for ${app.name}.`)
  }
  return button
}

export async function requestRedeploy(page, app, { timeout = 30000 } = {}) {
  const button = await verifyApplication(page, app)
  const expected = new URL(app.url)
  const prefix = `${expected.pathname.replace(/\/$/, '')}/deployment/`
  let rejectedDialog = false
  const dialogHandler = async dialog => {
    // Only accept a deployment confirmation; never blanket-accept browser dialogs.
    if (dialog.type() === 'confirm' && /deploy/i.test(dialog.message()) && !/stop|restart|delete|remove/i.test(dialog.message())) await dialog.accept()
    else { rejectedDialog = true; await dialog.dismiss() }
  }
  page.on('dialog', dialogHandler)
  try {
    const navigation = page.waitForURL(url => url.origin === expected.origin && url.pathname.startsWith(prefix) && url.pathname.length > prefix.length, { timeout })
    // Observe the rejection immediately even if clicking fails first.
    navigation.catch(() => {})
    await button.click({ timeout })
    await navigation
    if (rejectedDialog) throw new Error('An unexpected confirmation appeared. Review Coolify manually.')
    return page.url()
  } catch (error) {
    throw new Error(`${app.name}: deployment was not verified. Do not retry automatically; check its Coolify Deployments tab. ${error.message}`)
  } finally { page.off('dialog', dialogHandler) }
}
