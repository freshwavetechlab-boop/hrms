export const roundPayroll = (value: number, digits = 2, awayFromZero = false) => {
  const factor = 10 ** digits
  const scaled = Math.abs(value) * factor
  const whole = Math.floor(scaled)
  const midpoint = Math.abs(scaled - whole - 0.5) < 1e-9
  const rounded = midpoint ? whole + (awayFromZero || whole % 2 !== 0 ? 1 : 0) : Math.round(scaled)
  return Math.sign(value) * rounded / factor
}

// Mirrors the payroll engine's arithmetic grammar. Never remove unknown tokens or evaluate JavaScript.
export function evaluatePayrollFormula(source: string, values: Record<string, number>): number {
  const text = source.toUpperCase().replace(/\u00d7/g, '*').replace(/\u00f7/g, '/')
    .replace(/SUM\s*\(\s*(FIXED\s+)?EARNINGS(_BEFORE_THIS)?\s*\)/g, (_, fixed) => String(values[fixed ? 'FIXED_EARNINGS_BEFORE_THIS' : 'EARNINGS_BEFORE_THIS'] ?? 0))
    .replace(/(\d+(?:\.\d+)?)\s*%\s*OF\s*([A-Z0-9_]+)/g, '$2*$1/100')
    .replace(/(\d+(?:\.\d+)?)\s*%/g, '$1/100')
  let index = 0
  const skip = () => { while (/\s/.test(text[index] ?? '') && index < text.length) index++ }
  const match = (token: string) => { skip(); if (text[index] !== token) return false; index++; return true }
  const factor = (): number => {
    if (match('+')) return factor()
    if (match('-')) return -factor()
    if (match('(')) { const result = expression(); if (!match(')')) throw Error('Missing closing parenthesis.'); return result }
    skip()
    const number = text.slice(index).match(/^(?:\d+(?:\.\d*)?|\.\d+)/)
    if (number) { index += number[0].length; return Number(number[0]) }
    const name = text.slice(index).match(/^[A-Z_][A-Z0-9_]*/)?.[0]
    if (!name) throw Error(`Expected a value at position ${index + 1}.`)
    index += name.length
    if (!match('(')) {
      const value = values[name]
      if (value === undefined || !Number.isFinite(value)) throw Error(`Unknown or unresolved value: ${name}.`)
      return value
    }
    const args: number[] = []
    if (!match(')')) {
      do { args.push(expression()) } while (match(','))
      if (!match(')')) throw Error('Missing closing function parenthesis.')
    }
    if (!args.length) throw Error(`${name} needs at least one argument.`)
    if (['ROUND', 'ROUNDUP', 'ROUNDDOWN'].includes(name) && args.length !== 1) throw Error(`${name} supports one argument.`)
    switch (name) {
      case 'MIN': return Math.min(...args)
      case 'MAX': return Math.max(...args)
      case 'ROUND': return roundPayroll(args[0], 0, true)
      case 'ROUNDUP': return Math.ceil(args[0])
      case 'ROUNDDOWN': return Math.floor(args[0])
      default: throw Error(`Unsupported function: ${name}.`)
    }
  }
  const term = (): number => {
    let result = factor()
    for (;;) {
      if (match('*')) result *= factor()
      else if (match('/')) { const divisor = factor(); if (!divisor) throw Error('A formula denominator is zero.'); result /= divisor }
      else return result
    }
  }
  const expression = (): number => {
    let result = term()
    for (;;) {
      if (match('+')) result += term()
      else if (match('-')) result -= term()
      else return result
    }
  }
  const result = expression()
  skip()
  if (index !== text.length) throw Error(`Unexpected token at position ${index + 1}.`)
  if (!Number.isFinite(result)) throw Error('Formula result is not finite.')
  return result
}
