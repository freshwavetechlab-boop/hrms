export type PublicCareerJob = {
  publicSlug: string; publicUrl: string; publicTitle: string; positionCode: string; positionTitle: string
  clientName: string; department: string; jobLocation: string; employmentType: string; workMode: string
  experienceRange: string; salaryMin: number; salaryMax: number; currency: string
  numberOfPositions: number; remainingPositions: number
  publishedAtUtc?: string | null; opensAtUtc?: string | null; closesAtUtc?: string | null
  summary: string; rolePurpose: string; responsibilities: string[]; skills: string[]
  qualifications: string[]; certifications: string[]; languages: string[]; benefits: string[]
}

export type PublicJobFilters = { q: string; location: string; type: string; mode: string; experience: string; salary: boolean }
export const emptyPublicJobFilters: PublicJobFilters = { q: '', location: '', type: '', mode: '', experience: '', salary: false }
const normalized = (value: string) => value.normalize('NFKC').toLocaleLowerCase().trim()
export const publicJobTitle = (job: PublicCareerJob) => job.publicTitle || job.positionTitle

type AdvertisedTerms = { experienceRange?: string; summary?: string; rolePurpose?: string }

export function formatRecruitmentExperience(job: AdvertisedTerms) {
  if (job.experienceRange?.trim()) return job.experienceRange.trim()
  const source = [job.summary, job.rolePurpose].filter(Boolean).join('\n')
  const essential = /Essential (?:qualifications?|eligibility)(?:\s*\([^)]*\))?\s*:\s*([\s\S]*?)(?=\bDesirable\s*:|\n\s*(?:Travel|This single|Source)|$)/i.exec(source)?.[1]?.trim()
  if (!essential) return 'Not specified'
  const numbers: Record<string, number> = { one: 1, two: 2, three: 3, four: 4, five: 5, six: 6, seven: 7, eight: 8, nine: 9, ten: 10 }
  const requirements = new Set<string>()
  for (const match of essential.matchAll(/\b(\d+(?:\.\d+)?|one|two|three|four|five|six|seven|eight|nine|ten)\s+(years?|months?)\b/gi)) {
    const before = essential.slice(Math.max(0, match.index - 45), match.index)
    const after = essential.slice(match.index + match[0].length, match.index + match[0].length + 110)
    // Degree/course lengths and experience equivalence are not work-experience
    // minimums. Only durations attached to an experience clause are displayed.
    if (/treated\s+as\s*$/i.test(before)) continue
    const experienceAt = after.search(/\bexperience\b/i)
    const educationAt = after.search(/\b(?:graduate|degree|diploma|course)\b/i)
    const clauseEnd = after.search(/[.;]|\bor\b/i)
    const workExperience = experienceAt >= 0 && (educationAt < 0 || experienceAt < educationAt) && (clauseEnd < 0 || experienceAt < clauseEnd)
    const staffLevel = /^\s+for\s+(?:juniors?|seniors?)\b/i.test(after) && /\bexperience\b/i.test(essential)
    if (!workExperience && !staffLevel) continue
    const amount = numbers[match[1].toLowerCase()] ?? Number(match[1])
    const unit = match[2].toLowerCase().startsWith('year') ? 'year' : 'month'
    requirements.add(`${amount} ${unit}${amount === 1 ? '' : 's'}`)
  }
  if (!requirements.size) return /\bexperience\b/i.test(essential) ? 'See JD experience requirements' : 'No minimum experience stated'
  const alternatives = /\bor\b|\b(?:juniors?|seniors?)\b/i.test(essential)
  return `${[...requirements].join(' / ')}${alternatives ? ' (by eligibility route)' : ''}`
}

export function savedMonthlyRemuneration(job: Pick<AdvertisedTerms, 'summary' | 'rolePurpose'>) {
  const source = [job.summary, job.rolePurpose].filter(Boolean).join('\n')
  const match = /Monthly (?:remuneration|salary|pay)\s*:\s*([^\n]+?)(?=\.\s+(?:Duration|Upper age limit|Essential|Desirable)\s*:|\n|$)/i.exec(source)
  const value = match?.[1]?.trim().replace(/\.$/, '') || ''
  return /(?:Rs\.?|INR|₹)\s*[\d,]+/i.test(value) ? value : ''
}

