import type { Component, Structure } from '../types/payroll'
import { evaluatePayrollFormula, roundPayroll } from './payrollFormula'

export const money = (value: number | undefined) => `Rs ${Math.round(value || 0).toLocaleString('en-IN')}`
export const percent = (value: number | undefined) => value === undefined || value === null ? '-' : `${value > 0 ? '+' : ''}${value}%`
export type SalaryCalculationContext = { payrollDays?: number; payableDays?: number; presentDays?: number }
export type SalaryCalculationRow = { line: Structure['lines'][number]; component: Component; monthly: number; annual: number; earned?: number; source?: string; calculationType?: string; proRata?: boolean; error?: string; dailyRate?: number; units?: number }
export type SalaryTotals = { gross: number; deductions: number; net: number; employerCost: number }

const numberFrom = (value: string | number | undefined) => {
  const text = String(value ?? '').replace(/,/g, '').trim()
  return Number(text) || Number.parseFloat(text) || 0
}

const hasFormulaSyntax = (value: string) => /[-+*/()%A-Z_]/i.test(value)
const sourceFor = (line: Structure['lines'][number], component: Component) => {
  const source = line.formula || line.value || component.formula || component.value || ''
  const type = line.calculationType || component.calculationType
  if (/^\d+(\.\d+)?\s*%?$/.test(source.trim())) {
    const rate = source.trim().replace(/%$/, '').trim()
    if (type === 'Percentage of CTC') return `CTC * ${rate}%`
    if (type === 'Percentage of Component') return `${line.baseComponent || component.baseComponent || 'BASIC'} * ${rate}%`
  }
  return source
}
const lineCalculationType = (line: Structure['lines'][number], component: Component) => line.calculationType || component.calculationType
const isSummaryCode = (code: string) => ['GROSS_EARNED', 'NET_PAY', 'EMPLOYER_COST'].includes(code.toUpperCase())
const normalizeCalculationType = (value: string) =>
  value === 'Daily Rate' ? 'Daily Rate' :
  value === 'Percentage of CTC' || value === 'Percentage of Component' || value === 'Formula' ? 'Formula' :
  value === 'Balancing Amount' || value === 'Residual / Balancing' ? 'Residual / Balancing' :
  value === 'Manual Entry' || value === 'Manual Override' || value === 'Manual / Variable' ? 'Manual / Variable' :
  value === 'Slab Based' ? 'Slab Based' : 'Fixed Amount'
// Employee-specific overrides are valid for fixed, formula, percentage, slab
// and balancing components. Only calculated summary rows must stay derived.
export const canOverrideSalaryComponent = (component: Component) => !isSummaryCode(component.code)
export const hasDailyRateComponents = (components: Component[], structure?: Structure) => !!structure?.lines.some(line => components.some(component => String(component.id) === String(line.componentId) && component.calculationType === 'Daily Rate'))

function slabValue(source: string, baseAmount: number) {
  for (const slab of source.split(';')) {
    const [range, value] = slab.split(':')
    if (!range || value === undefined) continue
    const amount = numberFrom(value)
    if (range.includes('+') && baseAmount >= numberFrom(range)) return amount
    const [from, to] = range.split('-').map(numberFrom)
    if (baseAmount >= from && baseAmount <= to) return amount
  }
  return 0
}

