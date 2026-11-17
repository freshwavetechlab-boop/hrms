export const roundPayroll = (value: number, digits = 2, awayFromZero = false) => {
  const factor = 10 ** digits
  const scaled = Math.abs(value) * factor
  const whole = Math.floor(scaled)
  const midpoint = Math.abs(scaled - whole - 0.5) < 1e-9
  const rounded = midpoint ? whole + (awayFromZero || whole % 2 !== 0 ? 1 : 0) : Math.round(scaled)
  return Math.sign(value) * rounded / factor
}

export type PayrollFormulaErrorKind = 'syntax' | 'unresolved' | 'arithmetic' | 'limit' | 'result-type'
export class PayrollFormulaError extends Error {
  readonly kind: PayrollFormulaErrorKind
  constructor(message: string, kind: PayrollFormulaErrorKind) { super(message); this.name = 'PayrollFormulaError'; this.kind = kind }
}
export const payrollFormulaErrorKind = (error: unknown): PayrollFormulaErrorKind | undefined => error instanceof PayrollFormulaError ? error.kind : undefined

type FormulaValue = { number: number; boolean?: boolean }
type FormulaNode = () => FormulaValue

// Bounded parity with PayrollFormulaEvaluator. AST construction does not resolve
// inputs: IF is lazy, while IFERROR catches arithmetic failures only. Unknown
// payroll inputs and syntax/limits must never become an apparent zero salary.
function parsePayrollFormula(source: string, resolve: (token: string) => number) {
  if (!source.trim() || source.length > 4096) throw new PayrollFormulaError('Payroll formula is empty or exceeds 4096 characters.', 'limit')
  const text = source.toUpperCase().replace(/\u00d7/g, '*').replace(/\u00f7/g, '/')
    .replace(/SUM\s*\(\s*(FIXED\s+)?EARNINGS(_BEFORE_THIS)?\s*\)/g, (_, fixed) => fixed ? 'FIXED_EARNINGS_BEFORE_THIS' : 'EARNINGS_BEFORE_THIS')
    .replace(/(\d+(?:\.\d+)?)\s*%\s*OF\s*([A-Z_][A-Z0-9_]*)/g, '($2*$1%)')
  let index = 0, depth = 0, nodes = 0
  const variables = new Set<string>()
  const syntax = (message: string): never => { throw new PayrollFormulaError(message, 'syntax') }
  const skip = () => { while (index < text.length && /\s/.test(text[index])) index++ }
  const match = (token: string) => { skip(); if (text[index] !== token) return false; index++; return true }
  const require = (token: string) => { if (!match(token)) syntax(`Missing payroll formula delimiter: ${token}`) }
  const arithmetic = (calculate: () => number): FormulaNode => () => {
    const number = calculate()
    if (!Number.isFinite(number) || Math.abs(number) > 7.922816251426434e28) throw new PayrollFormulaError('Formula arithmetic exceeds decimal limits (#NUM!).', 'arithmetic')
    return { number }
  }
  const functionNode = (name: string, args: FormulaNode[]): FormulaNode => {
    if (name === 'IF' && [2, 3].includes(args.length)) return () => args[0]().number !== 0 ? args[1]() : args.length === 3 ? args[2]() : { number: 0, boolean: true }
    if (name === 'IFERROR' && args.length === 2) return () => { try { return args[0]() } catch (error) { if (payrollFormulaErrorKind(error) !== 'arithmetic') throw error; return args[1]() } }
    if (['AND', 'OR'].includes(name) && args.length > 0) return () => {
      const values = args.map(arg => arg().number !== 0)
      return { number: (name === 'AND' ? values.every(Boolean) : values.some(Boolean)) ? 1 : 0, boolean: true }
    }
    if (['SUM', 'MIN', 'MAX'].includes(name) && args.length > 0) return arithmetic(() => {
      const values = args.map(arg => arg().number)
      return name === 'SUM' ? values.reduce((sum, value) => sum + value, 0) : name === 'MIN' ? Math.min(...values) : Math.max(...values)
    })
    if (name === 'ABS' && args.length === 1) return arithmetic(() => Math.abs(args[0]().number))
    if (['ROUND', 'ROUNDUP', 'ROUNDDOWN'].includes(name) && [1, 2].includes(args.length)) return arithmetic(() => {
      const digits = args.length === 2 ? args[1]().number : 0
      if (!Number.isInteger(digits) || digits < -6 || digits > 8) throw new PayrollFormulaError('Rounding precision must be an integer between -6 and 8 (#NUM!).', 'arithmetic')
      const value = args[0]().number
      if (name === 'ROUND') return roundPayroll(value, digits, true)
      const scale = 10 ** digits, scaled = value * scale
      return (name === 'ROUNDDOWN' ? Math.trunc(scaled) : Math.sign(scaled) * Math.ceil(Math.abs(scaled))) / scale
    })
    return syntax(`Unsupported native payroll function/argument count: ${name}.`)
  }
  const atom = (): FormulaNode => {
    if (++depth > 64 || ++nodes > 512) throw new PayrollFormulaError('Payroll formula depth/node limit exceeded.', 'limit')
    try {
      let node: FormulaNode
      if (match('+')) node = atom()
      else if (match('-')) { const inner = atom(); node = arithmetic(() => -inner().number) }
      else if (match('(')) { node = compare(); require(')') }
      else {
        skip()
        const numeric = text.slice(index).match(/^(?:\d+(?:\.\d*)?|\.\d+)(?:E[+-]?\d+)?/)
        if (numeric) {
          index += numeric[0].length
          const number = Number(numeric[0])
          if (!Number.isFinite(number) || Math.abs(number) > 7.922816251426434e28) syntax('Invalid payroll formula number.')
          node = () => ({ number })
        } else {
          const name = text.slice(index).match(/^[A-Z_][A-Z0-9_]*/)?.[0]
          if (!name) return syntax(`Expected a payroll value at position ${index + 1}.`)
          index += name.length
          if (match('(')) {
            const args: FormulaNode[] = []
            if (!match(')')) {
              do { if (args.length >= 256) throw new PayrollFormulaError('Too many formula arguments.', 'limit'); args.push(compare()) } while (match(','))
              require(')')
            }
            node = functionNode(name, args)
          } else if (name === 'TRUE' || name === 'FALSE') node = () => ({ number: name === 'TRUE' ? 1 : 0, boolean: true })
          else {
            variables.add(name)
            node = () => {
              const number = resolve(name)
              if (!Number.isFinite(number)) throw new PayrollFormulaError(`Unknown or unresolved value: ${name}.`, 'unresolved')
              return { number }
            }
          }
        }
      }
      while (match('%')) { const inner = node; node = arithmetic(() => inner().number / 100) }
      return node
    } finally { depth-- }
  }
  const product = (): FormulaNode => {
    let node = atom()
    for (;;) {
      skip(); const op = text[index]
      if (!['*', '/'].includes(op)) return node
      index++; const left = node, right = atom()
      node = arithmetic(() => { const a = left().number, b = right().number; if (op === '/' && b === 0) throw new PayrollFormulaError('A formula denominator is zero (#DIV/0!).', 'arithmetic'); return op === '*' ? a * b : a / b })
    }
  }
  const sum = (): FormulaNode => {
    let node = product()
    for (;;) {
      skip(); const op = text[index]
      if (!['+', '-'].includes(op)) return node
      index++; const left = node, right = product()
      node = arithmetic(() => op === '+' ? left().number + right().number : left().number - right().number)
    }
  }
  const compare = (): FormulaNode => {
    const left = sum(); skip(); const start = index
    while (index < text.length && ['<', '>', '='].includes(text[index])) index++
    if (start === index) return left
    const op = text.slice(start, index)
    if (!['=', '<>', '<', '>', '<=', '>='].includes(op)) syntax('Unsupported formula comparison.')
    const right = sum()
    return () => {
      const a = left(), b = right()
      const cmp = !!a.boolean !== !!b.boolean ? Number(!!a.boolean) - Number(!!b.boolean) : a.number - b.number
      return { number: (op === '=' ? cmp === 0 : op === '<>' ? cmp !== 0 : op === '<' ? cmp < 0 : op === '>' ? cmp > 0 : op === '<=' ? cmp <= 0 : cmp >= 0) ? 1 : 0, boolean: true }
    }
  }
  match('=')
  const node = compare()
  skip()
  if (index !== text.length) syntax(`Unsupported payroll formula syntax at position ${index + 1}.`)
  return { node, variables: [...variables] }
}

/** Validate grammar and dependencies without executing any formula or resolving inputs. */
export function inspectPayrollFormula(source: string): { variables: string[] } {
  return { variables: parsePayrollFormula(source, () => Number.NaN).variables }
}

export function evaluatePayrollFormula(source: string, values: Record<string, number>): number {
  const result = parsePayrollFormula(source, token => values[token]).node()
  if (result.boolean) throw new PayrollFormulaError('A salary output must be numeric; select numeric IF branches.', 'result-type')
  return result.number
}
