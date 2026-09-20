import fs from 'node:fs'
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'

// No secrets are persisted or logged. The repository test creates/drops ONLY its own random database.
const root = fileURLToPath(new URL('../', import.meta.url))
const settings = JSON.parse(fs.readFileSync(new URL('../Payroll.API/appsettings.Development.json', import.meta.url), 'utf8'))
const connection = settings.ConnectionStrings?.Default
if (!connection) throw new Error('Local development connection is missing.')
const parts = Object.fromEntries(connection.split(';').filter(Boolean).map(item => {
  const i = item.indexOf('='); return [item.slice(0, i).trim().toLowerCase(), item.slice(i + 1).trim()]
}))
if (!['localhost', '127.0.0.1', '::1'].includes(parts.server || parts.host)) throw new Error('Refusing a non-loopback test target.')
const run = spawn('dotnet', ['test', 'Payroll.API.Tests/Payroll.API.Tests.csproj', '--artifacts-path', `${root}.codex-validation/internal-interviews`, '--filter', 'FullyQualifiedName~InternalInterview', '--nologo', '--verbosity', 'quiet', '--logger', 'console;verbosity=normal'], {
  cwd: root, windowsHide: true, stdio: 'inherit', env: { ...process.env, HRMS_INTERNAL_INTERVIEW_TEST_CONNECTION: connection,
    ...(process.argv.includes('--portal') ? { HRMS_INTERVIEW_REPO: root, HRMS_TEST_UI_URL: process.env.HRMS_TEST_UI_URL || 'http://localhost:5184' } : {}),
  },
})
run.on('exit', code => { process.exitCode = code ?? 1 })
run.on('error', () => { console.error('Unable to start the backend tests.'); process.exitCode = 1 })
