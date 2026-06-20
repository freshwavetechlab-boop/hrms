import type { Component, Structure } from '../types/payroll'

export const money = (value: number | undefined) => `Rs ${Math.round(value || 0).toLocaleString('en-IN')}`
export const percent = (value: number | undefined) => value === undefined || value === null ? '-' : `${value > 0 ? '+' : ''}${value}%`

export function calculateSalaryJson(ctc: number, components: Component[], salaryStructure?: Structure) {
  const monthlyCtc = ctc / 12
  const values: Record<string, string> = {}
  const ordered = components.filter(component => salaryStructure?.lines.some(line => line.componentId === String(component.id))).sort((a, b) => Number(a.priority) - Number(b.priority))
  for (const component of ordered) {
    const byCode = Object.fromEntries(components.map(item => [item.code, Number(values[item.id] || 0)]))
    const line = salaryStructure?.lines.find(item => item.componentId === String(component.id))?.value || component.formula || component.value
    const formula = line.toUpperCase().replace(/(\d+(?:\.\d+)?)\s*%\s*OF\s*(CTC|BASIC)/g, '$2*$1/100').replace(/(CTC|BASIC)\s*\*\s*(\d+(?:\.\d+)?)\s*%/g, '$1*$2/100').replace(/(\d+(?:\.\d+)?)%/g, '$1/100').replace(/\s+/g, '').replace(/MIN\(/g, 'Math.min(').replace(/CTC/g, String(monthlyCtc)).replace(/BASIC/g, String(byCode.BASIC || 0)).replace(/[^0-9+\-*/().,Mathmin]/g, '')
    if (component.calculationType === 'Percentage of CTC') values[component.id] = String(Math.round(monthlyCtc * Number(component.value || 0) / 100))
    else if (component.calculationType === 'Formula') {
      try { values[component.id] = String(Math.round(Number(Function(`"use strict";return (${formula})`)()) || 0)) } catch { values[component.id] = '0' }
    } else if (component.calculationType === 'Balancing Amount') {
      const used = Object.entries(values).filter(([id]) => components.find(item => String(item.id) === id)?.category === 'Earning').reduce((sum, [, value]) => sum + Number(value), 0)
      values[component.id] = String(Math.max(0, Math.round(monthlyCtc - used)))
    } else values[component.id] = String(Number(component.value || 0))
  }
  return JSON.stringify(values)
}
