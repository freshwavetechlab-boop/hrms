import { buildExcelPayslipRows, defaultPayslipMapping, excelColumnLetter, payslipHeaders, translatePayslipSharedFormula, type PayslipCell, type PayslipColumn, type PayslipSheet } from './excelPayslipImport'

export type PayslipWorkbookPart = { fileName: string; sheet: PayslipSheet; headerRow: number; columns: PayslipColumn[]; excludedRows?: number[] }
const key = (text: string) => text.normalize('NFKC').toLowerCase().replace(/[^a-z0-9]/g, '')
const money = new Set(['earning', 'deduction', 'employer', 'netPay', 'grossTotal', 'deductionTotal'])

// A closing SUM in the mapped net column identifies the primary salary table.
// Copied calculations below it remain in the source and in the exclusion review.
// Only a direct, contiguous range starting immediately after the reviewed header qualifies.
export function reviewPayslipTableBoundary(part: PayslipWorkbookPart) {
  const name = part.columns.find(c => c.kind === 'employeeName'), net = part.columns.find(c => c.kind === 'netPay')
  if (!name || !net) return null
  for (const row of part.sheet.rows.filter(r => r.sourceRow > part.headerRow)) {
    const text = row.cells[name.columnIndex]?.value.trim() || ''
    if (text && !/^(grand\s*)?total(?:s|\s|:|$)/i.test(text)) continue
    const match = /^\s*=?SUM\(\$?([A-Z]{1,3})\$?([1-9]\d*):\$?\1\$?([1-9]\d*)\)\s*$/i.exec(row.cells[net.columnIndex]?.formulaText || '')
    if (!match || match[1].toUpperCase() !== excelColumnLetter(net.columnIndex) || Number(match[2]) !== part.headerRow + 1 || Number(match[3]) !== row.sourceRow - 1) continue
    if (!part.sheet.rows.some(r => r.sourceRow > part.headerRow && r.sourceRow < row.sourceRow && r.cells[name.columnIndex]?.value.trim())) continue
    return { totalRow: row.sourceRow, referenceRows: part.sheet.rows.filter(r => r.sourceRow > row.sourceRow && r.cells[name.columnIndex]?.value.trim()).map(r => r.sourceRow) }
  }
  return null
}

export function suggestPayslipWorksheet(sheets: PayslipSheet[]) {
  const candidates = sheets.flatMap(sheet => sheet.rows.slice(0, 20).map(row => {
    const columns = defaultPayslipMapping(payslipHeaders(sheet, row.sourceRow), sheet, row.sourceRow)
    return { sheet, headerRow: row.sourceRow, score: (columns.filter(c => c.kind === 'employeeName').length === 1 ? 20 : 0) + (columns.filter(c => c.kind === 'netPay').length === 1 ? 20 : 0) + Math.min(10, columns.filter(c => c.kind === 'earning').length) }
  })).sort((a, b) => b.score - a.score)
  return candidates[0]?.score >= 40 ? candidates[0] : null
}

