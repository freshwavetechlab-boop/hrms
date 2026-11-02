import { useState } from 'react'
import { Alert, Select } from 'antd'
import type { ExcelPayslipBatch, ExcelPayslipCalculationSource } from '../services/excelPayslipService'
import type { PayrollSetupRequest } from '../services/excelPayrollSetupService'
import { payrollSourceEvidence } from '../utils/payrollSourceEvidence'
import DataTable from './DataTable'
export default function ExcelPayrollSourceEvidence({ batch, source, request }: { batch: ExcelPayslipBatch; source: ExcelPayslipCalculationSource; request: PayrollSetupRequest }) {
  const [sourceRowId, setSourceRowId] = useState<string>()
  const selected = batch.rows.filter(row => request.rowIds.includes(row.id))
  const inspected = sourceRowId && selected.some(row => row.id === sourceRowId) ? sourceRowId : undefined
  const evidence = payrollSourceEvidence(batch, source, inspected ? { ...request, rowIds: [inspected] } : request)
  return <section aria-label="Saved batch financial source evidence">
    <Alert type="info" showIcon message="Saved batch source — read only" description={`Batch ${batch.id}; ${batch.month}; worksheet ${source.sheetName}; captured divisor cell ${source.monthDaysCell || 'not recorded'}. Only selected financial/day inputs are shown. Formula text and cache are separate evidence; no calculation or save occurs here. Text literals/identifier-like values are redacted. Review the divisor from the captured formula before choosing a basis. Local referenced inputs are read-only trace evidence; their monthly/day meaning and original-file provenance remain separate review steps.`} />
    <Select aria-label="Source evidence row" allowClear showSearch optionFilterProp="label" placeholder="Inspect all selected source rows" style={{ width: '100%', marginBlock: 12 }} value={inspected} options={selected.map(row => ({ value: row.id, label: `Source row ${row.sourceRow}` }))} onChange={setSourceRowId} />
    <DataTable title="Captured financial formulas and inputs" rows={evidence} getRowId={row => row.key} exportDisabled emptyText="No safe financial source cells are available in this selected saved batch." columns={[
      { key: 'cell', label: 'Source cell', width: 100 }, { key: 'heading', label: 'Source heading', width: 170 },
      { key: 'formula', label: 'Original formula (redacted display)', wrap: true }, { key: 'cached', label: 'Saved numeric value', width: 150 }, { key: 'state', label: 'Evidence state', wrap: true }
    ]} />
  </section>
}
