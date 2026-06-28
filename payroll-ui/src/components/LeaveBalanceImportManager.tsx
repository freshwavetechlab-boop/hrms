import { useState } from 'react'
import { downloadLeaveBalanceSample, finalizeLeaveBalanceImport, previewLeaveBalanceImport } from '../services/leaveAttendanceService'
import type { LeaveBalanceImportMapping, LeaveBalanceImportPreview } from '../types/payroll'
import DataTable, { type Column } from './DataTable'
import FileDropZone from './FileDropZone'
import SearchSelect, { selectOptions } from './SearchSelect'

const encodings = ['UTF-8', 'Windows-1252', 'ISO-8859-1']
const required: { key: keyof LeaveBalanceImportMapping; label: string }[] = [{ key: 'employeeNumber', label: 'Employee Number' }, { key: 'leaveType', label: 'Leave Type Code' }, { key: 'date', label: 'Balance As Of Date' }, { key: 'count', label: 'Opening Balance' }]

export default function LeaveBalanceImportManager({ clientId, onMessage }: { clientId: number; onMessage: (message: string) => void }) {
  const [file, setFile] = useState<File | null>(null), [encoding, setEncoding] = useState('UTF-8'), [preview, setPreview] = useState<LeaveBalanceImportPreview | null>(null)
  const [mapping, setMapping] = useState<LeaveBalanceImportMapping>({ employeeNumber: '', leaveType: '', date: '', count: '' }), [busy, setBusy] = useState(false), [error, setError] = useState(''), [uploadProgress, setUploadProgress] = useState(0), [uploadStage, setUploadStage] = useState('')
  const upload = async (manualMapping?: LeaveBalanceImportMapping) => {
    if (!file) { setError('Select a CSV, XLS or XLSX file.'); return }
    setBusy(true); setError(''); setUploadProgress(0); setUploadStage('Uploading file...')
    const response = await previewLeaveBalanceImport(clientId, file, encoding, manualMapping, percent => { setUploadProgress(percent); if (percent === 100) setUploadStage('Upload complete. Validating file...') })
    if (response.ok && response.data) {
      setUploadProgress(100)
      const hasValidationIssues = response.data.unmappedFields.length > 0 || response.data.errorRecords.length > 0
      setUploadStage(hasValidationIssues ? `Upload complete. Validation found ${response.data.errorRecords.length || response.data.unmappedFields.length} issue(s).` : 'Upload and validation successful.')
      setPreview(response.data)
      setMapping(response.data.mapping)
      onMessage(hasValidationIssues ? 'File uploaded, but validation issues need correction.' : 'File parsed successfully. Review mapping and preview records.')
    } else { setUploadStage('Upload failed.'); setError(response.error || 'Unable to parse file.') }
    setBusy(false)
  }
  const importRows = async () => {
    if (!preview) return
    setBusy(true); setError('')
    const response = await finalizeLeaveBalanceImport(clientId, preview, encoding)
    if (response.ok && response.data) { onMessage(`Imported ${response.data.importedCount} rows. Skipped ${response.data.skippedCount}. Log #${response.data.logId}`); setPreview(null); setFile(null) } else setError(response.error || 'Import failed.')
    setBusy(false)
  }
  const downloadSample = async () => { const response = await downloadLeaveBalanceSample(clientId); if (!response.ok || !response.data) { setError('Unable to download the selected client sample.'); return }; const url = URL.createObjectURL(response.data); const link = document.createElement('a'); link.href = url; link.download = 'leave-balance-import-sample.csv'; link.click(); URL.revokeObjectURL(url) }
  const hasValidationIssues = Boolean(preview && (preview.errorRecords.length > 0 || preview.unmappedFields.length > 0))
  return <section className="leave-import">
    <div className="card">
      <header><i className="blue">I</i><div><h3>Import Employee Leave Balance</h3><p>Upload opening balances, map fields, preview errors and import valid rows.</p></div></header>
      <div className="import-steps"><span className={file ? 'done' : 'active'}>1 Upload</span><span className={preview && !hasValidationIssues ? 'done' : ''}>2 Mapping</span><span className={preview ? 'active' : ''}>3 Preview</span><span>4 Import</span></div>
      <div className="grid"><label className="wide"><span>Select file</span><FileDropZone accept=".csv,.xls,.xlsx" fileName={file?.name} title="Drop CSV/XLSX here or browse" hint="Supports .csv, .xls and .xlsx leave balance files." onFile={next => { setFile(next); setUploadProgress(0); setUploadStage(''); setError(''); setPreview(null) }} /></label><label><span>Character encoding</span><SearchSelect value={encoding} onChange={setEncoding} options={selectOptions(encodings)} /></label></div>
      {uploadStage && <div className={`import-upload-progress ${error ? 'failed' : hasValidationIssues ? 'warning' : uploadProgress === 100 && !busy ? 'complete' : ''}`}><div><strong>{uploadStage}</strong><span className="import-progress-meta"><b>{uploadProgress}% uploaded</b><button type="button" aria-label="Close upload status" onClick={() => { setUploadStage(''); setUploadProgress(0) }}>×</button></span></div><span><i style={{ width: `${uploadProgress}%` }} /></span></div>}
      <div className="actions"><p>Required: Employee Number, Leave Type Code, Balance As Of Date (YYYY-MM-DD), Opening Balance.</p><span><button type="button" className="secondary" onClick={() => void downloadSample()}>Download Sample File</button><button type="button" disabled={busy || !file} onClick={() => void upload()}>{busy ? `${uploadProgress}% Uploading...` : 'Upload & Auto-map'}</button></span></div>
      {error && <div className="form-errors runtime-error" role="alert"><strong>Runtime error</strong><p>{error}</p></div>}
    </div>
    {preview && <MappingCard preview={preview} mapping={mapping} setMapping={setMapping} remap={() => void upload(mapping)} busy={busy} />}
    {preview && <PreviewCard preview={preview} importRows={importRows} busy={busy} />}
  </section>
}

