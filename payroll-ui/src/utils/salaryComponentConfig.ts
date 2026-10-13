import type { Component } from '../types/payroll'
import { evaluatePayrollFormula } from './payrollFormula'

export const calculationChoices = [
  { value: 'Fixed Amount', label: 'Monthly amount' },
  { value: 'Daily Rate', label: 'Daily rate x payable days' },
  { value: 'Formula', label: 'Formula' },
  { value: 'Manual / Variable', label: 'Payroll input / adjustment' },
  { value: 'Residual / Balancing', label: 'Remaining balance' },
  { value: 'Slab Based', label: 'Slab-based' }
]
export const employeeStatutoryTypes = ['TDS', 'Professional Tax', 'PF Employee', 'VPF', 'ESI Employee', 'LWF Employee', 'NPS Employee']
export const employerStatutoryTypes = ['PF Employer', 'EPS', 'ESI Employer', 'LWF Employer', 'NPS Employer', 'Workmen Compensation']
export const formulaValues = ['CTC', 'MONTHLY_CTC', 'ANNUAL_CTC', 'GROSS', 'GROSS_EARNED', 'PAYROLL_DAYS', 'TOTAL_DAYS', 'WORKING_DAYS', 'PAYABLE_DAYS', 'PRESENT_DAYS', 'LOP_DAYS', 'EARNINGS_BEFORE_THIS', 'EARNED_EARNINGS_BEFORE_THIS', 'MONTHLY_EARNINGS_BEFORE_THIS', 'FIXED_EARNINGS_BEFORE_THIS', 'FIXED_EARNINGS']
export const calculationKind = (value: string) => value.startsWith('Percentage of ') ? 'Formula' : value === 'Balancing Amount' ? 'Residual / Balancing' : ['Manual Entry', 'Manual Override'].includes(value) ? 'Manual / Variable' : value
export function displayFormula(row: Component) {
  if (row.formula?.trim()) return row.formula
  if (row.calculationType === 'Percentage of CTC') return `CTC * ${row.value || 0}%`
  if (row.calculationType === 'Percentage of Component') return `${row.baseComponent || 'BASIC'} * ${row.value || 0}%`
  return row.formula || ''
}

export function validateSalaryComponent(row: Component, components: Component[], original?: Component): string[] {
  const errors: string[] = []
  const kind = calculationKind(row.calculationType)
  const unchanged = (keys: (keyof Component)[]) => !!original && keys.every(key => row[key] === original[key])
  if (original && (original.calculationType === 'Daily Rate') !== (row.calculationType === 'Daily Rate')) errors.push('Create a separate component to change between monthly and daily rates. Existing employee amounts must not be reinterpreted.')
  if (!row.name.trim()) errors.push('Enter a component name.')
  if (!unchanged(['code'])) {
    if (!/^[A-Z][A-Z0-9_]*$/i.test(row.code)) errors.push('Code must start with a letter and contain only letters, numbers or underscores.')
    if (formulaValues.includes(row.code.toUpperCase()) || ['MIN', 'MAX', 'ROUND', 'ROUNDUP', 'ROUNDDOWN', 'SUM'].includes(row.code.toUpperCase()) || /_(MONTHLY|EARNED)$/i.test(row.code)) errors.push('This code is reserved for calculation values. Choose another code.')
    if (components.some(item => item.id !== row.id && item.code.toUpperCase() === row.code.toUpperCase())) errors.push('Another component already uses this code.')
  }
  if (!unchanged(['calculationType', 'value', 'formula', 'baseComponent', 'code'])) {
    if (['Fixed Amount', 'Daily Rate'].includes(kind) && (!row.value.trim() || !Number.isFinite(Number(row.value)) || Number(row.value) < 0)) errors.push('Enter a valid non-negative amount or rate.')
    if (kind === 'Daily Rate' && (row.category !== 'Earning' || !/^(?:\d+(?:\.\d*)?|\.\d+)$/.test(row.value.trim()) || Number(row.value) <= 0)) errors.push('Daily rate is available for earnings and must be a positive number, such as 700 or 700.50.')
    if (kind === 'Formula') {
      const source = displayFormula(row)
      const tokens = source.toUpperCase().match(/\b[A-Z_][A-Z0-9_]*\b/g) || []
      const self = row.code.toUpperCase()
      if (tokens.some(token => [self, `${self}_MONTHLY`, `${self}_EARNED`].includes(token))) errors.push('A formula cannot refer to itself.')
      const values: Record<string, number> = Object.fromEntries(formulaValues.map(token => [token, 30]))
      for (const item of components.filter(item => item.active && item.id !== row.id))
        for (const token of [item.code, `${item.code}_MONTHLY`, `${item.code}_EARNED`]) values[token.toUpperCase()] = 1000
      try { evaluatePayrollFormula(source, values) } catch (error) { errors.push(error instanceof Error ? error.message : 'Invalid formula.') }
    }
    if (kind === 'Slab Based') {
      const slabs = (row.formula || row.value).split(';').filter(Boolean)
      let previousTo = -1
      if (!slabs.length) errors.push('Add at least one slab.')
      for (const slab of slabs) {
        const match = slab.trim().match(/^(\d+(?:\.\d+)?)(?:-(\d+(?:\.\d+)?)|(\+))\s*:\s*(\d+(?:\.\d+)?)$/)
        if (!match) { errors.push('A slab is invalid. Use numeric ranges and amounts.'); break }
        const from = Number(match[1]), to = match[3] ? Infinity : Number(match[2])
        if (to < from || from <= previousTo) { errors.push('Slabs must be ordered and must not overlap.'); break }
        previousTo = to
      }
    }
    if (kind === 'Residual / Balancing' && !['GROSS', 'CTC'].includes(row.baseComponent || 'GROSS') && !components.some(item => item.active && item.id !== row.id && item.code === row.baseComponent)) errors.push('Select a valid balance target.')
  }
  if (!unchanged(['componentRole', 'statutoryType'])) {
    const allowed = row.category === 'Benefit' ? employerStatutoryTypes : employeeStatutoryTypes
    if (['Statutory Deduction', 'Employer Contribution'].includes(row.componentRole) && !allowed.includes(row.statutoryType)) errors.push('Select the statutory type matching this category.')
  }
  return [...new Set(errors)]
}

export function componentChangeImpact(original: Component | undefined, next: Component) {
  if (!original) return false
  return (['category', 'componentRole', 'statutoryType', 'calculationType', 'value', 'formula', 'baseComponent', 'taxable', 'proRata', 'active', 'priority', 'payType'] as const).some(key => original[key] !== next[key])
}
