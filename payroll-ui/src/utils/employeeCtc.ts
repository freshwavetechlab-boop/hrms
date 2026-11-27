const maximumAnnualPaise = 999999999999999999n

export function parseSalaryUnits(text: string, scale: number, maximum: bigint, allowGrouping = false): bigint | null {
  let value = text.trim()
  if (allowGrouping) {
    if (/^[^+-]+[+-]$/.test(value)) value = value.slice(-1) + value.slice(0, -1).trimEnd()
    value = value.replace(/^([+-]?\d[\d,]*)/, whole => whole.replaceAll(',', ''))
  }
  const match = /^([+-]?)(\d*)(?:\.(\d*))?$/.exec(value)
  if (!match || (!match[2] && !match[3])) return null
  const whole = match[2].replace(/^0+/, '') || '0'
  const fraction = (match[3] ?? '').replace(/0+$/, '')
  if (whole.length > maximum.toString().length - scale || fraction.length > scale) return null
  const units = BigInt(whole + fraction.padEnd(scale, '0'))
  return units <= maximum && (match[1] !== '-' || units === 0n) ? units : null
}

function displayPaise(paise: bigint) {
  const cents = (paise % 100n).toString().padStart(2, '0').replace(/0+$/, '')
  return (paise / 100n).toString() + (cents && '.' + cents)
}

export function resolveEmployeeCtc(annual: string, monthly: string, allowGrouping = true) {
  const issues: { column: 'Annual CTC' | 'Monthly CTC'; message: string }[] = []
  const hasAnnual = !!annual.trim(), hasMonthly = !!monthly.trim()
  const annualPaise = hasAnnual ? parseSalaryUnits(annual, 2, maximumAnnualPaise, allowGrouping) : null
  const monthlyPaise = hasMonthly ? parseSalaryUnits(monthly, 2, maximumAnnualPaise, allowGrouping) : null
  const invalidAmount = 'Enter a non-negative amount with up to 16 integer digits and two decimal places.'
  if (hasAnnual && annualPaise === null) issues.push({ column: 'Annual CTC', message: invalidAmount })
  if (hasMonthly && monthlyPaise === null) issues.push({ column: 'Monthly CTC', message: invalidAmount })
  if (!issues.length) {
    if (annualPaise !== null) {
      const expectedMonthly = (annualPaise + 6n) / 12n
      if (!hasMonthly) monthly = displayPaise(expectedMonthly)
      else if (monthlyPaise !== expectedMonthly) issues.push({ column: 'Monthly CTC', message: `Annual CTC divided by 12 gives Monthly CTC ${displayPaise(expectedMonthly)}. Check both amounts.` })
    } else if (monthlyPaise !== null) {
      const expectedAnnual = monthlyPaise * 12n
      if (expectedAnnual > maximumAnnualPaise) issues.push({ column: 'Monthly CTC', message: 'Monthly CTC multiplied by 12 exceeds the maximum Annual CTC of 9999999999999999.99.' })
      else annual = displayPaise(expectedAnnual)
    }
  }
  return { annual: annual.trim() ? annual : '', monthly: monthly.trim() ? monthly : '', issues }
}
