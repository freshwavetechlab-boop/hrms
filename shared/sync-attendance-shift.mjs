import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'

// Commit these generated copies so each frontend can build from its own folder.
const read = url => readFileSync(url, 'utf8').replace(/\r\n/g, '\n')
const source = read(new URL('./attendanceShift.ts', import.meta.url))
const generated = '// Generated from shared/attendanceShift.ts; run node shared/sync-attendance-shift.mjs to update.\n' + source
for (const app of ['payroll-ui', 'ess-mss']) {
  const target = new URL(`../${app}/src/shared/attendanceShift.ts`, import.meta.url)
  if (process.argv.includes('--check')) {
    if (read(target) !== generated) throw new Error(`${app}: attendance helper is out of date. Run node shared/sync-attendance-shift.mjs and commit both copies.`)
  } else {
    mkdirSync(new URL('./', target), { recursive: true })
    writeFileSync(target, generated)
  }
}
