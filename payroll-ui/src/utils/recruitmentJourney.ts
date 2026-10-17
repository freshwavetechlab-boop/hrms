export type JourneyVisit = {
  id: number
  pipelineStageId: number
  stageCode?: string
  stageName: string
  displayOrder?: number
  enteredAtUtc?: string | null
  status: string
}

// Visit IDs are never collapsed by stage: returning to the same stage is a new visit.
export function recruitmentJourney<T extends JourneyVisit>(source: T[]) {
  const seen = new Map<string, number>()
  const rows = source.filter(row => row.enteredAtUtc && row.status !== 'Pending')
    .sort((a, b) => Date.parse(a.enteredAtUtc!) - Date.parse(b.enteredAtUtc!) || a.id - b.id)
  return rows.map((row, index) => {
    const key = row.stageCode || String(row.pipelineStageId)
    const visit = (seen.get(key) ?? 0) + 1
    seen.set(key, visit)
    const previous = rows[index - 1]
    const direction = !previous ? 'start' : row.displayOrder != null && previous.displayOrder != null && row.displayOrder < previous.displayOrder
      ? 'back' : (previous.stageCode || String(previous.pipelineStageId)) === key ? 'repeat' : 'forward'
    return { row, visit, direction, sequence: index + 1 } as const
  })
}
