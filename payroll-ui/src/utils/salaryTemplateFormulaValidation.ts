import { inspectPayrollFormula } from './payrollFormula'

/** Structural validation only: absence of an employee's values is not syntax failure. */
export function validateSalaryTemplateFormula(text: string, knownTokens: Set<string>): string[] {
  if (!text.trim()) return ['Formula or value is blank.']
  try {
    const known = new Set([...knownTokens].map(token => token.toUpperCase()))
    const missing = inspectPayrollFormula(text).variables.filter(token => !known.has(token))
    return missing.length ? [`Unknown value: ${[...new Set(missing)].join(', ')}.`] : []
  } catch (error) { return [error instanceof Error ? error.message : 'Invalid salary formula.'] }
}
