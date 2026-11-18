import type { Component, Structure } from '../types/payroll'
import { inspectPayrollFormula } from './payrollFormula'

export type SalaryTemplateReadiness = { status: 'Draft' | 'Configured' | 'Inactive'; reason: string }

/** Display-only configuration check. Employee inputs and payroll approval are not template status. */
export function salaryTemplateReadiness(template: Structure, components: Component[]): SalaryTemplateReadiness {
  if (!template.active) return { status: 'Inactive', reason: 'This template is inactive.' }
  if (!template.name.trim()) return { status: 'Draft', reason: 'Add a template name.' }
  if (!template.lines.length) return { status: 'Draft', reason: 'Add salary components and configure their rules.' }
  const masters = new Map(components.map(component => [String(component.id), component]))
  const seen = new Set<string>()
  for (const line of template.lines) {
    const id = String(line.componentId)
    const component = masters.get(id)
    if (!component || !component.active) return { status: 'Draft', reason: 'Review missing or inactive salary components.' }
    if (seen.has(id)) return { status: 'Draft', reason: `Remove the duplicate ${component.name || component.code} component.` }
    seen.add(id)
    const type = line.calculationType || component.calculationType
    const source = (line.formula || line.value || component.formula || component.value || '').trim()
    const requiresFormula = ['Formula', 'Percentage of CTC', 'Percentage of Component'].includes(type)
    if (requiresFormula && !source) return { status: 'Draft', reason: `Configure the ${component.name || component.code} formula.` }
    // Fixed/manual amounts and daily rates may be supplied by an employee's
    // effective salary assignment. They are not fabricated or evaluated here.
    // Slab configuration has its own native grammar, rather than Excel syntax.
    const expression = requiresFormula || !['Slab Based', 'Manual Entry', 'Manual Override', 'Manual / Variable'].includes(type) && /[A-Z_+*/()%]/i.test(source)
    if (source && expression) {
      try { inspectPayrollFormula(source) }
      catch { return { status: 'Draft', reason: `Review the ${component.name || component.code} formula syntax.` } }
    }
  }
  return { status: 'Configured', reason: 'Components and formula structure are configured. Employee inputs are checked during simulation.' }
}
