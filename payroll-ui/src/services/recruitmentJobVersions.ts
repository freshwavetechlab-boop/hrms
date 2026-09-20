import type { RecruitmentJobPosting } from '../types/recruitmentOrchestration'
import type { RecruitmentHiringCase } from '../types/recruitmentCases'

// A title is not an identity: two separate positions may legitimately share it.
// Keep historical postings accessible by ID, without repeating active job cards.
export function currentRecruitmentJobPostings(rows: RecruitmentJobPosting[]): RecruitmentJobPosting[] {
  const current = new Map<string, RecruitmentJobPosting>()
  const rank = (row: RecruitmentJobPosting) => row.status === 'Published' ? 3 : row.status === 'Draft' ? 2 : 1
  const date = (row: RecruitmentJobPosting) => Date.parse(row.publishedAtUtc || row.createdAtUtc || '') || 0
  for (const row of rows) {
    const key = `${row.clientId}:${row.positionId || `posting-${row.id}`}`
    const previous = current.get(key)
    if (!previous || rank(row) > rank(previous) || (rank(row) === rank(previous)
      && (date(row) > date(previous) || (date(row) === date(previous) && row.id > previous.id)))) current.set(key, row)
  }
  return [...current.values()]
}

export function currentRecruitmentHiringCases(rows: RecruitmentHiringCase[]): RecruitmentHiringCase[] {
  const current = new Map<string, RecruitmentHiringCase>()
  for (const row of rows) {
    if (row.status === 'Superseded' || row.isHistorical) continue
    const key = `${row.clientId}:${row.requisitionId ? `request-${row.requisitionId}` : `line-${row.workOrderLineId}`}`
    const previous = current.get(key)
    if (!previous || Number(Boolean(row.positionId)) > Number(Boolean(previous.positionId))
      || (Boolean(row.positionId) === Boolean(previous.positionId) && row.id > previous.id)) current.set(key, row)
  }
  return [...current.values()]
}
