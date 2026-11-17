import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const compiled = ts.transpileModule(readFileSync(new URL('../src/utils/publicJobsBoard.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
const exports = {}
new Function('exports', compiled)(exports)
const { filterPublicJobs, sortPublicJobs, formatPublicJobSalary, formatRecruitmentExperience, savedMonthlyRemuneration, publicJobApplyUrl, publicJobFiltersFromSearch, emptyPublicJobFilters } = exports
const job = (patch = {}) => ({ publicSlug: 'architect-north', publicUrl: 'https://jobs.example.invalid/careers/architect-north', publicTitle: 'Technical Architect', positionTitle: 'Architect', positionCode: 'JOB-002', clientName: 'North Research', department: 'Technology', jobLocation: 'Bengaluru', employmentType: 'Contract', workMode: 'Hybrid', experienceRange: '10–15 years', summary: 'Lead secure platforms', rolePurpose: 'Architecture and delivery', salaryMin: 90000, salaryMax: 120000, currency: 'INR', remainingPositions: 2, numberOfPositions: 3, publishedAtUtc: '2026-11-01T09:30:00', opensAtUtc: null, closesAtUtc: null, skills: ['Cloud', 'Security'], qualifications: ['Engineering degree'], responsibilities: [], certifications: [], languages: [], benefits: [], ...patch })

test('keyword search combines advertised title and skills without using client identity or modifying amounts', () => {
  const jobs = [job(), job({ publicSlug: 'researcher', publicTitle: 'Research Associate', clientName: 'South Research', skills: ['Biology'] })]
  const before = structuredClone(jobs)
  assert.deepEqual(filterPublicJobs(jobs, { ...emptyPublicJobFilters, q: 'architect cloud' }), [jobs[0]])
  assert.deepEqual(filterPublicJobs(jobs, { ...emptyPublicJobFilters, q: 'north' }), [])
  assert.deepEqual(filterPublicJobs(jobs, { ...emptyPublicJobFilters, q: 'ENGINEERING' }), jobs)
  assert.deepEqual(filterPublicJobs(jobs, { ...emptyPublicJobFilters, q: 'unknown-role' }), [])
  assert.deepEqual(jobs, before)
})

test('all chosen public filters apply together while undisclosed salary stays undisclosed', () => {
  const jobs = [job(), job({ publicSlug: 'second', clientName: 'South Research', jobLocation: 'Delhi', salaryMin: 0, salaryMax: 0 })]
  assert.equal(filterPublicJobs(jobs, { ...emptyPublicJobFilters, location: 'Bengaluru', type: 'Contract', mode: 'Hybrid', experience: '10–15 years', salary: true }).length, 1)
  assert.equal(filterPublicJobs(jobs, { ...emptyPublicJobFilters, location: 'Delhi', salary: true }).length, 0)
  assert.equal(filterPublicJobs(jobs, { ...emptyPublicJobFilters, location: 'Delhi' }).length, 1)
})

test('structured salary values follow the owner-selected annual display standard without changing currency', () => {
  assert.equal(formatPublicJobSalary(job()), 'INR 90,000 – 1,20,000 / year')
  assert.equal(formatPublicJobSalary(job({ salaryMin: 125000, salaryMax: 125000, currency: 'USD' })), 'USD 1,25,000 / year')
  assert.equal(formatPublicJobSalary(job({ salaryMin: 0, salaryMax: 0 })), 'Not disclosed')
  assert.equal(formatPublicJobSalary(job({ salaryMin: 0, salaryMax: 50000 })), 'Up to INR 50,000 / year')
  assert.equal(formatPublicJobSalary(job({ salaryMin: 50000, salaryMax: 0 })), 'From INR 50,000 / year')
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
  assert.deepEqual(publicJobFiltersFromSearch(new URLSearchParams('q=cloud&location=Delhi&client=North+Research&type=Contract&mode=Hybrid&experience=10+years&salary=disclosed')), { q: 'cloud', location: 'Delhi', type: 'Contract', mode: 'Hybrid', experience: '10 years', salary: true })
  assert.deepEqual(publicJobFiltersFromSearch(new URLSearchParams()), emptyPublicJobFilters)
})

test('saved JD experience distinguishes degree lengths, actual work experience and alternative eligibility routes', () => {
  const source = essential => ({ summary: `Project duration: 4 years. Essential qualifications: ${essential} Desirable: Five years experience.` })
  assert.equal(formatRecruitmentExperience(source('Three-year graduate degree plus three years of post-qualification experience OR Post-graduate degree.')), '3 years (by eligibility route)')
  assert.equal(formatRecruitmentExperience(source('12th in Science + Diploma + five years experience OR Graduate + two years experience.')), '5 years / 2 years (by eligibility route)')
  assert.equal(formatRecruitmentExperience(source('MBBS with 6 years experience OR MBBS + PG Diploma with 5 years experience OR MD with 4 years research experience.')), '6 years / 5 years / 4 years (by eligibility route)')
  assert.equal(formatRecruitmentExperience(source('Three-year General Nursing & Midwife (GNM) Course.')), 'No minimum experience stated')
  assert.equal(formatRecruitmentExperience(source('Masters in Health Economics. 2 years of experience for Junior Staff and 6 years for Seniors')), '2 years / 6 years (by eligibility route)')
  assert.equal(formatRecruitmentExperience({ ...source('Graduate + 2 years experience'), experienceRange: '3–5 years' }), '3–5 years')
  assert.equal(formatRecruitmentExperience({ summary: 'A 4-year research project with a three-year degree.' }), 'Not specified')
})

test('monthly remuneration reads the explicitly labelled saved source without interpreting duration as salary', () => {
  assert.equal(savedMonthlyRemuneration({ summary: 'Monthly remuneration: Rs. 1,04,000 per month (Rs. 80,000 + 30% HRA). Duration: One year. Upper age limit: 40 years.' }), 'Rs. 1,04,000 per month (Rs. 80,000 + 30% HRA)')
  assert.equal(savedMonthlyRemuneration({ summary: 'Monthly remuneration: Rs. 28,000 + 10% HRA. Duration: One year.' }), 'Rs. 28,000 + 10% HRA')
  assert.equal(savedMonthlyRemuneration({ summary: 'Project budget: Rs. 1,00,000. Duration: 2 years.' }), '')
})

test('monthly JD pay annualizes once, includes explicit extra HRA and leaves unspecified HRA separate', () => {
  const salary = monthly => job({ salaryMin: 0, salaryMax: 0, summary: `Monthly remuneration: ${monthly}. Duration: One year.` })
  assert.equal(formatPublicJobSalary(salary('Rs. 1,04,000 per month (Rs. 80,000 + 30% HRA)')), 'INR 12,48,000 / year')
  assert.equal(formatPublicJobSalary(salary('Rs. 28,000 + 10% HRA')), 'INR 3,69,600 / year (includes 10% HRA)')
  assert.equal(formatPublicJobSalary(salary('Rs. 20,000 + HRA')), 'INR 2,40,000 + HRA / year')
  assert.equal(formatPublicJobSalary(salary('Rs. 36,400')), 'INR 4,36,800 / year')
  assert.equal(filterPublicJobs([salary('Rs. 36,400')], { ...emptyPublicJobFilters, salary: true }).length, 1)
})
