import type { RosterExchangeColumn, RosterExchangeMode, RosterExchangeTemplate } from '../services/recruitmentRosterExchangeService'
import { downloadXlsx, type XlsxCell, type XlsxSheet } from './xlsx'

export function standardRosterCell(column: RosterExchangeColumn, text: string): XlsxCell {
  if (['money', 'amount', 'integer'].includes(column.type) && /^-?\d+(?:\.\d+)?$/.test(text) && text.replace(/[-.]/g, '').replace(/^0+/, '').length <= 15)
    return { value: Number(text), type: 'number', style: column.type === 'money' ? 'currency' : 'operand' }
  return { value: text, type: 'text', style: 'text' }
}

export function standardRosterSheets(template: RosterExchangeTemplate, mode: RosterExchangeMode): XlsxSheet[] {
  const rows = mode === 'Update' ? template.rows : [{ rowAction: 'Apply' } as Record<string, string>]
  const reference = '__FREVO_TEMPLATE__:' + JSON.stringify({ version: template.version, clientId: template.clientId, templateId: template.templateId, importBatch: template.sourceGroup })
  return [
    { name: 'Employees', rows: [[...template.columns.map(c => ({ value: c.header, style: 'header' as const })), { value: reference, type: 'text' }],
      ...rows.map(row => [...template.columns.map(c => standardRosterCell(c, row[c.key] ?? '')), ''])],
      hiddenColumns: [...template.columns.flatMap((c, i) => c.key === 'revision' ? [i] : []), template.columns.length],
      freezeRows: 1, freezeColumns: 5, headerRowHeight: 58, columnWidths: [...template.columns.map(c => c.key === 'revision' ? 70 : c.type === 'date' ? 19 : c.header.length > 35 ? 34 : 25), 10] },
  ]
}

export function downloadStandardRosterTemplate(template: RosterExchangeTemplate, mode: RosterExchangeMode) {
  downloadXlsx(`employee-standard-${mode === 'Update' ? 'corrected-version' : 'insertion'}-client-${template.clientId}.xlsx`, standardRosterSheets(template, mode))
}
