import type { EmployeeImportCodeSuggestion, EmployeeImportReviewRow } from '../services/settingsService'
import type { ImportPreviewData } from './importPreview'
import { buildEmployeeCodes, inferEmployeeCodeDigits, type BulkImportOperation } from './smartBulkImport'

export const employeeCodeColumn = (headers: string[]) => headers.findIndex(header => header.replace(/[\s_-]/g, '').toLowerCase() === 'employeecode')

export type EmployeeCodeHintSnapshot = {
  clientId: number
  sheetName: string
  headers: string[]
  entries: { source: string[]; review: EmployeeImportReviewRow }[]
}

export function captureEmployeeCodeHints(clientId: number, sheet: ImportPreviewData & { name: string }, review: EmployeeImportReviewRow[]): EmployeeCodeHintSnapshot {
  const column = employeeCodeColumn(sheet.headers)
  return { clientId, sheetName: sheet.name, headers: sheet.headers, entries: review.flatMap(row => {
    const source = sheet.rows[row.rowNumber - 2]
    return source && column >= 0 && !source[column]?.trim() ? [{ source, review: row }] : []
  }) }
}

export function employeeCodeHintsForRow(snapshot: EmployeeCodeHintSnapshot | null, clientId: number, sheet: ImportPreviewData & { name: string }, rowIndex: number): EmployeeImportCodeSuggestion[] {
  if (!snapshot || snapshot.clientId !== clientId || snapshot.sheetName !== sheet.name || snapshot.headers !== sheet.headers) return []
  const source = sheet.rows[rowIndex], column = employeeCodeColumn(sheet.headers)
  if (!source || column < 0 || source[column]?.trim()) return []
  const review = snapshot.entries.find(entry => entry.source === source)?.review
  const seen = new Set<number>()
  return (review?.codeSuggestions ?? []).filter(item => {
    if (!(item.employeeId > 0) || !item.employeeCode?.trim() || seen.has(item.employeeId)) return false
    seen.add(item.employeeId)
    return true
  }).slice(0, 3)
}

export function employeeCodeSuggestionUsed(code: string, rows: string[][], column: number, rowIndex: number, otherCodes: string[]) {
  const normalized = code.trim().toUpperCase()
  return !!normalized && (rows.some((row, index) => index !== rowIndex && row[column]?.trim().toUpperCase() === normalized)
    || otherCodes.some(value => value.trim().toUpperCase() === normalized))
}

export function employeeDraftPrefix(codes: string[], fallback: string) {
  const counts = new Map<string, number>()
  for (const code of codes) {
    const prefix = code.trim().toUpperCase().match(/^(.*?\D)\d+$/)?.[1]
    if (prefix) counts.set(prefix, (counts.get(prefix) ?? 0) + 1)
  }
  const options = [...counts.entries()].sort((left, right) => right[1] - left[1])
  return options.length && options[0][1] !== options[1]?.[1] ? options[0][0] : fallback.toUpperCase()
}

export function resolveDraftEmployeeCodes(sheet: ImportPreviewData, review: EmployeeImportReviewRow[], mode: BulkImportOperation, prefix: string, reserved: string[]) {
  const column = employeeCodeColumn(sheet.headers)
  const changes = new Map<number, string>()
  const newRows: number[] = []
  const unresolved: number[] = []
  const byRow = new Map(review.map(row => [row.rowNumber, row]))
  const taken = new Set(sheet.rows.map(row => row[column]?.trim().toUpperCase()).filter(Boolean))
  const matchedCounts = new Map<string, number>()
  for (const row of review) {
    const code = row.matchedEmployeeCode?.trim().toUpperCase()
    if (code) matchedCounts.set(code, (matchedCounts.get(code) ?? 0) + 1)
  }
  sheet.rows.forEach((source, index) => {
    if (column < 0 || source[column]?.trim()) return
    const number = index + 2
    const row = byRow.get(number)
    const code = row?.matchedEmployeeCode?.trim()
    if (row?.canGenerateEmployeeCode === true && !row.matchedEmployeeId && !row.candidateEmployees?.length) {
      newRows.push(index)
    } else if (mode !== 'insert' && code && row?.matchedEmployeeId && ['MatchedByEmployeeId', 'MatchedByIdentifiers'].includes(row.matchStatus)
        && !row.blockingReasons.length && !taken.has(code.toUpperCase()) && matchedCounts.get(code.toUpperCase()) === 1) {
      changes.set(index, code)
    } else {
      unresolved.push(number)
    }
  })
  const recovered = changes.size
  if (mode !== 'update' && prefix.trim() && newRows.length) {
    const existingCodes = [...reserved, ...changes.values(), ...taken]
    const codes = buildEmployeeCodes(newRows.map(() => []), undefined, { enabled: true, prefix, digits: inferEmployeeCodeDigits(prefix, existingCodes), existingCodes })
    newRows.forEach((index, position) => changes.set(index, codes[position]))
  }
  const rows = changes.size ? sheet.rows.map((row, index) => {
    const code = changes.get(index)
    if (!code) return row
    const edited = [...row]
    edited[column] = code
    return edited
  }) : sheet.rows
  return { rows, recovered, generated: changes.size - recovered, newCount: newRows.length, unresolved }
}
