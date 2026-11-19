import type { RecruitmentJoiningCandidate } from '../services/recruitmentJoiningService'

export type JoiningRecord = RecruitmentJoiningCandidate & { id: string }
export function joiningRecordKey(row: RecruitmentJoiningCandidate): string {
  const roster = row.sourceType === 'Roster'
  const id = roster ? row.rosterRowId || row.sourceId : row.offerId
  return Number.isSafeInteger(id) && id > 0 ? `${roster ? 'roster' : 'offer'}:${id}` : ''
}
export function joiningRecords(rows: RecruitmentJoiningCandidate[], clientId: number): JoiningRecord[] {
  return rows.filter(row => !clientId || row.clientId === clientId).map(row => ({ ...row, id: joiningRecordKey(row) })).filter(row => !!row.id)
}
export function releasedJoiningRecords(rows: JoiningRecord[]): JoiningRecord[] {
  return rows.filter(row => !row.acceptanceRecorded && ['Released', 'Pending Candidate'].includes(row.offerStatus))
}
export function joiningAcceptanceSelection(rows: JoiningRecord[], selected: string[], visible: string[], clientId: number) {
  const allowed = new Set(visible), chosen = new Set(selected)
  const eligible = releasedJoiningRecords(rows).filter(row => (!clientId || row.clientId === clientId) && allowed.has(row.id) && chosen.has(row.id))
  return { clientId, offerIds: [...new Set(eligible.filter(row => row.sourceType !== 'Roster').map(row => row.offerId))], rosterRowIds: [...new Set(eligible.filter(row => row.sourceType === 'Roster').map(row => row.rosterRowId || row.sourceId))] }
}
