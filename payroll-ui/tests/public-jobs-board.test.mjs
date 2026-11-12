import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const compiled = ts.transpileModule(readFileSync(new URL('../src/utils/publicJobsBoard.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
const exports = {}
new Function('exports', compiled)(exports)
const { filterPublicJobs, sortPublicJobs, formatPublicJobSalary, publicJobApplyUrl, publicJobFiltersFromSearch, emptyPublicJobFilters } = exports
const job = (patch = {}) => ({ publicSlug: 'architect-north', publicUrl: 'https://jobs.example.invalid/careers/architect-north', publicTitle: 'Technical Architect', positionTitle: 'Architect', positionCode: 'JOB-002', clientName: 'North Research', department: 'Technology', jobLocation: 'Bengaluru', employmentType: 'Contract', workMode: 'Hybrid', experienceRange: '10–15 years', summary: 'Lead secure platforms', rolePurpose: 'Architecture and delivery', salaryMin: 90000, salaryMax: 120000, currency: 'INR', remainingPositions: 2, numberOfPositions: 3, publishedAtUtc: '2026-11-01T09:30:00', opensAtUtc: null, closesAtUtc: null, skills: ['Cloud', 'Security'], qualifications: ['Engineering degree'], responsibilities: [], certifications: [], languages: [], benefits: [], ...patch })

test('keyword search combines public title, skills and client without modifying advertised amounts', () => {
  const jobs = [job(), job({ publicSlug: 'researcher', publicTitle: 'Research Associate', clientName: 'South Research', skills: ['Biology'] })]
  const before = structuredClone(jobs)
  assert.deepEqual(filterPublicJobs(jobs, { ...emptyPublicJobFilters, q: 'north cloud' }), [jobs[0]])
  assert.deepEqual(filterPublicJobs(jobs, { ...emptyPublicJobFilters, q: 'ENGINEERING' }), jobs)
  assert.deepEqual(filterPublicJobs(jobs, { ...emptyPublicJobFilters, q: 'unknown-role' }), [])
  assert.deepEqual(jobs, before)
})

test('all chosen public filters apply together while undisclosed salary stays undisclosed', () => {
  const jobs = [job(), job({ publicSlug: 'second', clientName: 'South Research', jobLocation: 'Delhi', salaryMin: 0, salaryMax: 0 })]
  assert.equal(filterPublicJobs(jobs, { ...emptyPublicJobFilters, client: 'North Research', location: 'Bengaluru', type: 'Contract', mode: 'Hybrid', experience: '10–15 years', salary: true }).length, 1)
  assert.equal(filterPublicJobs(jobs, { ...emptyPublicJobFilters, client: 'South Research', salary: true }).length, 0)
  assert.equal(filterPublicJobs(jobs, { ...emptyPublicJobFilters, client: 'South Research' }).length, 1)
})

test('salary display preserves raw advertised units and currency without inventing a period or LPA', () => {
  assert.equal(formatPublicJobSalary(job()), 'INR 90,000 – 1,20,000')
  assert.equal(formatPublicJobSalary(job({ salaryMin: 125000, salaryMax: 125000, currency: 'USD' })), 'USD 1,25,000')
  assert.equal(formatPublicJobSalary(job({ salaryMin: 0, salaryMax: 0 })), 'Not disclosed')
  assert.equal(formatPublicJobSalary(job({ salaryMin: 0, salaryMax: 50000 })), 'Up to INR 50,000')
  assert.equal(formatPublicJobSalary(job({ salaryMin: 50000, salaryMax: 0 })), 'From INR 50,000')
})

test('Apply keeps the exact published destination and rejects a different job or unsafe URL', () => {
  const expected = 'https://portal.example.invalid/hrms/careers/architect-north'
  assert.equal(publicJobApplyUrl(job({ publicUrl: expected })), expected)
  assert.equal(publicJobApplyUrl(job({ publicUrl: 'https://portal.example.invalid/careers/another-job' })), '')
  assert.equal(publicJobApplyUrl(job({ publicUrl: 'javascript:alert(1)' })), '')
  assert.equal(publicJobApplyUrl(job({ publicUrl: 'https://secret@portal.example.invalid/careers/architect-north' })), '')
  assert.equal(publicJobApplyUrl(job({ publicUrl: '' })), '')
})

test('newest sorting treats unzoned API timestamps as UTC and leaves source order intact', () => {
  const jobs = [job({ publicSlug: 'older', publishedAtUtc: '2026-11-01T09:30:00' }), job({ publicSlug: 'newer', publicTitle: 'Analyst', publishedAtUtc: '2026-11-02T09:30:00Z' }), job({ publicSlug: 'undated', publishedAtUtc: 'bad-date' })]
  assert.deepEqual(sortPublicJobs(jobs, 'recent').map(item => item.publicSlug), ['newer', 'older', 'undated'])
  assert.equal(sortPublicJobs(jobs, 'title')[0].publicSlug, 'newer')
  assert.deepEqual(jobs.map(item => item.publicSlug), ['older', 'newer', 'undated'])
})

test('shared search URLs restore filters without an authenticated client context', () => {
  assert.deepEqual(publicJobFiltersFromSearch(new URLSearchParams('q=cloud&location=Delhi&client=North+Research&type=Contract&mode=Hybrid&experience=10+years&salary=disclosed')), { q: 'cloud', location: 'Delhi', client: 'North Research', type: 'Contract', mode: 'Hybrid', experience: '10 years', salary: true })
  assert.deepEqual(publicJobFiltersFromSearch(new URLSearchParams()), emptyPublicJobFilters)
})
