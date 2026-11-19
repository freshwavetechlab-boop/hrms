import type { RecruitmentUnifiedPipelineLane } from '../types/recruitmentOrchestration'

// The hiring journey includes the hand-off to candidate processing. Scope only
// the operational cards, never truncate a map labelled "Complete hiring journey".
export function recruitmentJourneyLanes(lanes: RecruitmentUnifiedPipelineLane[], view: 'hiring' | 'candidates') {
  return lanes.filter(lane => view === 'hiring' || lane.cardScope === 'Application')
    .sort((left, right) => left.pipelineVersionId - right.pipelineVersionId || left.displayOrder - right.displayOrder)
}
