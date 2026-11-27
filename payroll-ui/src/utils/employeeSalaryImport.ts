import type { Component, Structure } from '../types/payroll'
import type { BulkImportFieldDefinition } from './smartBulkImport'
import type { ImportPreviewIssue, ImportPreviewRow } from './importPreview'
import { firstSalaryRuleText, salaryCalculationKind, salaryInputMethodIsEditable } from './salaryTemplateInput'
import { parseSalaryUnits, resolveEmployeeCtc } from './employeeCtc'

const calculatedPayrollOutputs = new Set(['GROSS_EARNED', 'NET_PAY', 'EMPLOYER_COST'])

/** Only saved employee inputs in this template have destinations in the employee salary record. */
export function employeeSalaryInputFields(template: Structure, components: Component[]): BulkImportFieldDefinition[] {
  if (!template.active || !template.id) return []
  const byId = new Map(components.map(component => [String(component.id), component]))
  const fields: BulkImportFieldDefinition[] = []
  for (const line of template.lines) {
    const component = byId.get(String(line.componentId))
    if (!component?.active || calculatedPayrollOutputs.has(component.code.toUpperCase())) continue
    const configured = firstSalaryRuleText(line.calculationType, component.calculationType)
    const method = salaryCalculationKind(configured)
    const expression = firstSalaryRuleText(line.formula, line.value, component.formula, component.value)
    const numericConstant = /^[+-]?(?:\d[\d,]*(?:\.\d*)?|\.\d+)$/.test(expression)
    if (!salaryInputMethodIsEditable(method, configured, numericConstant)) continue
    const code = `SALARY_INPUT:${component.id}`
    const unit = method === 'Daily Rate' ? 'Daily rate' : 'Monthly amount'
    fields.push({
      code, header: `${component.name} · ${unit} [${code}]`, label: `${component.name} · ${component.code} · ${unit}`,
      group: `Salary inputs · ${template.name}`, type: 'number', aliases: [component.code, component.name],
      description: unit,
    })
  }
  return fields
}

export function employeeSalaryImportIssues(headers: string[], row: ImportPreviewRow, rowNumber: number, allowNewHireDefaults = false): ImportPreviewIssue[] {
  const inputHeaders = headers.filter(header => /\[SALARY_INPUT:\d+\]$/i.test(header))
  const dateColumn = 'Salary Effective From', templateColumn = 'Salary Template Id'
  const annual = row['Annual CTC'] ?? '', monthly = row['Monthly CTC'] ?? ''
  const { issues: ctcIssues } = resolveEmployeeCtc(annual, monthly, !headers.includes(dateColumn))
  const issues: ImportPreviewIssue[] = ctcIssues.map(issue => ({ ...issue, rowNumber }))
  if (!inputHeaders.length && !headers.includes(dateColumn)) return issues
  const populated = inputHeaders.filter(header => row[header]?.trim())
  const date = row[dateColumn]?.trim(), templateId = row[templateColumn]?.trim()
  const issue = (column: string, message: string) => issues.push({ rowNumber, column, message })
  if (!populated.length && !annual.trim() && !monthly.trim() && !(allowNewHireDefaults && (date || templateId))) {
    if (date || templateId) issue(dateColumn, 'Enter a salary input, Annual CTC or Monthly CTC for this dated salary change, or clear the template and date.')
    return issues
  }
  if (!templateId || !/^[1-9]\d*$/.test(templateId)) issue(templateColumn, 'Choose the saved salary template for these inputs.')
  const parsedDate = date && /^[1-9]\d{3}-\d{2}-\d{2}$/.test(date) ? new Date(date + 'T00:00:00Z') : undefined
  if (!parsedDate || !Number.isFinite(parsedDate.getTime()) || parsedDate.toISOString().slice(0, 10) !== date) {
    issue(dateColumn, 'Enter a valid salary effective date in YYYY-MM-DD format.')
  }
  for (const column of ['Salary Json', 'Salary Template']) {
    if (row[column]?.trim()) issue(column, 'Leave this field blank when using dated salary inputs and a selected template.')
  }
  for (const header of populated) {
    if (parseSalaryUnits(row[header].trim(), 4, 10000000000000n, false, true) === null) {
      issue(header, 'Enter a number from 0 to 1,000,000,000, without currency symbols or commas. Extra decimal places are rounded to four places.')
    }
  }
  return issues
}