// One monthly snapshot with a raw row block for every reviewed source sheet.
// No input file is edited. Local formula references (including absolute rows) move with their block.
export function consolidatePayslipWorkbooks(parts: PayslipWorkbookPart[], clientId: number, month: string, reviewSourceSplits = false): PayslipSheet {
  if (parts.length < 2 || parts.length > 50) throw new Error('Choose between 2 and 50 salary workbooks.')
  if (!Number.isInteger(clientId) || clientId < 1 || !/^\d{4}-(0[1-9]|1[0-2])$/.test(month)) throw new Error('Choose the client and salary month before combining.')
  if (parts.some(p => p.sheet.template && p.sheet.template.clientId !== clientId)) throw new Error('A salary template belongs to another client.')
  if (parts.some(p => p.sheet.dateSystem === '1904')) throw new Error('Review 1904-date workbooks separately before consolidating.')
  const rawWidth = Math.max(...parts.map(p => p.sheet.columnCount)), fields = new Map<string, PayslipColumn>()
  const reservedLabels = new Set(['sourceworkbook', 'sourceworksheet', 'sourceexcelrow', 'payrollunit', 'reviewedsourcesplit'])
  if (parts.some(p => p.columns.some(c => c.kind !== 'ignore' && reservedLabels.has(key(c.label))))) throw new Error('Source trace labels are reserved for consolidation. Rename these mapped labels before combining.')
  const fieldKey = (c: PayslipColumn) => `${c.kind}:${key(c.label)}`
  for (const part of parts) for (const c of part.columns.filter(c => c.kind !== 'ignore')) {
    const id = ['employeeName', 'employeeCode', 'email', 'netPay'].includes(c.kind) ? c.kind : fieldKey(c)
    if (!fields.has(id)) fields.set(id, { ...c, columnIndex: rawWidth + fields.size, sourceHeader: c.label })
    // A blank in any contributing source needs approval even if another source's preset approved its blanks.
    else fields.get(id)!.blankAsZero = fields.get(id)!.blankAsZero === true && c.blankAsZero === true
  }
  const trace = ['Source workbook', 'Source worksheet', 'Source Excel row', 'Payroll unit', ...(reviewSourceSplits ? ['Reviewed source split'] : [])].map(label => ({ columnIndex: rawWidth + fields.size, sourceHeader: label, label, kind: 'info' } as PayslipColumn))
  trace.forEach((c, i) => { c.columnIndex += i })
  const columns: PayslipColumn[] = [...Array.from({ length: rawWidth }, (_, columnIndex) => ({ columnIndex, sourceHeader: `Original ${excelColumnLetter(columnIndex)}`, label: `Original ${excelColumnLetter(columnIndex)}`, kind: 'ignore' as const })), ...fields.values(), ...trace]
  if (columns.length > 1500) throw new Error('Combined source exceeds 1500 columns. Consolidate smaller groups.')
  const rows: PayslipSheet['rows'] = [{ sourceRow: 1, cells: columns.map(c => ({ value: c.sourceHeader })) }], excluded: number[] = []
  let offset = 2, omittedOptionalErrors = 0
  const boundaryWarnings: string[] = []
  for (const part of parts) {
    const name = part.columns.find(c => c.kind === 'employeeName'), net = part.columns.find(c => c.kind === 'netPay')
    if (!name || !net) throw new Error(`${part.fileName}: select employee name and net pay before combining.`)
    const boundary = reviewPayslipTableBoundary(part)
    const partExcluded = new Set([...(part.excludedRows || []), ...(boundary?.referenceRows || [])])
    if (boundary?.referenceRows.length) boundaryWarnings.push(`${part.fileName}: ${boundary.referenceRows.length} named rows below the closing net-pay total (Excel row ${boundary.totalRow}) are excluded for review. Restore exclusions if an additional salary section should be included.`)
    for (const source of part.sheet.rows) {
      const number = offset + source.sourceRow, cells: PayslipCell[] = Array.from({ length: columns.length }, () => ({ value: '' }))
      source.cells.forEach((cell, i) => {
        if (!cell) return
        cells[i] = { ...cell }
        if (cell.formulaText) {
          if (/!/.test(cell.formulaText)) cells[i].calculationError ||= 'Cross-sheet formula retained for review; use its saved value for this import.'
          else cells[i].formulaText = translatePayslipSharedFormula(cell.formulaText, 'A1', `A${offset + 1}`, true)
        }
      })
      const employee = source.sourceRow > part.headerRow && source.cells[name.columnIndex]?.value.trim()
      const hasMoney = part.columns.some(c => money.has(c.kind) && source.cells[c.columnIndex]?.value.trim())
      const hasIdentity = source.cells.slice(0, 3).some(c => c?.value.trim())
      const noteOnly = employee && !hasMoney && !hasIdentity
      if (employee && !noteOnly) {
        if (partExcluded.has(source.sourceRow)) excluded.push(number)
        const occurrences = new Set<string>()
        for (const c of part.columns.filter(c => c.kind !== 'ignore')) {
          const id = ['employeeName', 'employeeCode', 'email', 'netPay'].includes(c.kind) ? c.kind : fieldKey(c)
          if (occurrences.has(id)) throw new Error(`${part.fileName}: duplicate mapped label ${c.label}. Give each distinct component a unique label.`)
          occurrences.add(id)
          const dest = fields.get(id)!, original = source.cells[c.columnIndex]
          // Blanks in fields that exist in a source retain the operator's explicit blank policy.
          cells[dest.columnIndex] = { ...(original || { value: '' }) }
          if (['info', 'email'].includes(c.kind) && (original?.error || original?.missingCachedValue)) {
            cells[dest.columnIndex] = { value: '' }
            omittedOptionalErrors++
            continue // Original error stays in the raw block; never fabricate an identifier or contact value.
          }
          if (original?.formulaText) cells[dest.columnIndex].formulaText = cells[c.columnIndex].formulaText
          if (c.blankAsZero && !original?.value.trim() && !original?.formulaText && !original?.error) cells[dest.columnIndex] = { value: '0' }
          if (c.kind === 'info' && key(c.label) === 'attendance' && original && !original.error && original.value.trim()) {
            // New-month attendance overrides target this single canonical input; original formulas still consume it.
            cells[dest.columnIndex] = { value: original.value }
            cells[c.columnIndex] = { value: original.value, formula: true, formulaText: `${excelColumnLetter(dest.columnIndex)}${number}` }
          }
        }
        for (const dest of fields.values()) if (money.has(dest.kind) && !occurrences.has(['netPay'].includes(dest.kind) ? dest.kind : fieldKey(dest))) {
          if (dest.kind === 'grossTotal' || dest.kind === 'deductionTotal') throw new Error('A total-control column is missing from one workbook. Keep partial subtotals as information before combining.')
          cells[dest.columnIndex] = { value: '0' }
        }
        trace.forEach((c, i) => { cells[c.columnIndex] = { value: [part.fileName, part.sheet.name, String(source.sourceRow), part.fileName.replace(/\.[^.]+$/, ''), 'Confirmed'][i] } })
      } else excluded.push(number)
      rows.push({ sourceRow: number, cells })
    }
    offset += Math.max(...part.sheet.rows.map(r => r.sourceRow), part.headerRow) + 2
  }
  if (rows.length > 100000) throw new Error('Combined source exceeds the supported row count.')
  const sheet: PayslipSheet = { name: 'Combined salary', rows, columnCount: columns.length, dateSystem: '1900', template: {
    version: 1, clientId, sourceBatchId: '', month, headerRow: 1, columns: columns.map(c => ({ ...c, componentId: undefined })), excludedSourceRows: excluded,
    simpleLayout: true, warnings: ['Combined workbooks retain per-row source file, sheet and Excel row. Review missing amounts, employee identities and net reconciliation before saving.', ...boundaryWarnings, ...(omittedOptionalErrors ? [`${omittedOptionalErrors} information/contact cells contain Excel errors or have no saved value. They are blank in the preview; originals are retained in the source for correction.`] : [])]
  } }
  const result = buildExcelPayslipRows(sheet, 1, sheet.template!.columns, excluded)
  if (result.rows.length > 1000) throw new Error('Combined batch exceeds 1,000 payslips. Use smaller reviewed groups.')
  return sheet
}
