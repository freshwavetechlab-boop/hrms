import { Alert } from 'antd'
import type { ExcelPayslipBatch, ExcelPayslipCalculationSource } from '../services/excelPayslipService'
import type { PayrollSetupRequest } from '../services/excelPayrollSetupService'
import { reviewMissingMonthlyInputs } from '../utils/excelPayrollSourceInputReview'
import DataTable from './DataTable'

export default function ExcelPayrollSourceInputReview({ batch, source, request }: { batch: ExcelPayslipBatch; source: ExcelPayslipCalculationSource; request: PayrollSetupRequest }) {
  const rows = reviewMissingMonthlyInputs(batch, source, request)
  if (!rows.length) return null
  return <section aria-label="Recurring monthly input review">
    <Alert type="warning" showIcon message="Monthly inputs need explicit review" description="No recurring monthly amount or daily-rate basis is mapped. The references below come from the saved batch; they are review candidates, not inferred salary inputs. No values are copied or changed. Zero attendance does not make a monthly rate zero, and choosing a column does not certify formula parity. At most 1,000 component/row review items are shown; the server still validates the complete selected scope." />
    <DataTable title="Captured monthly-input review" rows={rows} getRowId={row => row.key} exportDisabled columns={[
      { key: 'sourceRow', label: 'Source row', width: 95 }, { key: 'component', label: 'Component', width: 160 },
      { key: 'status', label: 'Source status', width: 200 },
      { key: 'outputCell', label: 'Earned output cell', width: 150 }, { key: 'references', label: 'Captured references to review', width: 260, wrap: true },
      { key: 'sourceRounding', label: 'Formula / rounding', width: 280, wrap: true }, { key: 'action', label: 'Required review', width: 380, wrap: true },
    ]} />
  </section>
}
