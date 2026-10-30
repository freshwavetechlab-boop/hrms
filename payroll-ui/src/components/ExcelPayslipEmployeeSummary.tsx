import { useMemo, useState } from 'react'
import { Alert, Button, Descriptions, Drawer, Space, Tag, Tooltip } from 'antd'
import type { ExcelPayslipBatch, ExcelPayslipRow } from '../services/excelPayslipService'
import { buildPayslipEmployeeGroups, type PayslipEmployeeGroup } from '../utils/excelPayslipEmployeeGroups'
import { formatExcelPayslipAmount } from '../utils/excelPayslipImport'
import { savedPayslipAttendance } from '../utils/excelPayslipAttendanceDisplay'
import DataTable from './DataTable'
import ExcelPayslipAttendanceValue from './ExcelPayslipAttendanceValue'
import './ExcelPayslipEmployeeSummary.css'

type Props = { batch: ExcelPayslipBatch; busy: boolean; view?: 'Table' | 'Cards'; onReviewSource: (row: ExcelPayslipRow) => void }
type ComponentLine = { key: string; category: string; label: string; amount: number }
const labelKey = (value: string) => value.normalize('NFKC').toUpperCase().replace(/[^A-Z0-9]/g, '')
// Whitelisted source metadata is displayed; private identity/contact fields never
// enter this summary, its search values or its exports.
const safeText = (value: string) => value.replace(/(?:\d[\s-]*){7,}/g, '[private value]').replace(/\b[A-Z]{5}\d{4}[A-Z]\b/gi, '[private value]')
const sourceText = (row: ExcelPayslipRow, labels: string[]) => [...new Set(row.information.filter(item => labels.includes(labelKey(item.label))).map(item => safeText(item.value.trim())).filter(Boolean))].join(' / ') || 'Not recorded'
const locationLabels = ['WORKLOCATION', 'WORKLOCATIONNAME', 'LOCATION', 'LOCATIONNAME', 'LOCATIONNAMETEHSILSUBTEHSIL', 'TEHSILSUBTEHSIL']
const stateLabels = ['STATE', 'STATENAME', 'WORKSTATE', 'WORKLOCATIONSTATE']
const amount = (value: number | null | undefined) => typeof value === 'number' && Number.isFinite(value) ? formatExcelPayslipAmount(value, 2) : 'Unavailable'
const reviewText = (group: PayslipEmployeeGroup) => [group.requiresIdentityReview ? 'Employee Master identity review' : '', !group.isReviewedLocationSplit && group.reviewReason ? 'Source review required' : '', group.hasMultipleParts && !group.hasDatedCoverage ? 'Transfer dates not verified' : ''].filter(Boolean).join('; ') || 'Source summary only'

export default function ExcelPayslipEmployeeSummary(props: Props) {
  return <EmployeeSummary key={`${props.batch.clientId}:${props.batch.id}`} {...props} />
}

