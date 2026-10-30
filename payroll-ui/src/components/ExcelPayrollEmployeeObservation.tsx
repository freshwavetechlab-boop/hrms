import type { PayrollSetupVerification } from '../services/excelPayrollSetupService'
import { payrollComparisonMoney as money, payrollRawAmount as raw } from '../utils/payrollRoundingEvidence'

export default function ExcelPayrollEmployeeObservation({ employeeId, observation }: { employeeId: number; observation?: PayrollSetupVerification }) {
  const data = observation?.data
  if (!observation?.ok || !data) return <p role="status">Saved Employee calculation unavailable: {observation?.error || 'No unique calculation observation.'}</p>
  const attendance = data.attendanceEvidence
  return <section aria-label={`Current saved Employee calculation ${employeeId}`}>
    <p>Calculation mode {data.mode}; complete {data.complete ? 'Yes' : 'No'}. Read-only current observation; saved source and receipt remain unchanged.</p>
    <p>Gross {money(data.gross)}; deductions {money(data.deductions)}; employer {money(data.benefits)}; net {money(data.net)}. Raw net {raw(data.net)}.</p>
    {attendance ? <p>Attendance source {attendance.sourceKind}; period {attendance.payPeriod}; cycle {attendance.cycleStart || 'not recorded'} to {attendance.cycleEnd || 'not recorded'}; temporary day overrides {attendance.temporaryDayOverrides ? 'Yes' : 'No'}. {attendance.explanation} {attendance.policyValidationError}</p> : <p>Attendance provenance not supplied; no dated cycle is inferred.</p>}
    {data.inputs.filter(input => ['PAYROLL_DAYS', 'PRESENT_DAYS', 'PAYABLE_DAYS'].includes(input.name)).map(input => <p key={input.name}>{input.name}: {raw(input.value)} — {input.source}</p>)}
    {data.lines.map((line, index) => <p key={`${line.code}:${index}`}>{line.code} / {line.category}: {money(line.earned)}; raw {raw(line.raw)}; formula {line.formula || 'not supplied'}; rounding {line.rounding || 'not supplied'}; pro-rata {line.proRata ? 'Yes' : 'No'}.</p>)}
  </section>
}