export function calculateSalaryDetails(ctc: number, components: Component[], salaryStructure?: Structure, overrides: Record<string, string | number> = {}, context: SalaryCalculationContext = {}): SalaryCalculationRow[] {
  const monthlyCtc = roundPayroll(ctc / 12)
  const values: Record<string, string> = {}
  const earnedValues: Record<string, string> = {}
  const payrollDays = Number(context.payrollDays ?? 30)
  const payableDays = Math.max(0, Number(context.payableDays ?? payrollDays))
  const presentDays = Math.max(0, Number(context.presentDays ?? payableDays))
  const componentById = new Map(components.map(component => [String(component.id), component]))
  const structureLines = salaryStructure?.lines ?? []
  const candidates = structureLines.map((line, index) => ({ line, index, component: componentById.get(String(line.componentId)) })).filter((item): item is { line: Structure['lines'][number]; index: number; component: Component } => !!item.component?.active).sort((a, b) => (Number(a.component.priority) || 999) - (Number(b.component.priority) || 999) || a.index - b.index)
  const ordered: typeof candidates = []
  const visited = new Set<string>(), visiting = new Set<string>(), orderErrors = new Map<string, string>()
  const visit = (item: typeof candidates[number]) => {
    const id = String(item.component.id)
    if (visited.has(id)) return
    if (visiting.has(id)) { orderErrors.set(id, `Circular salary formula dependency: ${item.component.code}.`); return }
    visiting.add(id)
    if (normalizeCalculationType(lineCalculationType(item.line, item.component)) === 'Formula') {
      for (const token of (sourceFor(item.line, item.component) || '').toUpperCase().match(/\b[A-Z_][A-Z0-9_]*\b/g) || []) {
        if (['GROSS', 'GROSS_EARNED', 'CTC', 'MONTHLY_CTC', 'ANNUAL_CTC'].includes(token)) continue
        const code = token.replace(/_(MONTHLY|EARNED)$/, '')
        const dependency = candidates.find(row => row.component.code.toUpperCase() === token || String(row.component.id) === token)
          || candidates.find(row => row.component.code.toUpperCase() === code || String(row.component.id) === code)
        if (dependency) visit(dependency)
      }
    }
    visiting.delete(id); visited.add(id); ordered.push(item)
  }
  candidates.forEach(visit)
  const evaluate = (text: string, byCode: Record<string, number>) => {
    return evaluatePayrollFormula(text, { ...byCode, GROSS: monthlyCtc, CTC: monthlyCtc, MONTHLY_CTC: monthlyCtc, ANNUAL_CTC: monthlyCtc * 12, PAYROLL_DAYS: payrollDays, TOTAL_DAYS: payrollDays, WORKING_DAYS: payrollDays, PAYABLE_DAYS: payableDays, PRESENT_DAYS: presentDays, LOP_DAYS: Math.max(0, payrollDays - payableDays) })
  }
  const rows: SalaryCalculationRow[] = []
  for (const { line, component } of ordered) {
    const byCode = Object.entries(values).reduce<Record<string, number>>((acc, [id, value]) => {
      const code = componentById.get(id)?.code.toUpperCase() ?? id.toUpperCase()
      const amount = Number(value)
      const earnedAmount = Number(earnedValues[id] ?? value)
      acc[code] = amount
      acc[`${code}_MONTHLY`] = amount
      acc[`${code}_EARNED`] = earnedAmount
      return acc
    }, {})
    const earnings = rows.filter(row => ['Earning', 'Reimbursement'].includes(row.component.category))
    byCode.EARNINGS_BEFORE_THIS = earnings.reduce((sum, row) => sum + row.monthly, 0)
    byCode.FIXED_EARNINGS_BEFORE_THIS = earnings.filter(row => row.component.payType === 'Fixed Pay').reduce((sum, row) => sum + row.monthly, 0)
    byCode.FIXED_EARNINGS = byCode.FIXED_EARNINGS_BEFORE_THIS
    byCode.GROSS_EARNED = earnings.reduce((sum, row) => sum + (row.earned ?? row.monthly), 0)
    byCode.EARNED_EARNINGS_BEFORE_THIS = byCode.GROSS_EARNED
    byCode.MONTHLY_EARNINGS_BEFORE_THIS = byCode.EARNINGS_BEFORE_THIS
    const source = sourceFor(line, component)
    let monthly = 0
    const calcType = normalizeCalculationType(lineCalculationType(line, component))
    let error: string | undefined
    let dailyRate: number | undefined
    try {
    if (orderErrors.has(String(component.id))) throw Error(orderErrors.get(String(component.id)))
    if (!Number.isInteger(payrollDays) || payrollDays < 1 || payrollDays > 31) throw Error('Payroll days must be a whole number between 1 and 31.')
    if (payableDays > payrollDays || presentDays > payrollDays) throw Error('Payable/present days cannot exceed payroll days.')
    if ((component.calculationType === 'Daily Rate') !== (calcType === 'Daily Rate')) throw Error('Use a separate component to change between daily and monthly calculation.')
    if (calcType === 'Daily Rate') {
      dailyRate = Number(overrides[String(component.id)] ?? source)
      if (component.category !== 'Earning' || !Number.isFinite(dailyRate) || dailyRate <= 0) throw Error('Daily wages require a positive numeric rate and an earning component.')
      monthly = dailyRate * payrollDays
    }
    else if (calcType === 'Manual / Variable') monthly = 0
    else if (calcType === 'Slab Based') monthly = slabValue(source, byCode.GROSS_EARNED ?? monthlyCtc)
    else if (calcType === 'Formula') {
      monthly = evaluate(source, byCode)
    }
    else if (calcType === 'Residual / Balancing') {
      monthly = /SUM\s*\(/i.test(source) ? evaluate(source, byCode) : Math.max(0, (byCode[line.baseComponent || component.baseComponent || 'GROSS'] ?? monthlyCtc) - byCode.EARNINGS_BEFORE_THIS)
    } else {
      const amount = numberFrom(source || component.value)
      monthly = amount || (hasFormulaSyntax(source) ? evaluate(source, byCode) : 0)
    }
    if (calcType !== 'Daily Rate' && canOverrideSalaryComponent(component) && overrides[String(component.id)] !== undefined) monthly = numberFrom(overrides[String(component.id)])
    monthly = Math.max(0, roundPayroll(monthly))
    } catch (exception) { error = exception instanceof Error ? exception.message : 'Invalid salary calculation.'; monthly = Number.NaN }
    const proRataText = (line.proRataOverride || '').toLowerCase()
    const proRata = calcType === 'Daily Rate' ? false : proRataText === 'true' || proRataText === 'yes' ? true : proRataText === 'false' || proRataText === 'no' ? false : component.proRata
    const earned = error ? Number.NaN : dailyRate !== undefined ? roundPayroll(dailyRate * payableDays) : proRata ? roundPayroll(monthly * payableDays / payrollDays) : monthly
    values[component.id] = String(monthly)
    earnedValues[component.id] = String(earned)
    rows.push({ line, component, monthly, earned, annual: roundPayroll(monthly * 12), source, calculationType: calcType, proRata, error, dailyRate, units: dailyRate === undefined ? undefined : payableDays })
  }
  return rows
}

export function calculateSalaryJson(ctc: number, components: Component[], salaryStructure?: Structure, overrides: Record<string, string | number> = {}) {
  return JSON.stringify(Object.fromEntries(calculateSalaryDetails(ctc, components, salaryStructure, overrides).map(row => [row.component.id, String(row.dailyRate ?? row.monthly)])))
}

export function calculateSalaryTotals(rows: SalaryCalculationRow[]): SalaryTotals {
  const grossRow = rows.find(row => row.component.code.toUpperCase() === 'GROSS_EARNED')
  const netRow = rows.find(row => row.component.code.toUpperCase() === 'NET_PAY')
  const employerCostRow = rows.find(row => row.component.code.toUpperCase() === 'EMPLOYER_COST')
  const gross = grossRow?.monthly ?? rows.filter(row => ['Earning', 'Reimbursement'].includes(row.component.category) && !isSummaryCode(row.component.code)).reduce((sum, row) => sum + row.monthly, 0)
  const deductions = rows.filter(row => row.component.category === 'Deduction' && row.component.code.toUpperCase() !== 'NET_PAY').reduce((sum, row) => sum + row.monthly, 0)
  const net = netRow?.monthly ?? Math.max(0, gross - deductions)
  const employerCost = employerCostRow?.monthly ?? rows.filter(row => row.component.category === 'Benefit' || row.component.componentRole === 'Employer Contribution').reduce((sum, row) => sum + row.monthly, 0)
  return { gross, deductions, net, employerCost }
}
