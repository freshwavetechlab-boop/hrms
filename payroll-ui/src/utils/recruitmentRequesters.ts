export type RecruitmentRequester = {
  userId: number
  displayName: string
  email: string
  clientId: number
}

export type RecruitmentRequesterSnapshot = {
  clientId: number
  rows: RecruitmentRequester[]
  status: 'idle' | 'loading' | 'ready' | 'error'
  error: string
}

export function eligibleRecruitmentRequesters(snapshot: RecruitmentRequesterSnapshot, clientId: number) {
  if (clientId <= 0 || snapshot.clientId !== clientId || snapshot.status !== 'ready') return []
  const seen = new Set<number>()
  return snapshot.rows.filter(row => {
    if (!Number.isInteger(row.userId) || row.userId <= 0 || row.clientId !== clientId || seen.has(row.userId)) return false
    seen.add(row.userId)
    return true
  })
}

export function recruitmentRequesterSelection(snapshot: RecruitmentRequesterSnapshot, clientId: number, currentId?: number | null, ownUserId?: number | null): number | null {
  const rows = eligibleRecruitmentRequesters(snapshot, clientId)
  if (rows.some(row => row.userId === currentId)) return currentId!
  if (rows.some(row => row.userId === ownUserId)) return ownUserId!
  return rows.length === 1 ? rows[0].userId : null
}