function EmployeeSummary({ batch, busy, view = 'Table', onReviewSource }: Props) {
  const result = useMemo(() => buildPayslipEmployeeGroups(batch), [batch])
  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const selected = result.groups.find(group => group.key === selectedKey)
  const reviewOriginal = (row: ExcelPayslipRow) => { if (busy) return; setSelectedKey(null); onReviewSource(row) }
  const summaryTitle = `${result.employeeCount} source employee groups · ${result.sourceRowCount} original source parts${result.unresolvedRowCount ? ` · ${result.unresolvedRowCount} source parts need identity review` : ''}`
  return <div className="excel-payslip-employee-summary" aria-label="Saved source employee summary">
    <DataTable<PayslipEmployeeGroup> title={summaryTitle} fillHeight rows={result.groups} view={view} getRowId={group => group.key} exportFileName={`excel-payslip-employee-summary-${batch.month}`} columns={[
      { key: 'employeeCode', label: 'Employee code', width: 130, value: group => safeText(group.employeeCode) || 'Not supplied' },
      { key: 'employeeName', label: 'Employee', width: 200, value: group => safeText(group.employeeName) },
      { key: 'sourcePartCount', label: 'Source parts', width: 110, value: group => group.rows.length },
      { key: 'sourceRows', label: 'Saved source rows', width: 150, value: group => group.sourceRows.join(', ') },
      { key: 'locations', label: 'Source locations', width: 220, value: group => group.locations.map(safeText).join(' / ') || 'Not recorded', wrap: true },
      { key: 'sourceUnits', label: 'Source payroll units', width: 220, value: group => group.sourceUnits.map(safeText).join(' / ') || 'Not recorded', wrap: true },
      { key: 'sourceAttendanceTotal', label: 'Source attendance total', width: 170, value: group => group.sourceAttendanceTotal, render: group => <Tooltip title="Sum of the original Attendance fields. This does not establish dated coverage, actual presence or payable days."><span>{group.sourceAttendanceTotal ?? 'Unavailable'}</span></Tooltip> },
      { key: 'gross', label: 'Source gross', width: 150, value: group => group.totals.gross, render: group => amount(group.totals.gross) },
      { key: 'deductions', label: 'Source deductions', width: 155, value: group => group.totals.deductions, render: group => amount(group.totals.deductions) },
      { key: 'employerContributions', label: 'Source employer contributions', width: 190, value: group => group.totals.employerContributions, render: group => amount(group.totals.employerContributions) },
      { key: 'netPay', label: 'Source net pay', width: 155, value: group => group.totals.netPay, render: group => <strong>{amount(group.totals.netPay)}</strong> },
      { key: 'review', label: 'Review', width: 220, value: reviewText, render: group => <Space direction="vertical" size={3}>{group.requiresIdentityReview && <Tag color="warning">Employee Master identity review</Tag>}{group.hasMultipleParts && !group.hasDatedCoverage && <Tag color="warning">Transfer dates not verified</Tag>}{!group.isReviewedLocationSplit && group.reviewReason && <Tag color="warning">Source review required</Tag>}</Space> },
    ]} actionsWidth={150} actions={group => <Button size="small" disabled={busy} aria-label={`Source parts for ${safeText(group.employeeCode || group.employeeName)}`} onClick={() => setSelectedKey(group.key)}>Source parts ({group.rows.length})</Button>} />
    <Drawer title={selected ? `Source parts · ${safeText(selected.employeeCode)} · ${safeText(selected.employeeName)}` : 'Source parts'} open={!!selected} width={1000} contentWrapperStyle={{ maxWidth: '96vw' }} className="excel-payslip-employee-parts" onClose={() => setSelectedKey(null)} footer={<Button aria-label="Close source parts" onClick={() => setSelectedKey(null)}>Close</Button>}>
      {selected && <>
        <Alert type="info" showIcon message={`${selected.rows.length} original source parts · source net ${amount(selected.totals.netPay)}`} description="Amounts below remain separate source records. This view does not create a combined payslip or recalculate payroll. Location and state are shown as recorded; dated coverage is not verified. Employee Master identity review and payroll rules remain separate." />
        {selected.requiresIdentityReview && <Alert type="warning" showIcon message="Employee Master identity review is still required" />}
        {selected.rows.map(row => {
          const attendance = savedPayslipAttendance(row)
          const components: ComponentLine[] = [
            ...row.earnings.map((item, index) => ({ key: `earning:${index}`, category: 'Earning', label: safeText(item.label), amount: item.amount })),
            ...row.deductions.map((item, index) => ({ key: `deduction:${index}`, category: 'Deduction', label: safeText(item.label), amount: item.amount })),
            ...row.employerContributions.map((item, index) => ({ key: `employer:${index}`, category: 'Employer contribution', label: safeText(item.label), amount: item.amount })),
          ]
          return <section key={row.id} className="excel-payslip-employee-part" aria-label={`Original source part ${row.sourceRow}`}>
            <div className="excel-payslip-employee-part-heading"><strong>Saved source row {row.sourceRow}</strong><Button size="small" disabled={busy} aria-label={`Review original source row ${row.sourceRow}`} onClick={() => reviewOriginal(row)}>Review original row</Button></div>
            <Descriptions bordered size="small" column={{ xs: 1, sm: 2 }}>
              <Descriptions.Item label="Source workbook">{sourceText(row, ['SOURCEWORKBOOK'])}</Descriptions.Item>
              <Descriptions.Item label="Source worksheet">{sourceText(row, ['SOURCEWORKSHEET'])}</Descriptions.Item>
              <Descriptions.Item label="Source Excel row">{sourceText(row, ['SOURCEEXCELROW'])}</Descriptions.Item>
              <Descriptions.Item label="Source payroll unit">{sourceText(row, ['PAYROLLUNIT'])}</Descriptions.Item>
              <Descriptions.Item label="Work location">{sourceText(row, locationLabels)}</Descriptions.Item>
              <Descriptions.Item label="State">{sourceText(row, stateLabels)}</Descriptions.Item>
              <Descriptions.Item label="Source attendance"><ExcelPayslipAttendanceValue value={attendance.source} showLabel /></Descriptions.Item>
              <Descriptions.Item label="Present days"><ExcelPayslipAttendanceValue value={attendance.present} /></Descriptions.Item>
              <Descriptions.Item label="Payable days"><ExcelPayslipAttendanceValue value={attendance.payable} /></Descriptions.Item>
              <Descriptions.Item label="Source net pay"><strong>{amount(row.netPay)}</strong></Descriptions.Item>
            </Descriptions>
            <DataTable<ComponentLine> rows={components} getRowId={line => line.key} hideSearch exportDisabled columns={[
              { key: 'category', label: 'Category', width: 180 }, { key: 'label', label: 'Source component', width: 240 }, { key: 'amount', label: 'Source amount', width: 150, render: line => amount(line.amount) },
            ]} />
          </section>
        })}
      </>}
    </Drawer>
  </div>
}
