import type { DocumentInput, DocumentTemplate } from '../services/recruitmentDocumentService'
import { documentHeaderKey, suggestRosterMapping } from './recruitmentDocumentLayout'
import { downloadXlsx, type XlsxCell, type XlsxSheet } from './xlsx'

export const rosterImportTemplateSheet = 'Employees'
export type RosterImportTemplateContext = { sourceGroup?: string; workLocationName?: string; defaults?: Record<string, string> }
const locationInputNames = new Set(['Location', 'Job location', 'Work location', 'Work location name', 'Institute', 'Institute name', 'Institution', 'Institution name', 'Work location / institute', 'Work location / institution', 'Office location', 'Posting location', 'Place of posting', 'Name of institute', 'Name of institution', 'Name of the institute', 'Name of the institution'].map(documentHeaderKey))
const hasLocationInput = (fields: DocumentInput[]) => fields.some(field => [field.key, field.label, ...field.sourceHeaders].some(name => locationInputNames.has(documentHeaderKey(name))))

// Prefer the configured source vocabulary, but never let a shared alias map
// two document inputs to the same employee column.
export function rosterImportTemplateHeaders(fields: DocumentInput[]) {
  const owners = new Map<string, Set<string>>()
  for (const field of fields) {
    for (const header of field.sourceHeaders) {
      const key = documentHeaderKey(header)
      if (key) owners.set(key, new Set([...(owners.get(key) || []), field.key]))
    }
  }
  const canonical = fields.map(field => `${field.label.trim() || field.key} [${field.key}]`)
  const headers = fields.map((field, index) => field.sourceHeaders.find(header => {
    const key = documentHeaderKey(header)
    return key && owners.get(key)?.size === 1 && !canonical.some((value, other) => other !== index && documentHeaderKey(value) === key)
  })?.trim() || canonical[index])
  return hasLocationInput(fields) ? headers : [...headers, 'Location']
}

export function isRosterImportTemplate(template: DocumentTemplate, headers: string[]) {
  const expected = rosterImportTemplateHeaders(template.fields)
  return template.fields.length > 0 && headers.length === expected.length && headers.every((header, index) => header === expected[index])
}

export function templateRosterMapping(fields: DocumentInput[], headers: string[]) {
  const suggested = suggestRosterMapping(fields, headers)
  const expected = rosterImportTemplateHeaders(fields)
  fields.forEach((field, index) => {
    const matches = headers.flatMap((header, column) => header === expected[index] ? [column] : [])
    if (matches.length === 1) suggested[field.key] = matches[0]
  })
  return suggested
}

const heading = (value: string): XlsxCell => ({ value, style: 'header' })
const text = (value: string): XlsxCell => ({ value, type: 'text', style: 'text' })
const formatHelp: Record<DocumentInput['type'], string> = {
  text: 'Text. Keep phone numbers, employee codes and other identifiers as text.',
  date: 'Date as YYYY-MM-DD, for example 2026-12-01.',
  money: 'Amount as a number, for example 25000.50. Omit currency symbols.',
  number: 'Number, for example 12. Omit units and currency symbols.',
  percent: 'Percentage as 30% or 0.30 for thirty percent.',
}

function requirement(field: DocumentInput) {
  if (field.key === 'candidateName') return 'Required for import'
  if (field.key === 'candidateEmail' || field.key === 'candidatePhone') return field.required ? 'Email or phone for import; required for letter' : 'Email or phone required for import'
  return field.required ? 'Required for letter' : 'Optional'
}

