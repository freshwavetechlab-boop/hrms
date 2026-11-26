import { inspectPayrollFormula } from './payrollFormula'

/** Structural validation only: absence of an employee's values is not syntax failure. */
export function validateSalaryTemplateFormula(text: string, knownTokens?: Set<string>): string[] {
  if (!text.trim()) return ['Formula or value is blank.']
  try {
    const variables = inspectPayrollFormula(text).variables
    if (!knownTokens) return []
    const known = new Set([...knownTokens].map(token => token.toUpperCase()))
    const missing = variables.filter(token => !known.has(token))
    return missing.length ? [`Unknown value: ${[...new Set(missing)].join(', ')}.`] : []
  } catch (error) { return [error instanceof Error ? error.message : 'Invalid salary formula.'] }
}

// IF(left) >= threshold, yes, no. Keep the original left operand grouped and
// count only the outer arguments so commas in nested functions are preserved.
function repairEarlyIfClose(text: string): string | undefined {
  const opening = /^\s*=?\s*IF\s*\(/i.exec(text)
  if (!opening) return
  let depth = 1
  let end = opening[0].length
  for (; end < text.length && depth > 0; end++) {
    if (text[end] === '(') depth++
    else if (text[end] === ')') depth--
    else if (text[end] === ',' && depth === 1) return
  }
  const tail = text.slice(end)
  if (depth !== 0 || !/^\s*(?:[<>]=?|=)/.test(tail)) return
  let separators = 0
  for (const ch of tail) {
    if (ch === '(') depth++
    else if (ch === ')') depth--
    else if (ch === ',' && depth === 0) separators++
    if (depth < 0) return
  }
  if (depth !== 0 || separators !== 2) return
  return opening[0] + '(' + text.slice(opening[0].length) + ')'
}

type FormulaHint = { message: string; formula?: string }

function bracketHint(text: string): FormulaHint | undefined {
  const open: number[] = []
  for (let i = 0; i < text.length; i++) {
    if (text[i] === '(') open.push(i)
    if (text[i] !== ')') continue
    if (open.length) { open.pop(); continue }
    const message = `Closing bracket at position ${i + 1} has no matching opening bracket.`
    const before = text.slice(0, i)
    // Between operands, removing ')' could change which amounts are combined.
    // Offer a deletion only for a trailing typo in a simple value or call.
    if (!text.slice(i + 1).trim() && /^[\w\s.,()%]+$/.test(before.trimStart().replace(/^=/, '')))
      return { message, formula: before.trimEnd() }
    return { message }
  }
  if (!open.length) return
  const last = open[open.length - 1]
  const message = open.length === 1
    ? `Opening bracket at position ${last + 1} needs a closing bracket.`
    : `${open.length} opening brackets need closing brackets. The innermost is at position ${last + 1}.`
  if (open.length > 1) return { message }
  if (/\bIF\s*$/i.test(text.slice(0, last))) {
    let depth = 0, separators = 0
    for (const ch of text.slice(last + 1)) {
      if (ch === '(') depth++
      else if (ch === ')') depth--
      else if (ch === ',' && depth === 0) separators++
    }
    if (separators !== 2) return { message }
  }

  const candidate = text.trimEnd() + ')'
  if (validateSalaryTemplateFormula(candidate).length) return { message }
  // Another legal close before an operator or comma may produce a different
  // payroll result. For example, MIN(BASIC, 25000 * 0.5% has two readings.
  const boundaries = [...text.slice(last + 1).matchAll(/[+*/%<>=,-]/g)]
  if (boundaries.length > 32) return { message }
  for (const boundary of boundaries) {
    const i = last + 1 + boundary.index
    if (!validateSalaryTemplateFormula(text.slice(0, i) + ')' + text.slice(i)).length) return { message }
  }
  return { message, formula: candidate }
}

export function suggestSalaryTemplateFormula(text: string, knownTokens?: Set<string>): FormulaHint | undefined {
  if (!text.trim() || text.length > 4096) return
  const [error] = validateSalaryTemplateFormula(text, knownTokens)
  if (!error) return
  if (error.startsWith('Unknown value:')) return { message: error + ' Choose a value from this template’s token suggestions.' }

  let candidate = text
  const hints: string[] = []
  if (candidate.includes('\\*')) {
    candidate = candidate.replaceAll('\\*', '*')
    hints.push('Use * for multiplication and remove the backslash.')
  }
  if (candidate.includes(';') && !candidate.includes(',')) {
    candidate = candidate.replaceAll(';', ',')
    hints.push('Separate function arguments with commas.')
  }
  const equality = candidate.replace(/([A-Z0-9_)%])(\s*)==(?!=)/gi, '$1$2=')
  if (equality !== candidate) {
    candidate = equality
    hints.push('Use a single = to compare two values.')
  }
  const repairedIf = repairEarlyIfClose(candidate)
  if (repairedIf) {
    candidate = repairedIf
    hints.unshift('IF closes before the comparison. Keep the condition and both amounts inside IF(...).')
  }
  const bracket = bracketHint(candidate)
  if (bracket) {
    // Positions refer to the original input, including pasted separators.
    if (candidate !== text) {
      const original = bracketHint(text)
      if (original) hints.push(original.message)
    } else hints.push(bracket.message)
    if (bracket.formula) candidate = bracket.formula
  }
  if (knownTokens && hints.length && !validateSalaryTemplateFormula(candidate, knownTokens).length)
    return { message: hints.join(' '), formula: candidate }
  if (hints.length) return { message: hints.join(' ') }

  if (/argument count: IF\.$/i.test(error))
    return { message: 'Write IF(condition, amount when true, amount when false). Put the closing bracket after the last amount.' }
  if (error.startsWith('Unsupported native payroll function/argument count'))
    return { message: error + ' Check the function name and number of arguments. For example, use ROUND(amount, digits) to round a value.' }
  if (error.includes('Missing payroll formula delimiter'))
    return { message: 'Check the brackets around each calculation and use commas between function arguments.' }
  if (error.startsWith('Unsupported formula comparison'))
    return { message: 'Compare values using =, <>, <, <=, > or >=.' }
  if (error.startsWith('Expected a payroll value'))
    return { message: error + ' Enter an amount or choose a component token at that position.' }
  return { message: error }
}
