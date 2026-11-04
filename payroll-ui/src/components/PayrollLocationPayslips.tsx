import { useRef, useState } from 'react'
import { Alert, Button, Drawer, Space } from 'antd'
import { DownloadOutlined } from '@ant-design/icons'
import DataTable from './DataTable'
import SearchSelect from './SearchSelect'
import { getBlob, getJsonResult } from '../services/apiClient'
import type { PayRun } from '../types/payroll'
import { money } from '../utils/salary'

type Slip = { employeeId: number; employeeCode: string; employeeName: string; partKey: string; workLocationId: number; workLocationName: string; grossPay: number; totalDeductions: number; netPay: number; presentDays: number | null; payableDays: number; fileName: string; html: string }
export default function PayrollLocationPayslips({ run }: { run: PayRun }) {
  const [open, setOpen] = useState(false), [loading, setLoading] = useState(false), [pdfBusy, setPdfBusy] = useState(false)
  const [slips, setSlips] = useState<Slip[]>([]), [error, setError] = useState(''), [locationId, setLocationId] = useState(0)
  const [preview, setPreview] = useState<Slip | null>(null)
  const sequence = useRef(0), downloading = useRef(false)
  const show = async () => {
    const request = ++sequence.current
    setOpen(true); setLoading(true); setSlips([]); setError(''); setLocationId(0)
    const result = await getJsonResult<Slip[]>(`/api/pay-runs/${run.id}/location-payslips`, [], { loader: false, toast: false, timeoutMs: 120000 })
    if (sequence.current !== request) return
    setLoading(false)
    if (result.ok) setSlips(result.data)
    else setError(result.error || 'Unable to load saved location payslips.')
  }
  const locations = [...new Map(slips.map(slip => [slip.workLocationId, slip.workLocationName])).entries()].sort((a, b) => a[1].localeCompare(b[1]))
  const rows = slips.filter(slip => !locationId || slip.workLocationId === locationId)
  const download = async () => {
    if (downloading.current || !rows.length) return
    const request = sequence.current
    downloading.current = true; setPdfBusy(true); setError('')
    try {
      const result = await getBlob(`/api/pay-runs/${run.id}/location-payslips/pdf?workLocationId=${locationId}`)
      if (sequence.current !== request) return
      if (!result.ok || !result.data) throw Error(result.error || 'Unable to prepare location PDF.')
      const link = document.createElement('a'), url = URL.createObjectURL(result.data)
      link.href = url; link.download = `payroll-${run.id}-${run.payPeriod}-location-${locationId || 'all'}.pdf`; link.click()
      window.setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (e) { if (sequence.current === request) setError(e instanceof Error ? e.message : 'PDF download failed.') }
    finally { downloading.current = false; setPdfBusy(false) }
  }
  return <>
    <Button aria-label="Location payslips" icon={<DownloadOutlined />} disabled={!['Draft', 'Pending Approval', 'Approved', 'Partially Paid', 'Paid'].includes(run.status)} onClick={() => void show()}>Location payslips</Button>
    <Drawer title={`${run.clientName} · ${run.payPeriod} · Location payslips`} width="min(1080px,98vw)" open={open} onClose={() => { sequence.current += 1; setOpen(false); setPreview(null) }}
      footer={<Space wrap><SearchSelect value={locationId} disabled={loading || pdfBusy} options={[{ value: 0, label: 'All locations' }, ...locations.map(([value, label]) => ({ value, label }))]} onChange={value => setLocationId(Number(value))} />
        <span>{rows.length} slips · {money(rows.reduce((sum, row) => sum + row.netPay, 0))}</span><Button aria-label={`Download PDF (${rows.length})`} type="primary" icon={<DownloadOutlined />} loading={pdfBusy} disabled={loading || !rows.length} onClick={() => void download()}>Download PDF ({rows.length})</Button></Space>}>
      {run.status === 'Draft' || run.status === 'Pending Approval' ? <Alert type="info" showIcon message="Draft previews are marked DRAFT. Approval, payment and delivery are separate actions." /> : null}
      {error && <Alert type="error" showIcon message={error} />}
      <DataTable rows={rows} loading={loading} getRowId={row => `${row.employeeId}:${row.partKey}`} scrollY={560} exportDisabled columns={[
        { key: 'employeeName', label: 'Employee', value: row => `${row.employeeName} ${row.employeeCode}`, render: row => <>{row.employeeName}<small>{row.employeeCode}</small></> },
        { key: 'workLocationName', label: 'Location' }, { key: 'payableDays', label: 'Payable days' },
        { key: 'grossPay', label: 'Gross', value: row => money(row.grossPay) }, { key: 'totalDeductions', label: 'Deductions', value: row => money(row.totalDeductions) },
        { key: 'netPay', label: 'Net pay', value: row => money(row.netPay) },
      ]} actions={row => <Button size="small" onClick={() => setPreview(row)}>Preview</Button>} />
    </Drawer>
    <Drawer title={preview ? `${preview.employeeCode} · ${preview.workLocationName}` : 'Payslip'} open={!!preview} onClose={() => setPreview(null)} width="min(900px,98vw)">
      {preview && <iframe title="Saved location payslip preview" sandbox="" srcDoc={preview.html} style={{ width: '100%', height: '78vh', border: 0 }} />}
    </Drawer>
  </>
}
