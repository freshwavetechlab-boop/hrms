import type { PayrollValue } from '../services/payrollCalculationService'

export type SalaryFormulaSuggestion = PayrollValue & { label: string }
export type SalaryFormulaSuggestionScope = {
  /** Active component IDs included in the current template. */
  includedComponentIds: readonly string[]
  excludeComponentId?: string
  limit?: number
}
export type SalaryFormulaTokenContext = { start: number; end: number; query: string }

const identifier = /^[A-Z_][A-Z0-9_]*$/
const normalize = (value: string) => value.trim().toUpperCase()
const unavailable = (value: PayrollValue) => /^(missing from|inactive|unavailable|not available)\b/i.test(value.availability.trim())

/** Suggestions never add a component or translate a display name into a code. */
export function salaryFormulaSuggestions(values: readonly PayrollValue[], query: string, scope: SalaryFormulaSuggestionScope): SalaryFormulaSuggestion[] {
  const included = new Set(scope.includedComponentIds.map(String))
  const excluded = scope.excludeComponentId == null ? undefined : String(scope.excludeComponentId)
  const groups = new Map<string, PayrollValue[]>()
  for (const value of values) {
    const token = normalize(value.token)
    const componentId = value.componentId == null || value.componentId === '' ? undefined : String(value.componentId)
    if (!identifier.test(token) || unavailable(value) || componentId != null && (!included.has(componentId) || componentId === excluded)) continue
    const group = groups.get(token) ?? []
    group.push(value)
    groups.set(token, group)
  }
  const search = normalize(query)
  const result: { value: SalaryFormulaSuggestion; rank: number; index: number }[] = []
  for (const [token, group] of groups) {
    // System tokens keep their defined meaning. Distinct component IDs sharing
    // one code are ambiguous, so do not choose an employee salary source for HR.
    const system = group.find(value => value.componentId == null || value.componentId === '')
    if (!system && new Set(group.map(value => String(value.componentId))).size > 1) continue
    const value = system ?? group[0]
    const meaning = normalize(value.meaning)
    if (search && !token.includes(search) && !meaning.includes(search)) continue
    const rank = !search || token === search ? 0 : token.startsWith(search) ? 1 : token.includes(search) ? 2 : 3
    result.push({ value: { ...value, token, label: `${token} — ${value.meaning}` }, rank, index: result.length })
  }
  const limit = scope.limit == null ? 20 : Math.max(0, Math.trunc(scope.limit))
  return result.sort((a, b) => a.rank - b.rank || a.index - b.index).slice(0, limit).map(row => row.value)
}

function selection(value: string, start?: number | null, end?: number | null) {
  const clamp = (offset: number | null | undefined, fallback: number) => typeof offset === 'number' && Number.isFinite(offset) ? Math.max(0, Math.min(value.length, Math.trunc(offset))) : fallback
  const first = clamp(start, value.length), last = clamp(end, first)
  return { start: Math.min(first, last), end: Math.max(first, last) }
}

/** Resolve the complete identifier being edited, including a word after the caret. */
export function getSalaryFormulaTokenContext(value: string, selectionStart?: number | null, selectionEnd?: number | null): SalaryFormulaTokenContext | null {
  const selected = selection(value, selectionStart, selectionEnd)
  for (const match of value.matchAll(/[A-Z_][A-Z0-9_]*/gi)) {
    const start = match.index, end = start + match[0].length
    // A numeric exponent such as 1E3 is not a payroll token.
    if (start > 0 && /[A-Z0-9_.]/i.test(value[start - 1])) continue
    if (selected.start < start || selected.end > end || selected.start > end) continue
    return { start, end, query: selected.start === selected.end ? value.slice(start, selected.start) : value.slice(selected.start, selected.end) }
  }
  return null
}

/** Replace the current identifier, or insert at the actual selection/caret. */
export function replaceSalaryFormulaToken(value: string, token: string, selectionStart?: number | null, selectionEnd?: number | null): { value: string; caret: number } {
  const code = normalize(token)
  if (!identifier.test(code)) throw new Error('Select a valid payroll token code.')
  const bounds = getSalaryFormulaTokenContext(value, selectionStart, selectionEnd) ?? selection(value, selectionStart, selectionEnd)
  return { value: value.slice(0, bounds.start) + code + value.slice(bounds.end), caret: bounds.start + code.length }
}
