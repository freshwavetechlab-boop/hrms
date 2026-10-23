import { useRef, useState } from 'react'
import { Alert, Button, Drawer, Space, Tag } from 'antd'
import { updateExcelPayslipEmails, type ExcelPayslipBatch } from '../services/excelPayslipService'
import { parseExcelPayslipFile } from '../utils/excelPayslipImport'
import { readPayslipEmailUpdates } from '../utils/excelPayslipEmailUpdates'
import FileDropZone from './FileDropZone'
import DataTable from './DataTable'

export default function ExcelPayslipEmailUpdates({ batch, initial, onClose, onSaved, onBusy }: {
  batch: ExcelPayslipBatch; onClose: () => void; onSaved: (batch: ExcelPayslipBatch, changed: number) => void; onBusy: (busy: boolean) => void
  initial?: { preview: ReturnType<typeof readPayslipEmailUpdates>; fileName: string }
}) {
  const [preview, setPreview] = useState<ReturnType<typeof readPayslipEmailUpdates> | null>(initial?.preview || null)
  const [fileName, setFileName] = useState(initial?.fileName || ''), [error, setError] = useState(''), [busy, setBusy] = useState(false)
  const request = useRef(0), inFlight = useRef(false)
  const upload = async (file: File) => {
    if (inFlight.current) return
    const sequence = ++request.current
    setFileName(file.name); setPreview(null); setError('')
    try {
      const result = readPayslipEmailUpdates(await parseExcelPayslipFile(file), batch)
      if (sequence === request.current) setPreview(result)
    } catch (failure) { if (sequence === request.current) setError(failure instanceof Error ? failure.message : 'Unable to read the email list.') }
  }
  const save = async () => {
    if (!preview?.updates.length || inFlight.current) return
    inFlight.current = true; setBusy(true); onBusy(true); setError('')
    const result = await updateExcelPayslipEmails(batch.clientId, batch.id, { month: batch.month, sourceFileName: fileName, updates: preview.updates })
    inFlight.current = false; setBusy(false); onBusy(false)
    if (!result.ok || !result.data) { setError(result.error || 'Email updates could not be confirmed. Retry is safe; salary and delivery history are retained.'); return }
    onSaved(result.data, preview.updates.length)
  }
  const close = () => { if (!busy) { request.current++; onClose() } }
  return <Drawer open title="Import email updates" width="min(1040px, 98vw)" className="excel-payslip-email-updates" onClose={close} closable={!busy} maskClosable={!busy}
    footer={<Space wrap className="excel-payslip-footer"><Button disabled={busy} onClick={close}>Cancel</Button><Button type="primary" loading={busy} disabled={!preview?.updates.length} onClick={() => void save()}>Save email updates ({preview?.updates.length || 0})</Button></Space>}>
    <Alert type="info" showIcon message="Export the list, fill Email, then upload it here" description="Blank emails are skipped. Updates contacts in this batch; salary and sent history stay the same. Already queued mail keeps its recipient. Saving sends no mail." />
    {error && <Alert type="error" showIcon message={error} />}
    <FileDropZone accept=".xlsx,.csv" fileName={fileName} title="Upload employee email list" hint="Keep Employee code, Batch ID and Payslip ID unchanged; edit only Email." onFile={file => { if (!busy) void upload(file) }} />
    {preview && <><Space wrap style={{ marginBlock: 12 }}><Tag color="blue">{preview.updates.length} emails to update</Tag><Tag>{preview.unchanged} unchanged</Tag><Tag>{preview.blank} blank skipped</Tag></Space>
      <DataTable rows={preview.updates} getRowId={row => row.rowId} title="Review email changes" exportDisabled columns={[
        { key: 'employeeCode', label: 'Employee code' }, { key: 'employeeName', label: 'Employee' }, { key: 'location', label: 'Location' },
        { key: 'previousEmail', label: 'Saved email' }, { key: 'email', label: 'New email' },
      ]} /></>}
  </Drawer>
}