export function filterPublicJobs(jobs: PublicCareerJob[], filters: PublicJobFilters) {
  const words = normalized(filters.q).split(/\s+/).filter(Boolean)
  return jobs.filter(job => {
    if (filters.location && job.jobLocation !== filters.location
      || filters.type && job.employmentType !== filters.type || filters.mode && job.workMode !== filters.mode
      || filters.experience && job.experienceRange !== filters.experience) return false
    if (filters.salary && formatPublicJobSalary(job) === 'Not disclosed') return false
    const text = normalized([publicJobTitle(job), job.positionCode, job.department, job.jobLocation, job.summary, job.rolePurpose, ...(job.skills || []), ...(job.qualifications || [])].join(' '))
    return words.every(word => text.includes(word))
  })
}

function timestamp(value?: string | null) {
  const text = value?.trim() || ''
  const parsed = text ? Date.parse(/(?:z|[+-]\d{2}:?\d{2})$/i.test(text) ? text : `${text}Z`) : 0
  return Number.isFinite(parsed) ? parsed : 0
}
export function sortPublicJobs(jobs: PublicCareerJob[], sort: string) {
  return [...jobs].sort((a, b) => sort === 'title'
    ? publicJobTitle(a).localeCompare(publicJobTitle(b)) || a.publicSlug.localeCompare(b.publicSlug)
    : timestamp(b.publishedAtUtc) - timestamp(a.publishedAtUtc) || a.publicSlug.localeCompare(b.publicSlug))
}

export function formatPublicJobSalary(job: Pick<PublicCareerJob, 'salaryMin' | 'salaryMax' | 'currency'> & Partial<AdvertisedTerms>) {
  const number = (value: number) => new Intl.NumberFormat('en-IN', { maximumFractionDigits: 2 }).format(value)
  const currency = job.currency?.trim() || 'INR'
  const min = Number(job.salaryMin), max = Number(job.salaryMax)
  if (min > 0 && max > 0) return `${currency} ${min === max ? number(min) : `${number(min)} – ${number(max)}`} / year`
  if (min > 0) return `From ${currency} ${number(min)} / year`
  if (max > 0) return `Up to ${currency} ${number(max)} / year`
  const monthly = savedMonthlyRemuneration(job)
  const amount = /(?:Rs\.?|INR|₹)\s*([\d,]+(?:\.\d+)?)/i.exec(monthly)?.[1]
  if (amount) {
    const base = Number(amount.replace(/,/g, ''))
    // A parenthesized breakdown is already included in the leading monthly
    // total. Only an additional HRA outside that breakdown is added to base pay.
    const mainAmount = monthly.split('(')[0]
    const hra = /\+\s*(\d+(?:\.\d+)?)\s*%\s*HRA\b/i.exec(mainAmount)?.[1]
    const annual = base * 12 * (hra ? 1 + Number(hra) / 100 : 1)
    if (Number.isFinite(annual) && annual > 0) {
      const additionalHra = !hra && /\+\s*HRA\b/i.test(mainAmount)
      return `INR ${number(annual)}${additionalHra ? ' + HRA' : ''} / year${hra ? ` (includes ${hra}% HRA)` : ''}`
    }
  }
  return 'Not disclosed'
}

export function publicJobFiltersFromSearch(params: URLSearchParams): PublicJobFilters {
  return { q: params.get('q') || '', location: params.get('location') || '', type: params.get('type') || '', mode: params.get('mode') || '', experience: params.get('experience') || '', salary: params.get('salary') === 'disclosed' }
}

// Only the existing published application destination is used. Reject unsafe
// schemes and a URL that belongs to a different job before rendering an anchor.
export function publicJobApplyUrl(job: Pick<PublicCareerJob, 'publicUrl' | 'publicSlug'>) {
  try {
    const url = new URL(job.publicUrl)
    if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password) return ''
    const parts = url.pathname.split('/').filter(Boolean)
    if (parts.at(-2)?.toLowerCase() !== 'careers' || decodeURIComponent(parts.at(-1) || '') !== job.publicSlug) return ''
    return url.toString()
  } catch { return '' }
}
