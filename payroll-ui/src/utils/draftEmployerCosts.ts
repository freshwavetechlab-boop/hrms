import type { RunEmployee } from '../types/payroll'

export type DraftEmployerCost = { employeeId: number; componentCode: string; amount: number; reference: string; notes: string }
export function prepareDraftEmployerCosts(text: string, employees: RunEmployee[]): DraftEmployerCost[] {
  const rows: string[][] = []; let row: string[] = [], cell = '', quoted = false
  for (let i = 0; i <= text.length; i++) {
    const c = text[i]
    if (c === '"') { if (quoted && text[i + 1] === '"') { cell += '"'; i++ } else quoted = !quoted }
    else if (!quoted && (c === ',' || c === '\n' || c === undefined)) { row.push(cell.replace(/\r$/, '').trim()); cell = ''; if (c !== ',') { if (row.some(v => v)) rows.push(row); row = [] } }
    else if (c !== undefined) cell += c
  }
  if (quoted) throw Error('CSV contains an unclosed quoted field.')
  const headers = rows.shift()?.map(v => v.replace(/^\uFEFF/, '')) ?? []
  const fields = ['employeeCode', 'componentCode', 'amount', 'reference', 'notes']
  if (fields.some(field => headers.filter(value => value === field).length !== 1) || !rows.length || rows.length > 1000) throw Error(`Supply 1–1000 rows with columns ${fields.join(', ')}.`)
  const seen = new Set<string>()
  return rows.map((values, index) => {
    const get = (field: string) => values[headers.indexOf(field)] || ''
    const matches = employees.filter(employee => employee.employeeCode.trim().toUpperCase() === get('employeeCode').toUpperCase() && !employee.isSkipped)
    const amount = Number(get('amount')), componentCode = get('componentCode').toUpperCase(), reference = get('reference'), notes = get('notes')
    const key = `${matches[0]?.employeeId}|${componentCode}|${reference}`
    if (matches.length !== 1 || !componentCode || componentCode.length > 50 || !Number.isFinite(amount) || amount <= 0 || amount > 1e9 || Math.abs(amount * 100 - Math.round(amount * 100)) > .000001 || !/^[A-Za-z0-9:._-]{5,100}$/.test(reference) || !notes || notes.length > 300 || seen.has(key)) throw Error(`Row ${index + 2}: review the employee, employer component, positive amount, unique source reference and reason.`)
    seen.add(key)
    return { employeeId: matches[0].employeeId, componentCode, amount, reference, notes }
  })
}
