export type PublicCareerJob = {
  publicSlug: string; publicUrl: string; publicTitle: string; positionCode: string; positionTitle: string
  clientName: string; department: string; jobLocation: string; employmentType: string; workMode: string
  experienceRange: string; salaryMin: number; salaryMax: number; currency: string
  numberOfPositions: number; remainingPositions: number
  publishedAtUtc?: string | null; opensAtUtc?: string | null; closesAtUtc?: string | null
  summary: string; rolePurpose: string; responsibilities: string[]; skills: string[]
  qualifications: string[]; certifications: string[]; languages: string[]; benefits: string[]
}

export type PublicJobFilters = { q: string; location: string; client: string; type: string; mode: string; experience: string; salary: boolean }
export const emptyPublicJobFilters: PublicJobFilters = { q: '', location: '', client: '', type: '', mode: '', experience: '', salary: false }
const normalized = (value: string) => value.normalize('NFKC').toLocaleLowerCase().trim()
export const publicJobTitle = (job: PublicCareerJob) => job.publicTitle || job.positionTitle

export function filterPublicJobs(jobs: PublicCareerJob[], filters: PublicJobFilters) {
  const words = normalized(filters.q).split(/\s+/).filter(Boolean)
  return jobs.filter(job => {
    if (filters.location && job.jobLocation !== filters.location || filters.client && job.clientName !== filters.client
      || filters.type && job.employmentType !== filters.type || filters.mode && job.workMode !== filters.mode
      || filters.experience && job.experienceRange !== filters.experience) return false
    if (filters.salary && !(job.salaryMin > 0 || job.salaryMax > 0)) return false
    const text = normalized([publicJobTitle(job), job.positionCode, job.clientName, job.department, job.jobLocation, job.summary, job.rolePurpose, ...(job.skills || []), ...(job.qualifications || [])].join(' '))
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

export function formatPublicJobSalary(job: Pick<PublicCareerJob, 'salaryMin' | 'salaryMax' | 'currency'>) {
  const number = (value: number) => new Intl.NumberFormat('en-IN', { maximumFractionDigits: 2 }).format(value)
  const currency = job.currency?.trim() || 'INR'
  const min = Number(job.salaryMin), max = Number(job.salaryMax)
  if (min > 0 && max > 0) return min === max ? `${currency} ${number(min)}` : `${currency} ${number(min)} – ${number(max)}`
  if (min > 0) return `From ${currency} ${number(min)}`
  if (max > 0) return `Up to ${currency} ${number(max)}`
  return 'Not disclosed'
}

export function publicJobFiltersFromSearch(params: URLSearchParams): PublicJobFilters {
  return { q: params.get('q') || '', location: params.get('location') || '', client: params.get('client') || '', type: params.get('type') || '', mode: params.get('mode') || '', experience: params.get('experience') || '', salary: params.get('salary') === 'disclosed' }
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
