import type { PayslipColumn, PayslipSheet } from './excelPayslipImport'

export type PayslipMappingEvidence = PayslipColumn & {
  formulaDependencies: number[]; formulaFunctions: string[]
  crossSheetDependencies: PayslipMappingDependency[]
  dependencyEvidenceIncomplete: boolean
  hasFormula: boolean; hasNumbers: boolean; hasText: boolean; hasBlanks: boolean
}
export type PayslipMappingDependency = { sheetIndex: number; sourceType: 'attendance' | 'salary' | 'other' | 'unknown'; status: 'supplied' | 'missing' | 'external-review'; columnIndexes: number[] }
const allowedFunctions = new Set(['SUM', 'ROUND', 'ROUNDUP', 'ROUNDDOWN', 'IF', 'IFERROR', 'MIN', 'MAX', 'SUMIF', 'SUMIFS', 'COUNT', 'COUNTA', 'COUNTIF', 'COUNTIFS', 'AND', 'OR', 'ABS', 'CEILING', 'FLOOR', 'VLOOKUP', 'HLOOKUP', 'XLOOKUP', 'INDEX', 'MATCH'])
const safeHeader = (text: string) => text.replace(/[^\s@]+@[^\s@]+\.[^\s@]+|(?:https?:\/\/|[A-Za-z]:\\|\\\\)\S+/gi, '[redacted]')
  .replace(/\d(?:[\s-]*\d){7,}/g, '[identifier]').replace(/\b[A-Z]{5}\d{4}[A-Z]\b/gi, '[identifier]').replace(/[\p{Cc}\p{Cf}<>]/gu, ' ').slice(0, 160)

// Build metadata locally. Employee names/identifiers, monetary cell values and raw formulas never leave the browser for AI.
export function buildPayslipMappingEvidence(sheet: PayslipSheet, headerRow: number, columns: PayslipColumn[], salaryTemplateId = '', excludedRows: number[] = [], workbookSheets: PayslipSheet[] = [sheet]) {
  const excluded = new Set(excludedRows)
  // Inspect every included source row: a late formula variant must not disappear
  // merely because it occurs after the first screen/sample of employees.
  const rows = sheet.rows.filter(row => row.sourceRow > headerRow && !excluded.has(row.sourceRow))
  const index = (text: string) => [...text.replace(/\$/g, '').toUpperCase()].reduce((total, letter) => total * 26 + letter.charCodeAt(0) - 64, 0) - 1
  const sameSheet = (left: string, right: string) => left.toLocaleLowerCase('en-US') === right.toLocaleLowerCase('en-US')
  return { salaryTemplateId, columns: columns.map(column => {
    const dependencies = new Set<number>(), functions = new Set<string>()
    const crossSheet = new Map<string, PayslipMappingDependency>()
    let hasFormula = false, hasNumbers = false, hasText = false, hasBlanks = false, dependencyEvidenceIncomplete = false
    for (const row of rows) {
      const cell = row.cells[column.columnIndex], value = cell?.value.trim() || ''
      if (!value) hasBlanks = true
      else if (/^[₹\s(+-]*[\d,.]+[)\s]*$/.test(value)) hasNumbers = true
      else hasText = true
      if (!cell?.formula && !cell?.formulaText) continue
      hasFormula = true
      if (!cell.formulaText || cell.calculationError || cell.missingCachedValue || cell.error) dependencyEvidenceIncomplete = true
      // Structure only. Never send raw formulas, worksheet names, lookup keys or resolved values to AI.
      const expression = (cell.formulaText || '').replace(/"(?:[^"]|"")*"/g, '""')
      const refs = /(?<![\p{L}\p{N}_.])(?:(?<sheet>'(?:[^']|'')+'|(?:\[[^\]]+\])?[\p{L}_][\p{L}\p{N}_.]*)!)?(?<first>\$?[A-Z]{1,3})(?:\$?[1-9]\d{0,6})(?::\$?(?<last>[A-Z]{1,3})\$?[1-9]\d{0,6})?(?![\p{L}\p{N}_(])|(?<![\p{L}\p{N}_.])(?:(?<wholeSheet>'(?:[^']|'')+'|(?:\[[^\]]+\])?[\p{L}_][\p{L}\p{N}_.]*)!)?(?<wholeFirst>\$?[A-Z]{1,3}):\$?(?<wholeLast>[A-Z]{1,3})(?![\p{L}\p{N}_(])/giu
      for (const match of expression.matchAll(refs)) {
        const target = (match.groups?.sheet || match.groups?.wholeSheet || '').replace(/^'|'$/g, '').replace(/''/g, "'")
        const first = index(match.groups?.first || match.groups!.wholeFirst), last = index(match.groups?.last || match.groups?.wholeLast || match.groups!.first)
        if (last < first || first > 511) { dependencyEvidenceIncomplete = true; continue }
        if (last > 511 || last - first + 1 > 128) dependencyEvidenceIncomplete = true
        const referenced = Array.from({ length: Math.min(last - first + 1, 128, 512 - first) }, (_, i) => first + i)
        if (!target || sameSheet(target, sheet.name)) {
          referenced.filter(i => i < columns.length).forEach(i => dependencies.add(i))
          continue
        }
        const external = /\[|\]|[\\/]/.test(target)
        const matches = external ? [] : workbookSheets.map((s, i) => ({ s, i })).filter(({ s }) => sameSheet(s.name, target))
        const sheetIndex = matches.length === 1 && matches[0].i <= 511 ? matches[0].i : -1
        const status = external ? 'external-review' : sheetIndex >= 0 ? 'supplied' : 'missing'
        const sourceType = external ? 'unknown' : /attendance|timesheet|muster/i.test(target) ? 'attendance' : /salary|payroll|wages/i.test(target) ? 'salary' : 'other'
        const key = `${status}:${sheetIndex}:${target.toLocaleLowerCase('en-US')}:${first}:${last}`
        if (crossSheet.has(key) || crossSheet.size < 32) crossSheet.set(key, { sheetIndex, sourceType, status, columnIndexes: referenced })
        else dependencyEvidenceIncomplete = true
      }
      // Parentheses inside a quoted worksheet/file name are not function calls.
      const functionExpression = expression.replace(/'(?:[^']|'')+'!/g, 'SHEET!')
      for (const match of functionExpression.matchAll(/\b([A-Z_][A-Z0-9._]*)\s*\(/gi)) {
        const name = match[1].toUpperCase().replace(/^(?:_?XLFN\.|_?XLWS\.)+/, '')
        if (allowedFunctions.has(name)) functions.add(name)
        else dependencyEvidenceIncomplete = true
      }
    }
    return { columnIndex: column.columnIndex, sourceHeader: safeHeader(column.sourceHeader), label: safeHeader(column.label).slice(0, 80), kind: column.kind,
      formulaDependencies: [...dependencies].sort((a, b) => a - b).slice(0, 128), formulaFunctions: [...functions].sort().slice(0, 24),
      crossSheetDependencies: [...crossSheet.values()], dependencyEvidenceIncomplete, hasFormula, hasNumbers, hasText, hasBlanks }
  }) }
}
