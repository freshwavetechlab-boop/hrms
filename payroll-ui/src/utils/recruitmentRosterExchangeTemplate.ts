import type { RosterExchangeColumn, RosterExchangeMode, RosterExchangeTemplate } from '../services/recruitmentRosterExchangeService'
import { downloadXlsx, type XlsxCell, type XlsxSheet } from './xlsx'

export function standardRosterCell(column: RosterExchangeColumn, text: string): XlsxCell {
  if (['money', 'amount', 'integer'].includes(column.type) && /^-?\d+(?:\.\d+)?$/.test(text) && text.replace(/[-.]/g, '').replace(/^0+/, '').length <= 15)
    return { value: Number(text), type: 'number', style: column.type === 'money' ? 'currency' : 'operand' }
  return { value: text, type: 'text', style: 'text' }
}

export function standardRosterSheets(template: RosterExchangeTemplate, mode: RosterExchangeMode): XlsxSheet[] {
  const rows = mode === 'Update' ? template.rows : [{} as Record<string, string>]
  const reference = '__FREVO_TEMPLATE__:' + JSON.stringify({ version: template.version, clientId: template.clientId, templateId: template.templateId, importBatch: template.sourceGroup })
  const plainCell = (column: RosterExchangeColumn, value: string): XlsxCell => {
    const cell = standardRosterCell(column, value)
    return typeof cell === 'object' && cell.style === 'operand' ? { ...cell, style: undefined } : cell
  }
  return [
    { name: 'Employees', rows: [[...template.columns.map(c => ({ value: c.header, type: 'text' as const, style: 'text' as const })), { value: reference, type: 'text' }],
      ...rows.map(row => [...template.columns.map(c => plainCell(c, row[c.key] ?? '')), ''])],
      hiddenColumns: [...template.columns.flatMap((c, i) => c.key === 'revision' ? [i] : []), template.columns.length],
      freezeRows: 1, freezeColumns: 5, columnWidths: [...template.columns.map(c => Math.min(54, Math.max(18, c.header.length + 2))), 10] },
  ]
}

export function downloadStandardRosterTemplate(template: RosterExchangeTemplate, mode: RosterExchangeMode) {
  const name = Array.from((template.clientName || '').trim().replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_').replace(/\s+/g, '_'))
    .slice(0, 100).join('').replace(/^[._]+|[._]+$/g, '') || `Client_${template.clientId}`
  downloadXlsx(`${name}_Employee_${mode === 'Update' ? 'Corrected_Version' : 'Import_Template'}.xlsx`, standardRosterSheets(template, mode))
}
