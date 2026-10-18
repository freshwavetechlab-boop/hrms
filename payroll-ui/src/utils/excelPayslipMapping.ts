import type { PayslipColumn, PayslipSheet } from './excelPayslipImport'

export type PayslipMappingEvidence = PayslipColumn & {
  formulaDependencies: number[]; formulaFunctions: string[]
  hasFormula: boolean; hasNumbers: boolean; hasText: boolean; hasBlanks: boolean
}
const allowedFunctions = new Set(['SUM', 'ROUND', 'ROUNDUP', 'ROUNDDOWN', 'IF', 'IFERROR', 'MIN', 'MAX', 'SUMIF', 'SUMIFS', 'COUNT', 'COUNTA', 'AND', 'OR', 'ABS', 'CEILING', 'FLOOR'])
const safeHeader = (text: string) => text.replace(/[^\s@]+@[^\s@]+\.[^\s@]+|(?:https?:\/\/|[A-Za-z]:\\|\\\\)\S+/gi, '[redacted]')
  .replace(/\d(?:[\s-]*\d){7,}/g, '[identifier]').replace(/\b[A-Z]{5}\d{4}[A-Z]\b/gi, '[identifier]').replace(/[\p{Cc}\p{Cf}<>]/gu, ' ').slice(0, 160)

// Build metadata locally. Employee names/identifiers, monetary cell values and raw formulas never leave the browser for AI.
export function buildPayslipMappingEvidence(sheet: PayslipSheet, headerRow: number, columns: PayslipColumn[], salaryTemplateId = '', excludedRows: number[] = []) {
  const excluded = new Set(excludedRows)
  const rows = sheet.rows.filter(row => row.sourceRow > headerRow && !excluded.has(row.sourceRow)).slice(0, 50)
  return { salaryTemplateId, columns: columns.map(column => {
    const dependencies = new Set<number>(), functions = new Set<string>()
    let hasFormula = false, hasNumbers = false, hasText = false, hasBlanks = false
    for (const row of rows) {
      const cell = row.cells[column.columnIndex], value = cell?.value.trim() || ''
      if (!value) hasBlanks = true
      else if (/^[₹\s(+-]*[\d,.]+[)\s]*$/.test(value)) hasNumbers = true
      else hasText = true
      if (!cell?.formula && !cell?.formulaText) continue
      hasFormula = true
      // Strip string literals, external and cross-sheet references before extracting local dependency indexes.
      const expression = (cell.formulaText || '').replace(/"(?:[^"]|"")*"/g, '""')
        .replace(/(?:'(?:[^']|'')*'|(?:\[[^\]]+\])?[\p{L}_][\p{L}\p{N}_.]*)!\$?[A-Z]{1,3}\$?\d+(?::\$?[A-Z]{1,3}\$?\d+)?/giu, '')
      for (const match of expression.matchAll(/(?<![A-Z0-9_])\$?([A-Z]{1,3})\$?\d+(?![A-Z0-9_(])/gi)) {
        const index = [...match[1].toUpperCase()].reduce((total, letter) => total * 26 + letter.charCodeAt(0) - 64, 0) - 1
        if (index < columns.length) dependencies.add(index)
      }
      // Expand local ranges so SUM(F2:J2) has F,G,H,I,J as dependencies.
      for (const match of expression.matchAll(/\$?([A-Z]{1,3})\$?\d+:\$?([A-Z]{1,3})\$?\d+/gi)) {
        const index = (text: string) => [...text.toUpperCase()].reduce((total, letter) => total * 26 + letter.charCodeAt(0) - 64, 0) - 1
        const first = index(match[1]), last = index(match[2])
        for (let i = first; i <= last && i < columns.length; i++) dependencies.add(i)
      }
      for (const match of expression.matchAll(/\b([A-Z][A-Z0-9.]*)\s*\(/gi)) if (allowedFunctions.has(match[1].toUpperCase())) functions.add(match[1].toUpperCase())
    }
    return { columnIndex: column.columnIndex, sourceHeader: safeHeader(column.sourceHeader), label: safeHeader(column.label).slice(0, 80), kind: column.kind,
      formulaDependencies: [...dependencies].sort((a, b) => a - b).slice(0, 128), formulaFunctions: [...functions].sort().slice(0, 16), hasFormula, hasNumbers, hasText, hasBlanks }
  }) }
}