function MappingCard(p: { preview: LeaveBalanceImportPreview; mapping: LeaveBalanceImportMapping; setMapping: (mapping: LeaveBalanceImportMapping) => void; remap: () => void; busy: boolean }) {
  return <section className="card import-mapping"><header><i className="blue">M</i><div><h3>Column Mapping</h3><p>Auto-mapped columns can be manually adjusted before preview.</p></div></header><div className="grid">{required.map(field => <label className={p.preview.unmappedFields.includes(field.label) ? 'unmapped' : ''} key={field.key}><span>{field.label}</span><SearchSelect value={p.mapping[field.key]} onChange={value => p.setMapping({ ...p.mapping, [field.key]: value })} options={selectOptions(p.preview.columns, 'Unmapped')} /></label>)}</div>{p.preview.unmappedFields.length > 0 && <div className="form-warning">Unmapped fields: {p.preview.unmappedFields.join(', ')}</div>}<div className="actions"><p>Click re-preview after changing mapping.</p><button type="button" disabled={p.busy} onClick={p.remap}>Re-preview</button></div></section>
}

function PreviewCard(p: { preview: LeaveBalanceImportPreview; importRows: () => void; busy: boolean }) {
  return <section className="card import-preview"><header><i className="blue">P</i><div><h3>Preview</h3><p>{p.preview.validRecords.length} valid, {p.preview.errorRecords.length} skipped/error records.</p></div></header><h3>Valid records</h3><RecordTable rows={p.preview.validRecords} /><h3>Skipped / error records</h3><RecordTable rows={p.preview.errorRecords} showErrors /><div className="actions"><p>Only valid records will update employee leave balances.</p><button type="button" disabled={p.busy || p.preview.validRecords.length === 0} onClick={() => void p.importRows()}>{p.busy ? 'Importing...' : 'Final Import'}</button></div></section>
}

function RecordTable(p: { rows: LeaveBalanceImportPreview['validRecords']; showErrors?: boolean }) {
  type ImportRow = LeaveBalanceImportPreview['validRecords'][number]
  const columns: Column<ImportRow>[] = [
    { key: 'rowNumber', label: 'Row' },
    { key: 'employeeNumber', label: 'Employee Number' },
    { key: 'leaveType', label: 'Leave Type Code' },
    { key: 'date', label: 'Balance As Of Date' },
    { key: 'count', label: 'Opening Balance' }
  ]
  return <div><DataTable rows={p.rows.slice(0, 50)} getRowId={row => row.rowNumber} emptyText="No records." rowClassName={row => row.isValid ? '' : 'error'} exportFileName={p.showErrors ? 'leave-balance-import-errors' : 'leave-balance-import-valid'} columns={p.showErrors ? [...columns, { key: 'errors', label: 'Errors', value: row => row.errors.join('; ') }] : columns} />{p.rows.length > 50 && <p className="empty">Showing first 50 records.</p>}</div>
}
