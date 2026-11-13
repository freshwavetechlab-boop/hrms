import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Collapse, Descriptions, Drawer, InputNumber, Select, Space, Spin, Tag } from 'antd'
import DataTable from './DataTable'
import FileDropZone from './FileDropZone'
import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import { applyPayslipUpdate, getPayslipUpdateCatalog, previewPayslipUpdate, type PayslipUpdateApplyRequest, type PayslipUpdateCatalog, type PayslipUpdateChange, type PayslipUpdatePatch, type PayslipUpdatePreview, type PayslipUpdateResult } from '../services/excelPayslipUpdateService'
import { excelColumnLetter, parseExcelPayslipFile, payslipHeaders, type PayslipSheet } from '../utils/excelPayslipImport'
import { isPayslipUpdateCodeHeader, parsePayslipUpdateInput, payslipUpdateTemplate, selectedPayslipUpdateRows, suggestPayslipUpdateMapping, unmappedPayslipUpdateColumns, updateSelectionKey, type PayslipUpdateMapping } from '../utils/excelPayslipUpdates'
import { downloadXlsx } from '../utils/xlsx'
import './ExcelPayslipBatchUpdate.css'

export default function ExcelPayslipBatchUpdate({ batch, onClose, onBusy, onSaved }: { batch: ExcelPayslipBatch; onClose: () => void; onBusy: (busy: boolean) => void; onSaved: (result: PayslipUpdateResult) => void }) {
  const [catalog, setCatalog] = useState<PayslipUpdateCatalog | null>(null)
  const [loading, setLoading] = useState(true), [working, setWorking] = useState(false), [error, setError] = useState(''), [reload, setReload] = useState(0)
  const [sheets, setSheets] = useState<PayslipSheet[]>([]), [sheetName, setSheetName] = useState(''), [headerRow, setHeaderRow] = useState(1), [codeColumn, setCodeColumn] = useState<number>()
  const [mapping, setMapping] = useState<PayslipUpdateMapping>({}), [excluded, setExcluded] = useState<number[]>([]), [fileName, setFileName] = useState('')
  const [preview, setPreview] = useState<PayslipUpdatePreview | null>(null), [selected, setSelected] = useState<string[]>([]), [reviewedSelection, setReviewedSelection] = useState('')
  const [acknowledged, setAcknowledged] = useState(false), [zeroAcknowledged, setZeroAcknowledged] = useState(false)
  const [ignoreAcknowledged, setIgnoreAcknowledged] = useState(false)
  const [pendingApply, setPendingApply] = useState<PayslipUpdateApplyRequest | null>(null), [visibleChangeIds, setVisibleChangeIds] = useState<string[]>([])
  const active = useRef(true), sequence = useRef(0), inFlight = useRef(false)
  useEffect(() => {
    active.current = true
    let current = true
    void getPayslipUpdateCatalog(batch.clientId, batch.id).then(result => {
      if (!current) return
      const value = result.data
      if (!result.ok || !value || value.clientId !== batch.clientId || value.batchId !== batch.id || value.month !== batch.month || !Array.isArray(value.fields) || !Array.isArray(value.rows) || !Array.isArray(value.blockers)) setError(result.error || 'The API could not confirm this saved batch and its update fields. No changes were made.')
      else { setCatalog(value); setError('') }
      setLoading(false)
    })
    return () => { current = false; active.current = false }
  }, [batch.clientId, batch.id, batch.month, reload])
  const sheet = sheets.find(value => value.name === sheetName)
  const headers = useMemo(() => sheet ? payslipHeaders(sheet, headerRow) : [], [sheet, headerRow])
  const parsed = useMemo(() => sheet && catalog ? parsePayslipUpdateInput(sheet, headerRow, codeColumn, mapping, catalog, excluded) : null, [sheet, headerRow, codeColumn, mapping, catalog, excluded])
  const unmapped = useMemo(() => sheet ? unmappedPayslipUpdateColumns(sheet, headerRow, codeColumn, mapping) : [], [sheet, headerRow, codeColumn, mapping])
  const locked = working || pendingApply !== null
  const selectedChanges = (preview?.changes || []).filter(change => selected.includes(change.id) && change.canApply && !change.computed)
  const computedChanges = (preview?.changes || []).filter(change => change.computed)
  const blockedChanges = (preview?.changes || []).filter(change => !change.canApply && !change.computed)
  const zeroChanges = selectedChanges.filter(change => catalog?.fields.find(field => field.id === change.fieldId)?.type === 'number' && /^[-+]?0+(?:\.0+)?$/.test(change.newValue.trim().replace(/,/g, '')))
  const exactReview = Boolean(preview) && reviewedSelection === updateSelectionKey(selected)
  const canSave = Boolean(preview?.token) && !preview?.blockers.length && !blockedChanges.length && exactReview && selectedChanges.length > 0 && acknowledged && (!zeroChanges.length || zeroAcknowledged) && (!unmapped.length || ignoreAcknowledged)
  const captureVisibleChanges = useCallback((changes: PayslipUpdateChange[]) => {
    const ids = changes.filter(change => change.canApply && !change.computed).map(change => change.id)
    setVisibleChangeIds(current => current.length === ids.length && current.every((id, index) => id === ids[index]) ? current : ids)
  }, [])
  const resetReview = () => { sequence.current++; setPreview(null); setSelected([]); setReviewedSelection(''); setAcknowledged(false); setZeroAcknowledged(false); setIgnoreAcknowledged(false); setPendingApply(null); setError('') }
  const selectRows = (ids: string[]) => { setSelected(ids); setAcknowledged(false); setZeroAcknowledged(false); setError('') }
  const setSource = (next: PayslipSheet, suppliedHeader?: number) => {
    const header = suppliedHeader ?? next.rows.find(row => row.cells.some(cell => isPayslipUpdateCodeHeader(cell?.value || '')))?.sourceRow ?? next.rows.find(row => row.cells.some(cell => cell?.value.trim()))?.sourceRow ?? 1
    const sourceHeaders = payslipHeaders(next, header)
    setSheetName(next.name); setHeaderRow(header); setCodeColumn(sourceHeaders.findIndex(isPayslipUpdateCodeHeader) >= 0 ? sourceHeaders.findIndex(isPayslipUpdateCodeHeader) : undefined)
    setMapping(suggestPayslipUpdateMapping(sourceHeaders, catalog?.fields || [])); setExcluded([]); resetReview()
  }
  const upload = async (file: File) => {
    if (!catalog || locked || inFlight.current) return
    inFlight.current = true; setWorking(true); onBusy(true); resetReview(); setFileName(file.name)
    const request = sequence.current
    try {
      const parsedSheets = await parseExcelPayslipFile(file)
      if (!active.current || request !== sequence.current) return
      const first = parsedSheets.find(value => value.rows.some(row => row.cells.some(cell => isPayslipUpdateCodeHeader(cell?.value || '')))) || parsedSheets[0]
      if (!first) throw new Error('This workbook has no readable worksheet.')
      setSheets(parsedSheets); setSource(first)
    } catch (failure) { if (active.current) { setSheets([]); setError(failure instanceof Error ? failure.message : 'Unable to read this spreadsheet.') } }
    finally { inFlight.current = false; if (active.current) { setWorking(false); onBusy(false) } }
  }
  const review = async (rows: PayslipUpdatePatch[]) => {
    if (!catalog || locked || inFlight.current || !rows.length || catalog.blockers.length || unmapped.length && !ignoreAcknowledged) return
    inFlight.current = true; setWorking(true); onBusy(true); setError(''); setAcknowledged(false); setZeroAcknowledged(false)
    const request = ++sequence.current
    try {
      const result = await previewPayslipUpdate(batch.clientId, batch.id, { month: batch.month, sourceFileName: fileName, rows })
      if (!active.current || request !== sequence.current) return
      if (!result.ok || !result.data || typeof result.data.token !== 'string' || !Array.isArray(result.data.changes) || !Array.isArray(result.data.blockers)) throw new Error(result.error || 'The server could not review these changes. No data was saved.')
      const changes = result.data.changes
      if (new Set(changes.map(change => change.id)).size !== changes.length) throw new Error('The review returned duplicate change IDs. Refresh the review before saving.')
      const ids = changes.filter(change => change.canApply && !change.computed).map(change => change.id)
      setPreview(result.data); setSelected(ids); setReviewedSelection(updateSelectionKey(ids)); setVisibleChangeIds(ids)
    } catch (failure) { if (active.current) setError(failure instanceof Error ? failure.message : 'The review failed. No data was saved.') }
    finally { inFlight.current = false; if (active.current) { setWorking(false); onBusy(false) } }
  }
  const save = async () => {
    if (inFlight.current || (!pendingApply && !canSave) || !preview) return
    const request = pendingApply || { token: preview.token, selectedChangeIds: selectedChanges.map(change => change.id), requestId: crypto.randomUUID(), acknowledged: true, acknowledgeZeroChanges: zeroAcknowledged }
    setPendingApply(request); inFlight.current = true; setWorking(true); onBusy(true); setError('')
    try {
      const result = await applyPayslipUpdate(batch.clientId, batch.id, request)
      if (!active.current) return
      if (!result.ok && [400, 403, 404, 409].includes(result.status)) {
        setPendingApply(null); setPreview(null); setSelected([]); setReviewedSelection(''); setAcknowledged(false); setZeroAcknowledged(false)
        setError(`${result.error || 'The update was rejected before saving.'} Review the spreadsheet and preview again before saving.`); return
      }
      if (!result.ok || !result.data?.batch || result.data.batch.id !== batch.id || result.data.batch.clientId !== batch.clientId || result.data.batch.month !== batch.month) {
        setError(`${result.error || 'The update result could not be confirmed.'} Keep this panel open and retry. The same request reference is reused.`); return
      }
      setPendingApply(null); onSaved(result.data)
    } catch (failure) { if (active.current) setError(`${failure instanceof Error ? failure.message : 'The update could not be confirmed.'} Retry with this same request reference.`) }
    finally { inFlight.current = false; if (active.current) { setWorking(false); onBusy(false) } }
  }
  const close = () => { if (!locked) onClose() }
  return <Drawer open title="Update saved payslips from Excel" width={1180} rootClassName="excel-payslip-batch-update-root" className="excel-payslip-batch-update" contentWrapperStyle={{ width: 'min(1180px, 100vw)', maxWidth: '100vw' }} style={{ width: '100%', minWidth: 0, maxWidth: '100%' }} onClose={close} closable={!locked} maskClosable={!locked}
    footer={<Space wrap className="excel-batch-update-footer"><Button disabled={locked} onClick={close}>Close</Button><Button loading={working} disabled={working || !pendingApply && !canSave} type="primary" onClick={() => void save()}>{pendingApply ? 'Retry / check saved update' : `Save reviewed changes (${selectedChanges.length})`}</Button></Space>}>
    <Descriptions size="small" column={{ xs: 1, sm: 2, lg: 3 }}><Descriptions.Item label="Client">{batch.clientName || batch.clientId}</Descriptions.Item><Descriptions.Item label="Salary month">{batch.month}</Descriptions.Item><Descriptions.Item label="Saved batch">{batch.sourceFileName}</Descriptions.Item></Descriptions>
    <Alert showIcon type="info" message="Update this batch and matched Employee Master records together" description="Profile, contact and bank fields show their destination below. Monthly salary amounts and source attendance are batch-only; they do not change monthly CTC or create daily attendance. Blank cells keep current values. Enter 0 only for an intentional numeric change. Saving sends no email." />
    {loading ? <div role="status" aria-live="polite"><Space><Spin /><span>Reading saved update fields...</span></Space></div> : !catalog ? <Alert type="error" showIcon message={error || 'Update fields are unavailable.'} action={<Button onClick={() => { setLoading(true); setReload(value => value + 1) }}>Retry</Button>} /> : <>
      {catalog.blockers.map((message, index) => <Alert key={index} type="error" showIcon message={message} />)}
      {error && <Alert type="error" showIcon message={error} />}
      <Space wrap className="excel-batch-update-controls"><Button disabled={locked || !!catalog.blockers.length} onClick={() => downloadXlsx(`${(batch.clientName || 'client').replace(/[^a-z0-9 -]/gi, '').trim()}-${batch.month}-update.xlsx`, payslipUpdateTemplate(catalog))}>Download existing data template</Button><Tag>{catalog.rows.length} saved employees</Tag><Tag>No new batch or employees</Tag></Space>
      <FileDropZone accept=".xlsx,.csv" fileName={fileName} title="Upload updates for this saved batch" hint="Use the downloaded template or map your spreadsheet. Keep existing employee codes unchanged." onFile={file => { if (!locked) void upload(file) }} />
      {sheet && <>
        <div className="excel-batch-update-fields"><label>Worksheet<Select aria-label="Update worksheet" value={sheetName} disabled={locked} options={sheets.map(value => ({ value: value.name, label: value.name }))} onChange={name => { const next = sheets.find(value => value.name === name); if (next) setSource(next) }} /></label><label>Header row<InputNumber aria-label="Update header row" min={1} max={100000} value={headerRow} disabled={locked} onChange={value => { if (value) setSource(sheet, value) }} /></label><label>Employee code column<Select aria-label="Update employee code column" value={codeColumn} disabled={locked} placeholder="Choose employee code" options={headers.map((header, index) => ({ value: index, label: `${excelColumnLetter(index)} · ${header || 'No header'}` }))} onChange={value => { setCodeColumn(value); resetReview() }} /></label></div>
        {unmapped.length > 0 && <Alert type="warning" showIcon message={`${unmapped.length} uploaded column(s) are not mapped`} description={<><div>These columns will not update any data: {unmapped.join(', ')}. Review column mapping below if they should be included.</div><Checkbox checked={ignoreAcknowledged} disabled={locked} onChange={event => { setIgnoreAcknowledged(event.target.checked); setAcknowledged(false) }}>I confirm these unmapped columns should be ignored.</Checkbox></>} />}
        <Collapse><Collapse.Panel key="mapping" header="Review column mapping and destinations"><DataTable rows={headers.map((header, index) => ({ id: index, header }))} getRowId={row => row.id} title="Uploaded fields" exportDisabled columns={[{ key: 'header', label: 'Uploaded column', value: row => `${excelColumnLetter(row.id)} · ${row.header || 'No header'}` }, { key: 'field', label: 'Update field', render: row => row.id === codeColumn ? <Tag>Employee code · matching only</Tag> : <Select aria-label={`Update column ${excelColumnLetter(row.id)}`} allowClear placeholder="Ignore this column" value={mapping[row.id] || undefined} disabled={locked} style={{ width: '100%', minWidth: 180 }} options={catalog.fields.map(field => ({ value: field.id, label: `${field.label}${field.editable ? '' : ' (read-only)'}`, disabled: !field.editable }))} onChange={value => { setMapping(current => { const next = { ...current }; if (value) next[row.id] = value; else delete next[row.id]; return next }); resetReview() }} /> }, { key: 'destination', label: 'Destination', value: row => catalog.fields.find(field => field.id === mapping[row.id])?.destination || 'Not updated' }, { key: 'reason', label: 'Rule', wrap: true, value: row => catalog.fields.find(field => field.id === mapping[row.id])?.reason || '' }]} /></Collapse.Panel></Collapse>
        {parsed && <>
          <Space wrap className="excel-batch-update-controls"><Tag>{parsed.rows.length} employees with proposed changes</Tag><Tag>{parsed.blankCells} blank values kept</Tag><Tag>{parsed.unchangedCells} unchanged</Tag>{excluded.length > 0 && <><Tag>{parsed.excludedRows} source rows excluded</Tag><Button disabled={locked} onClick={() => { setExcluded([]); resetReview() }}>Restore excluded rows</Button></>}<Button type="primary" disabled={locked || !parsed.rows.length || !!parsed.issues.length || !!catalog.blockers.length || !!unmapped.length && !ignoreAcknowledged} onClick={() => void review(parsed.rows)}>{preview ? 'Review all uploaded changes' : 'Preview changes'}</Button></Space>
          {parsed.issues.length > 0 && <DataTable rows={parsed.issues} getRowId={row => row.id} title="Fix or explicitly exclude these source rows" exportDisabled columns={[{ key: 'sourceRow', label: 'Excel row' }, { key: 'column', label: 'Field' }, { key: 'message', label: 'Issue', wrap: true }]} actions={row => row.sourceRow > headerRow && <Button size="small" disabled={locked} onClick={() => { setExcluded(current => [...new Set([...current, row.sourceRow])]); resetReview() }}>Exclude source row</Button>} />}
        </>}
      </>}
      {preview && <>
        {preview.blockers.map((message, index) => <Alert key={index} type="error" showIcon message={message} />)}
        <Space wrap className="excel-batch-update-controls"><Button disabled={locked} onClick={() => selectRows(preview.changes.filter(change => change.canApply && !change.computed).map(change => change.id))}>Select all changes</Button><Button disabled={locked} onClick={() => selectRows(visibleChangeIds)}>Select filtered changes</Button><Button disabled={locked} onClick={() => selectRows([])}>Clear selection</Button><Tag>{selectedChanges.length} input changes selected</Tag>{computedChanges.length > 0 && <Tag>{computedChanges.length} calculated impacts</Tag>}<Button disabled={locked || !selectedChanges.length || exactReview && !preview.blockers.length && !blockedChanges.length} onClick={() => void review(selectedPayslipUpdateRows(preview, selected))}>Review selected changes</Button></Space>
        {!exactReview && <Alert type="warning" showIcon message="Review selected changes again" description="Selection changed. The server must recalculate the selected inputs and show their dependent totals before you can save." />}
        <DataTable rows={preview.changes} getRowId={row => row.id} title="Old and new values" exportDisabled onVisibleRowsChange={captureVisibleChanges} rowSelection={{ selectedRowKeys: selected, onChange: keys => selectRows(keys.map(String)), getCheckboxProps: row => ({ disabled: locked || !row.canApply || row.computed === true }) }} columns={[{ key: 'employeeCode', label: 'Employee code' }, { key: 'employeeName', label: 'Employee' }, { key: 'label', label: 'Field' }, { key: 'oldValue', label: 'Current value', wrap: true, value: row => row.oldValue || 'Not set' }, { key: 'newValue', label: 'New value', wrap: true }, { key: 'destination', label: 'Destination', render: row => <Tag color={row.destination === 'Batch only' ? undefined : 'blue'}>{row.destination}</Tag> }, { key: 'reason', label: 'Review', wrap: true, value: row => `${row.computed ? 'Calculated impact. ' : !row.canApply ? 'Blocked. ' : ''}${row.reason || ''}` }]} />
        {zeroChanges.length > 0 && <Checkbox checked={zeroAcknowledged} disabled={locked || !exactReview} onChange={event => { setZeroAcknowledged(event.target.checked); setAcknowledged(false) }}>I confirm {zeroChanges.length} selected numeric value(s) should become zero.</Checkbox>}
        <Checkbox checked={acknowledged} disabled={locked || !exactReview || !selectedChanges.length || !!preview.blockers.length || !!blockedChanges.length} onChange={event => setAcknowledged(event.target.checked)}>I reviewed these exact changes and calculated impacts for {batch.month}, including the fields updating Employee Master. Keep this batch's row identities and delivery history.</Checkbox>
      </>}
    </>}
  </Drawer>
}
