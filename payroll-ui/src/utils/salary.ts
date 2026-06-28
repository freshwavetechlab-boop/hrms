import type { Component, Structure } from '../types/payroll'

export const money = (value: number | undefined) => `Rs ${Math.round(value || 0).toLocaleString('en-IN')}`
export const percent = (value: number | undefined) => value === undefined || value === null ? '-' : `${value > 0 ? '+' : ''}${value}%`
export type SalaryCalculationRow = { line: Structure['lines'][number]; component: Component; monthly: number; annual: number }
export type SalaryTotals = { gross: number; deductions: number; net: number; employerCost: number }

const escapeRegExp = (value: string) => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
const numberFrom = (value: string | number | undefined) => {
  const text = String(value ?? '').replace(/,/g, '').trim()
  return Number(text) || Number.parseFloat(text) || 0
}

const hasFormulaSyntax = (value: string) => /[-+*/()%A-Z_]/i.test(value)
const sourceFor = (line: Structure['lines'][number], component: Component) => line.value || component.formula || component.value
const isSummaryCode = (code: string) => ['GROSS_EARNED', 'NET_PAY', 'EMPLOYER_COST'].includes(code.toUpperCase())
const normalizeCalculationType = (value: string) =>
  value === 'Percentage of CTC' || value === 'Percentage of Component' || value === 'Formula' ? 'Formula' :
  value === 'Balancing Amount' || value === 'Residual / Balancing' ? 'Residual / Balancing' :
  value === 'Manual Entry' || value === 'Manual Override' || value === 'Manual / Variable' ? 'Manual / Variable' :
  value === 'Slab Based' ? 'Slab Based' : 'Fixed Amount'

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

