import { Alert, Tag } from 'antd'
import type { ExcelPayslipBatch, ExcelPayslipCalculationSource } from '../services/excelPayslipService'
import type { PayrollSetupRequest } from '../services/excelPayrollSetupService'
import { reviewMissingMonthlyInputs, summarizeMissingMonthlyInputs } from '../utils/excelPayrollSourceInputReview'
import DataTable from './DataTable'

export default function ExcelPayrollSourceInputReview({ batch, source, request }: { batch: ExcelPayslipBatch; source: ExcelPayslipCalculationSource; request: PayrollSetupRequest }) {
  const rows = reviewMissingMonthlyInputs(batch, source, request)
  if (!rows.length) return null
  const groups = summarizeMissingMonthlyInputs(rows)
  return <section aria-label="Recurring monthly input review">
    <Alert type="warning" showIcon message="Salary input mapping needs attention" description="Choose the monthly amount or daily-rate input for the components below. Repeated employee rows are grouped together; these source references are suggestions to inspect, not approved mappings." />
    <DataTable title="Salary inputs to map" rows={groups} getRowId={row => row.key} exportDisabled hideSearch columns={[
      { key: 'component', label: 'Component', width: 150 }, { key: 'employees', label: 'Source rows', width: 100 },
      { key: 'outputColumn', label: 'Earned column', width: 130 }, { key: 'inputColumns', label: 'Input columns to inspect', width: 240, wrap: true },
      { key: 'status', label: 'Next step', width: 225, wrap: true, render: row => <Tag style={{ whiteSpace: 'normal' }} color={row.status === 'Source output missing/error' ? 'red' : 'gold'}>{row.status === 'Source output missing/error' ? 'Fix captured source output' : 'Confirm monthly amount / daily rate'}</Tag> },
    ]} />
    <details aria-label="Monthly input row evidence">
      <summary>View row-level evidence ({rows.length} component rows)</summary>
      <p>Source formulas, missing outputs and rounding evidence remain unchanged. No salary input is inferred from an earned amount or zero attendance. Up to 1,000 evidence rows are shown; the server checks every selected employee.</p>
      <DataTable title="Captured monthly-input evidence" rows={rows} getRowId={row => row.key} exportDisabled columns={[
      { key: 'sourceRow', label: 'Source row', width: 95 }, { key: 'component', label: 'Component', width: 160 },
      { key: 'status', label: 'Source status', width: 200 },
      { key: 'outputCell', label: 'Earned output cell', width: 150 }, { key: 'references', label: 'Captured references to review', width: 260, wrap: true },
      { key: 'sourceRounding', label: 'Formula / rounding', width: 280, wrap: true }, { key: 'action', label: 'Required review', width: 380, wrap: true },
      ]} />
    </details>
  </section>
}