export function buildRosterImportTemplate(template: DocumentTemplate, context: RosterImportTemplateContext = {}): XlsxSheet[] {
  if (!template.fields.length) throw new Error('The selected document template has no employee input fields.')
  const headers = rosterImportTemplateHeaders(template.fields)
  const instructions: XlsxCell[][] = [
    [heading('Employee list import template'), heading(template.templateName)],
    ['Worksheet', 'Fill Employees only. Row 1 contains column headers; enter one employee per row from row 2.'],
    ['Document template', template.templateName],
    ['Import batch', context.sourceGroup?.trim() || 'Choose an import batch name in the import window.'],
    ['Work location / institute', context.workLocationName?.trim() || 'Fill the location / institute column per employee, or choose one existing work location in the import window.'],
    ['Before upload', 'Keep the headers unchanged. Save this workbook as .xlsx, then upload it in Import employee list.'],
    ['Import settings', 'Select worksheet Employees and header row 1. Review mapping and Preview list before saving.'],
    ['Identity', 'Employee name and a valid email or phone are required. Names alone never merge profiles.'],
    ['Phone', 'Use a valid 10-digit phone number. The +91 prefix is accepted. Keep phone cells as text.'],
    ['Duplicates', 'Do not repeat an email or phone across different employees. Resolve identity issues in the preview.'],
    ['Batch meaning', 'Import batch is your label for this uploaded list, for example November joiners. It is separate from the client, JD and work location.'],
    ['Re-upload', 'Reuse the same client, document template and import batch to update matched rows; blank incoming values retain saved details.'],
    ['Another list', 'Choose a different import batch name when you need a separate list.'],
    ['Per-employee location', 'On Save reviewed profiles, location / institute names reuse a matching master in the same client. A new master is created when your access allows it.'],
    ['One location for this list', 'Choosing an existing work location in the import window applies it to all imported rows and overrides their source location names.'],
    ['Location preview', 'Preview checks location names without saving or creating masters. Review location status before saving the profiles.'],
    ['Location details', 'Linked master location and institute details are used for letters. A linked work location is required before release.'],
    ['Missing details', 'Missing letter fields can remain pending after import. Complete required details before generating letters.'],
    ['Amounts', 'Monthly wages remain monthly document inputs. Do not multiply them by 12 or treat them as annual CTC.'],
    ['Cell format', 'Blank employee cells are formatted as text to preserve identifiers and YYYY-MM-DD dates. Enter values, not formulas.'],
    ['Defaults', 'Defaults below are reference values for unmapped fields. Empty cells in mapped columns do not apply these defaults.'],
    ['Blank rows', 'The 25 blank rows are a starting area. Add more rows as needed, up to 2000 named employees per import.'],
    ['Examples', 'No example employees are included. Import only your real, reviewed employee rows.'],
    [],
    ['Field guide', 'Required for import identifies profile inputs; Required for letter identifies document readiness.'],
    [heading('Column header'), heading('Document field'), heading('Requirement'), heading('Value type'), heading('Default when unmapped'), heading('Entry guidance')],
    ...template.fields.map((field, index): XlsxCell[] => [
      text(headers[index]), text(`${field.label} [${field.key}]`), requirement(field), field.type,
      text(context.defaults?.[field.key] ?? field.defaultValue ?? ''), formatHelp[field.type],
    ]),
    ...(!hasLocationInput(template.fields) ? [[text('Location'), text('Import metadata'), 'Optional', 'text', '', 'Location / institute name for this employee. It is matched or created within the selected client on reviewed import; it is separate from document field mapping.'] as XlsxCell[]] : []),
  ]
  return [
    { name: rosterImportTemplateSheet, freezeRows: 1, columnWidths: headers.map(header => Math.min(46, Math.max(24, header.length + 2))), rows: [headers.map(heading), ...Array.from({ length: 25 }, () => headers.map(() => text('')))] },
    { name: 'Instructions', freezeRows: 1, columnWidths: [38, 78, 40, 20, 30, 84], rows: instructions },
  ]
}

export function downloadRosterImportTemplate(template: DocumentTemplate, context: RosterImportTemplateContext = {}) {
  const name = (template.templateName || 'Employee list').replace(/[<>:"/\\|?*\u0000-\u001f]/g, '-').slice(0, 150)
  downloadXlsx(`${name}-import-template.xlsx`, buildRosterImportTemplate(template, context))
}
