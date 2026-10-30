import type { ReactNode } from 'react'
import { Button, Space, Tag } from 'antd'
import type { ExcelPayslipRow, PayslipIdentityMatch } from '../services/excelPayslipService'
import { usePayslipCodePreview } from '../hooks/usePayslipCodePreview'
import { orderPayslipPreviewRows, payslipPreviewRowStatus } from '../utils/excelPayslipCodePreview'
import { PayslipCodeMatchLabel, PayslipCodePreviewTools } from './ExcelPayslipCodePreview'
import DataTable from './DataTable'

export default function ExcelPayslipEmployeePreview(p: { rows: ExcelPayslipRow[]; simpleLayout: boolean; generationEnabled: boolean; disabled: boolean; canManage: boolean; amount: (value: number) => ReactNode; onDetails: (row: ExcelPayslipRow & {previewIdentity?:PayslipIdentityMatch}) => void; onCorrect: (row: ExcelPayslipRow) => void; onExclude: (sourceRow: number) => void }) {
  const preview = usePayslipCodePreview()
  const rows = orderPayslipPreviewRows(p.rows,row=>row.sourceRow,row=>row.employeeCode,preview)
  return <section aria-label="Employee import preview"><PayslipCodePreviewTools /><DataTable key={preview.orderKey} rows={rows} title="Employee preview" getRowId={row => row.id} exportDisabled pageSizeOptions={[10, 25, 50, 100, 250]} rowClassName={row => preview.status !== 'Ready' ? 'excel-code-pending-row' : payslipPreviewRowStatus(preview,row.sourceRow,row.employeeCode)==='Existing' ? 'excel-code-existing-row' : payslipPreviewRowStatus(preview,row.sourceRow,row.employeeCode)==='Review'?'excel-code-review-row':'excel-code-new-row'} columns={[
    { key: 'sourceRow', label: 'Source row' }, { key: 'employeeName', label: 'Employee' }, { key: 'employeeCode', label: 'Employee code', render: row => <span className="excel-code-preview-label">{row.employeeCode?<span>{row.employeeCode}</span>:<Tag color="blue">{p.generationEnabled ? 'Auto on save' : 'Existing Master code required'}</Tag>}<PayslipCodeMatchLabel code={row.employeeCode} sourceRow={row.sourceRow} /></span> }, { key: 'netPay', label: p.simpleLayout ? 'In Hand Salary' : 'Net pay as stated', render: row => p.amount(row.netPay) },
  ]} actions={row => <Space size={4}><Button size="small" onClick={() => p.onDetails({...row,previewIdentity:preview.identityMode&&preview.status==='Ready'?preview.identities.get(row.sourceRow):undefined})}>Details</Button>{p.canManage && <Button size="small" disabled={p.disabled} onClick={() => p.onCorrect(row)}>Correct amounts</Button>}<Button size="small" danger disabled={p.disabled} onClick={() => p.onExclude(row.sourceRow)}>Exclude row</Button></Space>} /></section>
}
