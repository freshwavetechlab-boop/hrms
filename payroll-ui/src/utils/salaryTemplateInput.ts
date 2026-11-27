export const firstSalaryRuleText = (...values: (string | undefined)[]) => values.find(value => !!value?.trim())?.trim() || ''

/** Match the payroll engine's calculation aliases while no native result is available. */
export function salaryCalculationKind(value: string) {
  if (value === 'Daily Rate') return 'Daily Rate'
  const kind = value.trim().toLowerCase()
  if (['percentage of ctc', 'percentage of component', 'formula'].includes(kind)) return 'Formula'
  if (['balancing amount', 'residual / balancing'].includes(kind)) return 'Residual / Balancing'
  if (['manual entry', 'manual override', 'manual / variable'].includes(kind)) return 'Manual / Variable'
  return kind === 'slab based' ? 'Slab Based' : 'Fixed Amount'
}

export function salaryInputMethodIsEditable(method: string, configuredType: string, numericConstant: boolean) {
  if (['Fixed Amount', 'Daily Rate', 'Manual / Variable'].includes(method)) return true
  return method === 'Formula' && numericConstant
    && !['percentage of ctc', 'percentage of component'].includes(configuredType.toLowerCase())
}
