import type { Component } from '../types/payroll'

/** Add reviewed draft masters without changing any saved shared component. */
export function mergeSalaryTemplateDraftComponents(saved: Component[], proposed: Component[] = []): Component[] {
  const ids = new Set(saved.map(component => String(component.id)))
  const codes = new Set(saved.map(component => component.code.trim().toUpperCase()))
  for (const component of proposed) {
    const code = component.code.trim().toUpperCase()
    if (!Number.isSafeInteger(component.id) || component.id <= 0 || !code || !component.name.trim()) throw Error('An extracted component is missing its ID, code or name. Extract the workbook again.')
    if (ids.has(String(component.id)) || codes.has(code)) throw Error(`Component ${component.code} already exists. Refresh the component list and extract again; saved shared components are not overwritten.`)
    ids.add(String(component.id)); codes.add(code)
  }
  return proposed.length ? [...saved, ...proposed] : saved
}
