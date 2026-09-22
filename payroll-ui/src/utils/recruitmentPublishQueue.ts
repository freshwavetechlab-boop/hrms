import type { RecruitmentOpenPosition } from '../types/payroll'
import type { RecruitmentJobPosting } from '../types/recruitmentOrchestration'

export function positionsReadyToPublish(positions: RecruitmentOpenPosition[], postings: RecruitmentJobPosting[]) {
  const published = new Set(postings.filter(row => row.status.trim().toLowerCase() === 'published')
    .map(row => `${row.clientId}:${row.positionId}`))
  // Position publication also covers roles published through the position workflow.
  // Later hiring stages, closed roles and paused roles are not new publishing tasks.
  return positions.filter(row => ['open', 'recruiter assigned'].includes(row.status.trim().toLowerCase())
    && row.jobDescriptionStatus?.trim().toLowerCase() === 'approved'
    && row.remainingPositions > 0
    && !published.has(`${row.clientId}:${row.id}`))
}
