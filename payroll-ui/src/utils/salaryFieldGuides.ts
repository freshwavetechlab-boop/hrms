export type SalaryHelpOption = { label: string; meaning: string; example: string }

export const calculationHelp: SalaryHelpOption[] = [
  { label: 'Monthly amount', meaning: 'A full-month amount. Enable attendance adjustment if unpaid days should reduce it.', example: '18,000 with 26 payable days out of 30 becomes 15,600 when adjustment is Yes.' },
  { label: 'Daily rate x payable days', meaning: 'A rate for one payable day, multiplied once by attendance payable days. For a new earning component only.', example: '700 x 22.5 payable days = 15,750. No division by 30.' },
  { label: 'Formula', meaning: 'Calculate from another value. CODE means the full-month value; CODE_EARNED means the value after attendance.', example: 'WAGES_EARNED * 5% gives 900 on earned wages of 18,000. Normally do not adjust this again.' },
  { label: 'Payroll input / adjustment', meaning: 'The template calculates zero automatically. An approved input for the employee and period supplies the payment or deduction.', example: 'An approved September incentive of 2,500 is not automatically repeated in October.' },
  { label: 'Remaining balance', meaning: 'The target minus earnings already calculated. Order matters. This does not guarantee net take-home pay.', example: 'Target 25,000 minus earlier earnings 21,000 leaves 4,000.' },
  { label: 'Slab-based', meaning: 'Choose one fixed amount from the range containing gross earned so far. This is not progressive income tax.', example: 'Illustration: 0-15,000 gives 0; 15,000.01 and above gives 200. Gross of 18,000 gives 200.' }
]
export const attendanceHelp: SalaryHelpOption[] = [
  { label: 'Yes: result is a full-month amount', meaning: 'Multiply the result by payable days divided by payroll days.', example: '18,000 x 26 / 30 = 15,600.' },
  { label: 'No: use the calculated amount', meaning: 'Pay or deduct the result without another attendance adjustment.', example: 'WAGES_EARNED * 5% already accounts for attendance; applying it again would reduce it twice.' }
]
export const payTypeHelp: SalaryHelpOption[] = [
  { label: 'Fixed Pay', meaning: 'Include this earning in the fixed-earnings aggregate. It does not mean attendance cannot reduce the amount.', example: 'Basic 10,000 marked Fixed Pay is included in SUM(FIXED EARNINGS).' },
  { label: 'Variable Pay', meaning: 'Exclude this earning from the fixed-earnings aggregate. It still runs if it is in the template.', example: 'Incentive 2,000 marked Variable Pay is excluded from SUM(FIXED EARNINGS), but included in total earnings.' }
]
const statutoryMeaning: Record<string, [string, string]> = {
  TDS: ['Employee income-tax withholding. The tag does not supply tax slabs or a calculation.', 'Tag the income-tax deduction TDS; configure the applicable tax rules separately.'],
  'Professional Tax': ['Employee professional-tax deduction. Applicability and slabs depend on the configured jurisdiction and period.', 'Keep a professional-tax deduction separate from PF and income tax.'],
  'PF Employee': ['Employee provident-fund share, deducted from take-home pay.', 'Map this deduction to the employee contribution in the applicable PF policy.'],
  VPF: ['Employee voluntary provident-fund contribution, separate from the regular employee PF share.', 'An approved additional employee contribution belongs in VPF, not Employer PF.'],
  'ESI Employee': ['Employee ESI contribution, deducted from take-home pay.', 'Keep the employee share here and the employer share under Employer Benefit.'],
  'LWF Employee': ['Employee labour-welfare-fund contribution.', 'Use the applicable state and deduction period; this tag does not choose either.'],
  'NPS Employee': ['Employee pension contribution to NPS.', 'A contribution deducted from the employee belongs here, not under NPS Employer.'],
  'PF Employer': ['Company provident-fund contribution, not an employee deduction.', 'Show the company cost under Employer Benefit without subtracting it from net pay.'],
  EPS: ['Company pension contribution classified separately from the employer provident-fund share.', 'Map the applicable split in the PF policy; do not duplicate the same employer cost.'],
  'ESI Employer': ['Company ESI contribution, not an employee deduction.', 'Show this as company cost, alongside a separate ESI Employee deduction.'],
  'LWF Employer': ['Company labour-welfare-fund contribution.', 'Record the company share separately from the employee LWF deduction.'],
  'NPS Employer': ['Company contribution to NPS.', 'A company-funded pension contribution belongs under Employer Benefit.'],
  'Workmen Compensation': ['Company cost classified as workmen compensation.', 'Record the applicable insurance or compensation cost as a benefit, not a take-home deduction.']
}
export const statutoryHelp = (values: readonly string[]): SalaryHelpOption[] => values.map(label => ({ label, meaning: statutoryMeaning[label]?.[0] || 'Saved statutory classification.', example: statutoryMeaning[label]?.[1] || 'Review its applicable policy separately.' }))
export const payrollValueHelp: Record<string, [string, string]> = {
  CTC: ['Monthly target derived from annual CTC, not automatically actual gross.', 'Annual CTC 300,000 gives CTC 25,000.'],
  MONTHLY_CTC: ['Same monthly target as CTC.', '300,000 / 12 = 25,000.'],
  ANNUAL_CTC: ['Annual CTC supplied to the calculation.', 'Use 300,000 for a 300,000 annual package, not as monthly pay.'],
  GROSS: ['Legacy alias of the monthly CTC target in this formula engine, not actual earned gross.', 'For actual earned earnings use GROSS_EARNED instead.'],
  GROSS_EARNED: ['Earnings calculated so far after attendance. Calculation order matters.', 'Earlier earned wages 15,000 plus earned allowance 1,000 gives 16,000.'],
  PAYROLL_DAYS: ['Denominator days of the pay period.', 'A configured 30-day period gives 30.'],
  TOTAL_DAYS: ['Alias of PAYROLL_DAYS.', 'TOTAL_DAYS and PAYROLL_DAYS both return 30 in a 30-day period.'],
  WORKING_DAYS: ['Legacy alias of PAYROLL_DAYS, not an independent working-day calendar.', 'Do not assume this excludes weekends: a 30-day payroll period gives 30.'],
  PAYABLE_DAYS: ['Days payable under attendance policy, including payable fractions.', '22 worked days and a paid half-day can give 22.5.'],
  PRESENT_DAYS: ['Recorded present days supplied to the engine.', 'Present days can differ from payable days when paid leave is included.'],
  LOP_DAYS: ['Payroll days minus payable days, with a minimum of zero.', '30 payroll days minus 26 payable days = 4.'],
  EARNINGS_BEFORE_THIS: ['Full-month earning and reimbursement values calculated before this line.', 'Earlier monthly earnings 10,000 and 2,000 give 12,000.'],
  FIXED_EARNINGS_BEFORE_THIS: ['Earlier full-month earnings classified as Fixed Pay.', 'Fixed basic 10,000 is included; Variable Pay incentive 2,000 is not.'],
  FIXED_EARNINGS: ['Alias of FIXED_EARNINGS_BEFORE_THIS.', 'Use for the same earlier fixed-earnings total.']
}
export const valueHelp = (label: string): SalaryHelpOption => ({ label, meaning: payrollValueHelp[label]?.[0] || (label.endsWith('_EARNED') ? 'This component after attendance adjustment.' : 'This component before attendance adjustment; a daily-rate component uses rate x payroll days.'), example: payrollValueHelp[label]?.[1] || (label.endsWith('_EARNED') ? 'Monthly 18,000 at 26/30 payable days gives 15,600.' : 'Monthly 18,000 stays 18,000 here; daily rate 700 at 30 payroll days gives 21,000.') })
export const formulaStyleHelp: SalaryHelpOption[] = [
  { label: 'Use component master default', meaning: 'Reset the line to the component default and inherited treatment.', example: 'Use the shared Wages calculation unchanged for this template.' },
  { label: 'Fixed amount in template', meaning: 'Use this amount only in this template; the master stays unchanged.', example: 'Allowance 1,500 for this client instead of the master default 1,000.' },
  { label: 'Percentage of another component', meaning: 'Multiply the selected monthly or earned base by a percentage.', example: 'WAGES_EARNED x 5% on 18,000 gives 900.' },
  { label: 'Percentage with wage ceiling', meaning: 'Use the lower of the chosen base and ceiling, then multiply by the percentage. This is arithmetic, not statutory certification.', example: 'Illustration: MIN(18,000, 15,000) x 10% = 1,500.' },
  { label: 'Advanced formula', meaning: 'Write an expression using supported values and functions.', example: 'MAX(WAGES_EARNED * 5%, 500) chooses the higher amount.' }
]
