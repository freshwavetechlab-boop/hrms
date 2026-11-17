export const publicRecruitmentEmployer = 'GA Digital Web Word Pvt. Ltd.'

type ClientIdentity = { clientName: string; positionCode: string; publicSlug: string }
const displayFields = new Set([
  'publicTitle', 'positionTitle', 'department', 'jobLocation', 'employmentType', 'workMode', 'experienceRange',
  'summary', 'rolePurpose', 'responsibilityText', 'skillName', 'qualificationName', 'specialization',
  'certificationName', 'languageName', 'benefitName', 'description', 'formName', 'sectionLabel',
  'label', 'helpText', 'placeholder', 'optionLabel',
])
const displayLists = new Set(['responsibilities', 'skills', 'qualifications', 'certifications', 'languages', 'benefits'])
const escapeRegex = (value: string) => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')

function clientAliases(job: ClientIdentity) {
  const name = job.clientName.trim()
  const code = /^POS-(.+)-\d+$/i.exec(job.positionCode)?.[1] || ''
  return [...new Set([name, name.replace(/\s*\([^()]+\)\s*/g, ' ').trim(), ...[...name.matchAll(/\(([^()]+)\)/g)].map(match => match[1]), code])]
    .filter(value => value.length > 1).sort((left, right) => right.length - left.length)
}

function clientPattern(job: ClientIdentity) {
  const aliases = clientAliases(job)
  return aliases.length ? aliases.map(value => escapeRegex(value).replace(/\s+/g, '\\s+')).join('|') : null
}

// Presentation only: source JDs, internal references, form/option IDs, routes,
// submitted values and public application URLs must remain unchanged.
export function publicRecruitmentJobView<T extends ClientIdentity>(job: T): T {
  const pattern = clientPattern(job)
  const client = pattern ? new RegExp(`(?<![\\p{L}\\p{N}])(?:${pattern})(?:[-_][\\p{L}][\\p{L}\\p{N}]*)?(?![\\p{L}\\p{N}])`, 'giu') : null
  const copy = (value: string) => client ? value.replace(client, 'GA Digital') : value
  const location = (value: string) => {
    if (!pattern) return value
    // Site masters often contain "CLIENT-INSTITUTE - City". Keep the posting
    // geography rather than relabelling a client's institute as the employer.
    const prefix = new RegExp(`^\\s*(?:${pattern})(?:[-_][\\p{L}\\p{N}]+)?\\s+[-–—]\\s*`, 'iu')
    const simplePrefix = new RegExp(`^\\s*(?:${pattern})[\\s_–—-]*`, 'iu')
    const city = value.replace(prefix, '').replace(simplePrefix, '').replace(/^[A-Z][A-Z\d-]{2,}\s*[-–—]\s*/u, '')
    return copy(city).trim() || 'Location shared during recruitment'
  }
  const project = (value: unknown, key = ''): unknown => {
    if (typeof value === 'string') {
      if (key === 'clientName') return publicRecruitmentEmployer
      if (key === 'positionCode') return `JOB-${job.publicSlug.toUpperCase()}`
      if (key === 'jobLocation') return location(value)
      return displayFields.has(key) || displayLists.has(key) ? copy(value) : value
    }
    if (Array.isArray(value)) return value.map(item => project(item, key))
    if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([field, item]) => [field, project(item, field)]))
    return value
  }
  return project(job) as T
}