export function calculateSalaryDetails(ctc: number, components: Component[], salaryStructure?: Structure): SalaryCalculationRow[] {
  const monthlyCtc = ctc / 12
  const values: Record<string, string> = {}
  const componentById = new Map(components.map(component => [String(component.id), component]))
  const structureLines = salaryStructure?.lines ?? []
  const ordered = structureLines.map((line, index) => ({ line, index, component: componentById.get(String(line.componentId)) })).filter((item): item is { line: { componentId: string; value: string }; index: number; component: Component } => !!item.component?.active).sort((a, b) => (Number(a.component.priority) || 999) - (Number(b.component.priority) || 999) || a.index - b.index)
  const evaluate = (text: string, byCode: Record<string, number>) => {
    const references = { GROSS: monthlyCtc, CTC: monthlyCtc, MONTHLY_CTC: monthlyCtc, ANNUAL_CTC: ctc, PAYROLL_DAYS: 30, PAYABLE_DAYS: 30, PRESENT_DAYS: 30, LOP_DAYS: 0, ...byCode }
    const earningsSum = Object.entries(byCode).filter(([code]) => !/^\d+$/.test(code)).reduce((sum, [, value]) => sum + value, 0)
    let formula = text.toUpperCase()
      .replace(/×/g, '*')
      .replace(/÷/g, '/')
      .replace(/SUM\s*\(\s*(FIXED\s+)?EARNINGS(_BEFORE_THIS)?\s*\)/g, String(earningsSum))
      .replace(/(\d+(?:\.\d+)?)\s*%\s*OF\s*([A-Z0-9_]+)/g, '$2*$1/100')
      .replace(/([A-Z0-9_]+)\s*\*\s*(\d+(?:\.\d+)?)\s*%/g, '$1*$2/100')
      .replace(/(\d+(?:\.\d+)?)%/g, '$1/100')
      .replace(/\bMIN\(/g, 'Math.min(')
      .replace(/\bMAX\(/g, 'Math.max(')
      .replace(/\bROUNDDOWN\(/g, 'Math.floor(')
      .replace(/\bROUNDUP\(/g, 'Math.ceil(')
      .replace(/\bROUND\(/g, 'Math.round(')
      .replace(/\s+/g, '')
    Object.entries(references).sort((a, b) => b[0].length - a[0].length).forEach(([code, amount]) => {
      formula = formula.replace(new RegExp(`(^|[^A-Z0-9_])${escapeRegExp(code)}(?=$|[^A-Z0-9_])`, 'g'), `$1${amount}`)
    })
    formula = formula.replace(/[^0-9+\-*/().,Mathminaxfloorceud]/g, '')
    try { return Math.round(Number(Function(`"use strict";return (${formula})`)()) || 0) } catch { return 0 }
  }
  const rows: SalaryCalculationRow[] = []
  for (const { line, component } of ordered) {
    const byCode = Object.fromEntries(Object.entries(values).map(([id, value]) => [componentById.get(id)?.code.toUpperCase() ?? id, numberFrom(value)]))
    const source = sourceFor(line, component)
    let monthly = 0
    const calcType = normalizeCalculationType(component.calculationType)
    if (calcType === 'Manual / Variable') monthly = 0
    else if (calcType === 'Slab Based') monthly = slabValue(source, byCode.GROSS_EARNED || monthlyCtc)
    else if (/SUM\s+EARNED\s+COMPONENTS/i.test(source)) monthly = rows.filter(row => row.component.category === 'Earning' && (row.component.code.toUpperCase().includes('EARNED') || row.component.code.toUpperCase() === 'LAPTOP_ALLOWANCE')).reduce((sum, row) => sum + row.monthly, 0) + (byCode.TA_DA || 0)
    else if (calcType === 'Formula') {
      const fallback = component.calculationType === 'Percentage of CTC' ? `CTC * ${component.value}%` : component.calculationType === 'Percentage of Component' ? `${component.baseComponent || 'BASIC'} * ${component.value}%` : source
      monthly = evaluate(source || fallback, byCode)
    }
    else if (calcType === 'Residual / Balancing') {
      const used = Object.entries(values).filter(([id]) => components.find(item => String(item.id) === id)?.category === 'Earning').reduce((sum, [, value]) => sum + Number(value), 0)
      monthly = /SUM\s*\(/i.test(source) ? evaluate(source, byCode) : Math.max(0, Math.round(monthlyCtc - used))
    } else {
      const amount = numberFrom(source || component.value)
      monthly = amount || (hasFormulaSyntax(source) ? evaluate(source, byCode) : 0)
    }
    values[component.id] = String(monthly)
    rows.push({ line, component, monthly, annual: monthly * 12 })
  }
  return rows
}

export function calculateSalaryJson(ctc: number, components: Component[], salaryStructure?: Structure) {
  return JSON.stringify(Object.fromEntries(calculateSalaryDetails(ctc, components, salaryStructure).map(row => [row.component.id, String(row.monthly)])))
}

export function calculateSalaryTotals(rows: SalaryCalculationRow[]): SalaryTotals {
  const grossRow = rows.find(row => row.component.code.toUpperCase() === 'GROSS_EARNED')
  const netRow = rows.find(row => row.component.code.toUpperCase() === 'NET_PAY')
  const employerCostRow = rows.find(row => row.component.code.toUpperCase() === 'EMPLOYER_COST')
  const gross = grossRow?.monthly ?? rows.filter(row => ['Earning', 'Reimbursement'].includes(row.component.category) && !isSummaryCode(row.component.code)).reduce((sum, row) => sum + row.monthly, 0)
  const deductions = rows.filter(row => row.component.category === 'Deduction' && row.component.code.toUpperCase() !== 'NET_PAY').reduce((sum, row) => sum + row.monthly, 0)
  const net = netRow?.monthly ?? Math.max(0, gross - deductions)
  const employerCost = employerCostRow?.monthly ?? rows.filter(row => row.component.category === 'Employer Contribution').reduce((sum, row) => sum + row.monthly, 0)
  return { gross, deductions, net, employerCost }
}
