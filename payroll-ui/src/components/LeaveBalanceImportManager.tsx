import { useState } from 'react'
import { finalizeLeaveBalanceImport, leaveBalanceSampleUrl, previewLeaveBalanceImport } from '../services/leaveAttendanceService'
import type { LeaveBalanceImportMapping, LeaveBalanceImportPreview } from '../types/payroll'
import DataTable, { type Column } from './DataTable'

const encodings = ['UTF-8', 'Windows-1252', 'ISO-8859-1']
const required: { key: keyof LeaveBalanceImportMapping; label: string }[] = [{ key: 'employeeNumber', label: 'Employee Number' }, { key: 'leaveType', label: 'Leave Type' }, { key: 'date', label: 'Date' }, { key: 'count', label: 'Count' }]

export default function LeaveBalanceImportManager({ clientId, onMessage }: { clientId: number; onMessage: (message: string) => void }) {
  const [file, setFile] = useState<File | null>(null), [encoding, setEncoding] = useState('UTF-8'), [preview, setPreview] = useState<LeaveBalanceImportPreview | null>(null)
  const [mapping, setMapping] = useState<LeaveBalanceImportMapping>({ employeeNumber: '', leaveType: '', date: '', count: '' }), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const upload = async (manualMapping?: LeaveBalanceImportMapping) => {
    if (!file) { setError('Select a CSV, XLS or XLSX file.'); return }
    setBusy(true); setError('')
    const response = await previewLeaveBalanceImport(clientId, file, encoding, manualMapping)
    if (response.ok && response.data) { setPreview(response.data); setMapping(response.data.mapping); onMessage('File parsed. Review mapping and preview records.') } else setError(response.error || 'Unable to parse file.')
    setBusy(false)
  }
  const importRows = async () => {
    if (!preview) return
    setBusy(true); setError('')
    const response = await finalizeLeaveBalanceImport(clientId, preview, encoding)
    if (response.ok && response.data) { onMessage(`Imported ${response.data.importedCount} rows. Skipped ${response.data.skippedCount}. Log #${response.data.logId}`); setPreview(null); setFile(null) } else setError(response.error || 'Import failed.')
    setBusy(false)
  }
  const downloadSample = async () => { const response = await fetch(leaveBalanceSampleUrl(clientId)); if (!response.ok) { setError('Unable to download the selected client sample.'); return }; const blob = await response.blob(); const url = URL.createObjectURL(blob); const link = document.createElement('a'); link.href = url; link.download = 'leave-balance-import-sample.csv'; link.click(); URL.revokeObjectURL(url) }
  return <section className="leave-import"><div className="card"><header><i className="blue">I</i><div><h3>Import Employee Leave Balance</h3><p>Upload opening balances, map fields, preview errors and import valid rows.</p></div></header><div className="import-steps"><span className={file ? 'done' : 'active'}>1 Upload</span><span className={preview ? 'done' : ''}>2 Mapping</span><span className={preview ? 'active' : ''}>3 Preview</span><span>4 Import</span></div><div className="grid"><label><span>Select file</span><input type="file" accept=".csv,.xls,.xlsx" onChange={event => setFile(event.target.files?.[0] ?? null)} /></label><label><span>Character encoding</span><select value={encoding} onChange={event => setEncoding(event.target.value)}>{encodings.map(item => <option key={item}>{item}</option>)}</select></label></div><div className="actions"><p>Required columns: Employee Number, Leave Type, Date, Count.</p><span><button type="button" className="secondary" onClick={() => void downloadSample()}>Download Sample File</button><button type="button" disabled={busy || !file} onClick={() => void upload()}>{busy ? 'Parsing...' : 'Upload & Auto-map'}</button></span></div>{error && <div className="form-errors"><p>{error}</p></div>}</div>{preview && <MappingCard preview={preview} mapping={mapping} setMapping={setMapping} remap={() => void upload(mapping)} busy={busy} />}{preview && <PreviewCard preview={preview} importRows={importRows} busy={busy} />}</section>
}

function MappingCard(p: { preview: LeaveBalanceImportPreview; mapping: LeaveBalanceImportMapping; setMapping: (mapping: LeaveBalanceImportMapping) => void; remap: () => void; busy: boolean }) {
  return <section className="card import-mapping"><header><i className="blue">M</i><div><h3>Column Mapping</h3><p>Auto-mapped columns can be manually adjusted before preview.</p></div></header><div className="grid">{required.map(field => <label className={p.preview.unmappedFields.includes(field.label) ? 'unmapped' : ''} key={field.key}><span>{field.label}</span><select value={p.mapping[field.key]} onChange={event => p.setMapping({ ...p.mapping, [field.key]: event.target.value })}><option value="">Unmapped</option>{p.preview.columns.map(column => <option key={column}>{column}</option>)}</select></label>)}</div>{p.preview.unmappedFields.length > 0 && <div className="form-warning">Unmapped fields: {p.preview.unmappedFields.join(', ')}</div>}<div className="actions"><p>Click re-preview after changing mapping.</p><button type="button" disabled={p.busy} onClick={p.remap}>Re-preview</button></div></section>
}

function PreviewCard(p: { preview: LeaveBalanceImportPreview; importRows: () => void; busy: boolean }) {
  return <section className="card import-preview"><header><i className="blue">P</i><div><h3>Preview</h3><p>{p.preview.validRecords.length} valid, {p.preview.errorRecords.length} skipped/error records.</p></div></header><h3>Valid records</h3><RecordTable rows={p.preview.validRecords} /><h3>Skipped / error records</h3><RecordTable rows={p.preview.errorRecords} showErrors /><div className="actions"><p>Only valid records will update employee leave balances.</p><button type="button" disabled={p.busy || p.preview.validRecords.length === 0} onClick={() => void p.importRows()}>{p.busy ? 'Importing...' : 'Final Import'}</button></div></section>
}

function RecordTable(p: { rows: LeaveBalanceImportPreview['validRecords']; showErrors?: boolean }) {
  type ImportRow = LeaveBalanceImportPreview['validRecords'][number]
  const columns: Column<ImportRow>[] = [
    { key: 'rowNumber', label: 'Row' },
    { key: 'employeeNumber', label: 'Employee Number' },
    { key: 'leaveType', label: 'Leave Type' },
    { key: 'date', label: 'Date' },
    { key: 'count', label: 'Count' }
  ]
  return <div><DataTable rows={p.rows.slice(0, 50)} getRowId={row => row.rowNumber} emptyText="No records." rowClassName={row => row.isValid ? '' : 'error'} exportFileName={p.showErrors ? 'leave-balance-import-errors' : 'leave-balance-import-valid'} columns={p.showErrors ? [...columns, { key: 'errors', label: 'Errors', value: row => row.errors.join('; ') }] : columns} />{p.rows.length > 50 && <p className="empty">Showing first 50 records.</p>}</div>
}
