import type { ExcelPayslipRow } from '../services/excelPayslipService'

export type SourceCodeScope = { clientId: number; batchId: string; month: string }
export type SourceCodeCandidate = Pick<ExcelPayslipRow, 'id' | 'employeeCode' | 'sourceRow'>
export type SourceCodeResolution = { rowIds: string[]; codes: string[]; errors: string[]; fingerprint: string; inputText: string }
const normalized = (value: string) => value.trim().toLowerCase()

export function resolvePayslipSourceCodes(inputText: string, scope: SourceCodeScope, sourceRows: SourceCodeCandidate[], visibleRowIds: string[]): SourceCodeResolution {
  const errors: string[] = [], rowIds: string[] = [], codes: string[] = []
  if (!Number.isSafeInteger(scope.clientId) || scope.clientId <= 0 || !scope.batchId.trim() || !/^\d{4}-(0[1-9]|1[0-2])$/.test(scope.month)) errors.push('Open a saved batch for the current client and salary month first.')
  const roster = sourceRows.map(row => ({ id: row.id, code: typeof row.employeeCode === 'string' ? row.employeeCode.trim() : '', sourceRow: row.sourceRow }))
    .sort((a, b) => a.id.localeCompare(b.id) || a.code.localeCompare(b.code) || a.sourceRow - b.sourceRow)
  const fingerprint = JSON.stringify({ scope, inputText, roster, visibleRowIds: [...visibleRowIds].sort() })
  if (inputText.length > 10000) return { rowIds: [], codes: [], errors: ['Enter at most 100 source employee codes.'], fingerprint, inputText }
  const requested = inputText.split(/[,\r\n]+/).map(value => value.trim()).filter(Boolean)
  if (!requested.length || requested.length > 100 || requested.some(value => value.length > 100)) errors.push('Enter between 1 and 100 source employee codes, separated by commas or new lines.')
  const unique = new Set<string>(), visible = new Set(visibleRowIds), byCode = new Map<string, typeof roster>(), byId = new Map<string, number>()
  for (const row of roster) {
    byCode.set(normalized(row.code), [...(byCode.get(normalized(row.code)) || []), row])
    byId.set(row.id, (byId.get(row.id) || 0) + 1)
  }
  if (!errors.length) for (const code of requested) {
    const key = normalized(code)
    if (unique.has(key)) { errors.push(`Repeated input code: ${code}.`); continue }
    unique.add(key)
    const matches = byCode.get(key) || []
    if (matches.length !== 1) {
      errors.push(matches.length ? `Code ${code} has multiple source parts in this batch. Select the intended source rows manually; these parts are not merged.` : `Code ${code} is not in this saved client batch.`)
      continue
    }
    const match = matches[0]
    if (!match.id.trim() || match.id.trim() !== match.id || byId.get(match.id) !== 1 || !Number.isSafeInteger(match.sourceRow) || match.sourceRow <= 0) { errors.push(`Code ${code} has an invalid or repeated source-row identity.`); continue }
    if (!visible.has(match.id)) { errors.push(`Code ${code} is hidden by the current email-status filter. Clear that filter before selecting it.`); continue }
    rowIds.push(match.id); codes.push(match.code)
  }
  return { rowIds: errors.length ? [] : rowIds, codes: errors.length ? [] : codes, errors, fingerprint, inputText }
}

export function sourceCodeResolutionIsCurrent(reviewed: SourceCodeResolution | null, inputText: string, scope: SourceCodeScope, rows: SourceCodeCandidate[], visibleRowIds: string[]) {
  if (!reviewed || reviewed.errors.length || !reviewed.rowIds.length) return false
  const fresh = resolvePayslipSourceCodes(inputText, scope, rows, visibleRowIds)
  return !fresh.errors.length && reviewed.fingerprint === fresh.fingerprint
}
