import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Descriptions, Divider, Drawer, Empty, Form, Input, InputNumber, Segmented, Select, Space, Spin, Tag } from 'antd'
import { FilePdfOutlined, HistoryOutlined, UploadOutlined } from '@ant-design/icons'
import { useSessionPreference } from '../hooks/useRecruitmentPreferences'
import { buildExcelPayslipRows, defaultPayslipMapping, excelColumnLetter, formatExcelPayslipAmount, inferPayslipMonth, parseExcelPayslipFile, payslipColumnKinds, payslipHeaders, payslipHeaderSignature, type PayslipColumn, type PayslipColumnKind, type PayslipSheet } from '../utils/excelPayslipImport'
import { getExcelPayslipBatch, getExcelPayslipClients, getExcelPayslipHistory, getExcelPayslipPdf, getExcelPayslipProfile, getExcelPayslipTemplates, saveExcelPayslipBatch, saveExcelPayslipProfile, sendExcelPayslips, type ExcelPayslipBatch, type ExcelPayslipDelivery, type ExcelPayslipRow, type ExcelPayslipSendRequest, type ExcelPayslipSummary, type ExcelPayslipTemplate } from '../services/excelPayslipService'
import { useAuthSession } from './AuthGate'
import DataTable from './DataTable'
import FileDropZone from './FileDropZone'
import { PageHeaderPortal } from './layout/AppPageHeader'
import './ExcelPayslips.css'

type Action = { kind: 'preview' | 'download' | 'send'; rowIds: string[] }
type SendChunk = { request: ExcelPayslipSendRequest; completed: boolean }
const amount = formatExcelPayslipAmount
const sum = (items: { amount: number }[]) => items.reduce((total, item) => total + item.amount, 0)
const emailValid = (value: string) => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value)
const categoryKind = (category: string): PayslipColumnKind => ({ Earning: 'earning', Deduction: 'deduction', Employer: 'employer', Information: 'info' }[category] as PayslipColumnKind) || 'info'
const batchSummary = ({ rows, ...batch }: ExcelPayslipBatch): ExcelPayslipSummary => ({ ...batch, rowCount: rows.length })

export default function ExcelPayslips() {
  const session = useAuthSession()
  const canManage = Boolean(session?.user.permissions.some(permission => ['payroll.run', 'payroll.approve', 'payroll.payments'].includes(permission)))
  const [clients, setClients] = useState<{ id: number; name: string }[]>([])
  const [preferredClient, setPreferredClient] = useSessionPreference(`excel-payslips:${session?.user.id}:client`, 0)
  const clientId = clients.find(client => client.id === preferredClient)?.id || clients[0]?.id || 0
  const [view, setView] = useSessionPreference<'Table' | 'Cards'>(`excel-payslips:${session?.user.id}:${clientId}:view`, 'Table')
  const [lastBatchId, setLastBatchId] = useSessionPreference(`excel-payslips:${session?.user.id}:${clientId}:batch`, '')
  const [selectedIds, setSelectedIds] = useSessionPreference<string[]>(`excel-payslips:${session?.user.id}:${clientId}:${lastBatchId}:selection`, [])
  const [history, setHistory] = useState<ExcelPayslipSummary[]>([]), [templates, setTemplates] = useState<ExcelPayslipTemplate[]>([])
  const [batch, setBatch] = useState<ExcelPayslipBatch | null>(null)
  const [loading, setLoading] = useState(true), [busy, setBusy] = useState(false), [error, setError] = useState(''), [notice, setNotice] = useState('')
  const [historyOpen, setHistoryOpen] = useState(false), [importOpen, setImportOpen] = useState(false)
  const [sheets, setSheets] = useState<PayslipSheet[]>([]), [sheetName, setSheetName] = useState(''), [fileName, setFileName] = useState('')
  const [headerRow, setHeaderRow] = useState(5), [month, setMonth] = useState(''), [templateId, setTemplateId] = useState('')
  const [columns, setColumns] = useState<PayslipColumn[]>([]), [signature, setSignature] = useState(''), [profileLoading, setProfileLoading] = useState(false)
  const [excludedRows, setExcludedRows] = useState<number[]>([]), [importError, setImportError] = useState(''), [mappingNotice, setMappingNotice] = useState('')
  const [review, setReview] = useState<ExcelPayslipRow | null>(null)
  const [action, setAction] = useState<Action | null>(null), [acknowledgeWarnings, setAcknowledgeWarnings] = useState(false), [includeSeal, setIncludeSeal] = useState(true)
  const [amountDecimalPlaces, setAmountDecimalPlaces] = useState<0 | 2>(0)
  const [sendMode, setSendMode] = useState<'Individual' | 'Combined'>('Individual'), [combinedEmail, setCombinedEmail] = useState(''), [emailOverrides, setEmailOverrides] = useState<Record<string, string>>({})
  const [actionError, setActionError] = useState(''), [pdfUrl, setPdfUrl] = useState(''), [delivery, setDelivery] = useState<ExcelPayslipDelivery[]>([]), [sendProgress, setSendProgress] = useState(''), [pendingSendIds, setPendingSendIds] = useState<string[]>([])
  const iframe = useRef<HTMLIFrameElement>(null), scope = useRef(0), profileSequence = useRef(0), inFlight = useRef(false)
  const sendPlan = useRef<{ key: string; chunks: SendChunk[] } | null>(null)
  const currentClient = useRef(clientId)
  const sheet = sheets.find(item => item.name === sheetName)
  const headers = useMemo(() => sheet ? payslipHeaders(sheet, headerRow) : [], [sheet, headerRow])
  const imported = useMemo(() => sheet ? buildExcelPayslipRows(sheet, headerRow, columns, excludedRows) : { rows: [], issues: [], excludedRows: [], excludedDetails: [] }, [sheet, headerRow, columns, excludedRows])
  const template = templates.find(item => String(item.id) === templateId)
  const rows = batch?.clientId === clientId ? batch.rows : []
  const validSelection = selectedIds.filter(id => rows.some(row => row.id === id))
  const actionRows = action ? rows.filter(row => action.rowIds.includes(row.id)) : []
  const warningRows = actionRows.filter(row => row.warnings.length)
  const needsAck = warningRows.length > 0 && !acknowledgeWarnings
  const missingEmailRows = actionRows.filter(row => !emailValid((emailOverrides[row.id] ?? row.email).trim()))
  useEffect(() => { currentClient.current = clientId }, [clientId])

  useEffect(() => {
    let active = true
    void getExcelPayslipClients().then(result => { if (!active) return; setClients(result.data); if (!result.ok) setError(result.error); setLoading(false) })
    return () => { active = false }
  }, [])
  useEffect(() => {
    const token = ++scope.current
    void Promise.resolve().then(async () => {
      if (token !== scope.current) return
      setBatch(null); setHistory([]); setTemplates([]); setAction(null); setImportOpen(false); setReview(null); setHistoryOpen(false); setError(''); setNotice(''); setSheets([])
      if (!clientId) return
      setLoading(true)
      const [historyResult, templateResult] = await Promise.all([getExcelPayslipHistory(clientId), getExcelPayslipTemplates(clientId)])
      if (token !== scope.current) return
      if (historyResult.ok) setHistory(historyResult.data)
      else setError(historyResult.error || 'Unable to load batch history.')
      if (templateResult.ok) setTemplates(templateResult.data)
      else setError(templateResult.error || 'Unable to load salary templates.')
      if (lastBatchId && historyResult.data.some(item => item.id === lastBatchId)) {
        const result = await getExcelPayslipBatch(clientId, lastBatchId)
        if (token !== scope.current) return
        if (result.ok && result.data) setBatch(result.data)
        else setError(result.error || 'Unable to reopen the saved batch.')
      }
      setLoading(false)
    })
    return () => { scope.current = token + 1 }
    // Client scope changes reset the workspace; the last batch is read once for that scope.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [clientId])
  useEffect(() => {
    let active = true
    const sequence = ++profileSequence.current
    void Promise.resolve().then(async () => {
      if (!active) return
      setColumns(defaultPayslipMapping(headers)); setSignature(''); setExcludedRows([]); setMappingNotice(''); setTemplateId('')
      if (!headers.length || !clientId) return
      setProfileLoading(true)
      try {
        const nextSignature = await payslipHeaderSignature(headers)
        const result = await getExcelPayslipProfile(clientId, nextSignature)
        if (!active || sequence !== profileSequence.current) return
        setSignature(nextSignature)
        if (result.ok && result.data?.headerSignature === nextSignature && Array.isArray(result.data.columns) && result.data.columns.length === headers.length && result.data.columns.every((column, index) => column.columnIndex === index && column.sourceHeader === headers[index] && payslipColumnKinds.some(kind => kind.value === column.kind))) {
          setColumns(result.data.columns); setTemplateId(result.data.salaryTemplateId || ''); setMappingNotice('Saved mapping restored for these column headings.')
        } else if (!result.ok && result.status !== 404) setImportError(result.error || 'Saved mapping could not be loaded. Review the default mapping before continuing.')
      } catch (failure) { if (active) setImportError(failure instanceof Error ? failure.message : 'Unable to prepare column mapping.') }
      finally { if (active) setProfileLoading(false) }
    })
    return () => { active = false }
  }, [headers, clientId])
  useEffect(() => { return () => { if (pdfUrl) URL.revokeObjectURL(pdfUrl) } }, [pdfUrl])

  const upload = async (file: File) => {
    if (!canManage || busy) return
    setBusy(true); setImportError(''); setMappingNotice('')
    try {
      const parsed = await parseExcelPayslipFile(file)
      const selected = parsed.find(item => item.name.toLowerCase() === 'calcultion') || parsed[0]
      const proposedHeader = selected.rows.find(row => row.cells.some(cell => cell?.value.toLowerCase().trim() === 'name of the person'))?.sourceRow || selected.rows.find(row => row.cells.some(cell => cell?.value.trim()))?.sourceRow || 1
      setSheets(parsed); setSheetName(selected.name); setHeaderRow(proposedHeader); setMonth(inferPayslipMonth(selected)); setFileName(file.name)
    } catch (failure) { setImportError(failure instanceof Error ? failure.message : 'Unable to read this file.'); setSheets([]) }
    finally { setBusy(false) }
  }
  const patchColumn = (index: number, patch: Partial<PayslipColumn>) => setColumns(current => current.map(column => column.columnIndex === index ? { ...column, ...patch } : column))
  const saveMapping = async () => {
    if (!canManage || !clientId || !signature || busy) return
    setBusy(true); setImportError(''); setMappingNotice('')
    const result = await saveExcelPayslipProfile(clientId, { headerSignature: signature, columns, salaryTemplateId: templateId || undefined })
    setBusy(false)
    if (result.ok) setMappingNotice('Mapping saved for this client and these column headings.')
    else setImportError(result.error || 'Unable to save mapping.')
  }
  const saveBatch = async () => {
    if (!canManage || !clientId || busy || profileLoading) return
    if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(month)) return setImportError('Choose the salary month from the source before saving.')
    if (imported.issues.length || !imported.rows.length) return setImportError('Resolve the import issues before saving this batch.')
    if (templateId && !template) return setImportError('Choose a currently available client salary template, or manual mapping.')
    setBusy(true); setImportError('')
    const result = await saveExcelPayslipBatch(clientId, { month, sourceFileName: fileName, sheetName, headerRow, salaryTemplateId: templateId || undefined, salaryTemplateName: template?.name, rows: imported.rows.map(row => ({ ...row, id: crypto.randomUUID() })) })
    setBusy(false)
    if (!result.ok || !result.data) return setImportError(result.error || 'Unable to save this batch. Check batch history before retrying an interrupted request.')
    setBatch(result.data); setHistory(current => [batchSummary(result.data!), ...current.filter(item => item.id !== result.data!.id)]); setLastBatchId(result.data.id); setSelectedIds([]); setImportOpen(false); setNotice(`${result.data.rows.length} payslips saved. Saved batches retain their imported amounts.`)
  }
  const openHistoryBatch = async (id: string) => {
    if (busy) return
    const requestedClient = clientId
    setBusy(true); setError('')
    const result = await getExcelPayslipBatch(requestedClient, id)
    setBusy(false)
    if (currentClient.current !== requestedClient) return
    if (result.ok && result.data) { setBatch(result.data); setLastBatchId(result.data.id); setSelectedIds([]); setHistoryOpen(false); setNotice('') }
    else setError(result.error || 'Unable to open this saved batch.')
  }
  const beginAction = (kind: Action['kind'], rowIds: string[]) => {
    if (!batch || !rowIds.length || busy) return
    setAction({ kind, rowIds }); setAcknowledgeWarnings(false); setIncludeSeal(true); setAmountDecimalPlaces(0); setSendMode('Individual'); setCombinedEmail(''); setEmailOverrides({}); setActionError(''); setPdfUrl(''); setDelivery([]); setSendProgress(''); setPendingSendIds([]); sendPlan.current = null
  }
  const downloadBlob = (blob: Blob) => {
    const url = URL.createObjectURL(blob), anchor = document.createElement('a')
    anchor.href = url; anchor.download = `payslips-${batch?.month || 'batch'}-${actionRows.length}.pdf`; anchor.click(); window.setTimeout(() => URL.revokeObjectURL(url), 1000)
  }
  const createPdf = async () => {
    if (!batch || !action || needsAck || inFlight.current || !actionRows.length) return
    inFlight.current = true; setBusy(true); setActionError('')
    try {
      const result = await getExcelPayslipPdf(clientId, batch.id, { rowIds: action.rowIds, includeSeal, acknowledgeWarnings, amountDecimalPlaces })
      if (!result.ok || !result.data) return setActionError(result.error || 'Unable to create PDF.')
      if (action.kind === 'download') downloadBlob(result.data)
      setPdfUrl(URL.createObjectURL(result.data))
    } finally { inFlight.current = false; setBusy(false) }
  }
  const send = async () => {
    if (!canManage || !batch || !action || needsAck || inFlight.current || !actionRows.length) return
    if (sendMode === 'Combined' && !emailValid(combinedEmail.trim())) return setActionError('Enter a valid recipient email for the combined PDF.')
    if (sendMode === 'Individual' && actionRows.length === 1 && missingEmailRows.length) return setActionError('Enter a valid email before sending this payslip. PDF preview and download remain available.')
    const base = { includeSeal, acknowledgeWarnings, amountDecimalPlaces, mode: sendMode, email: combinedEmail.trim(), emailOverrides: Object.fromEntries(Object.entries(emailOverrides).map(([id, value]) => [id, value.trim()])) }
    const handledIds = new Set(delivery.filter(item => item.status === 'Queued' || item.status === 'Already queued').map(item => item.rowId))
    const eligibleIds = action.rowIds.filter(id => !handledIds.has(id))
    if (!eligibleIds.length) return setActionError('All selected payslips are already queued in this send. Close this dialog to start a separate send.')
    const chunks: SendChunk[] = sendPlan.current?.chunks.filter(chunk => !chunk.completed && chunk.request.rowIds.some(id => pendingSendIds.includes(id))) || []
    const retainedIds = new Set(chunks.flatMap(chunk => chunk.request.rowIds))
    const freshIds = eligibleIds.filter(id => !retainedIds.has(id))
    const size = sendMode === 'Combined' ? Math.max(1, freshIds.length) : 25
    for (let index = 0; index < freshIds.length; index += size) {
      const rowIds = freshIds.slice(index, index + size)
      chunks.push({ request: { ...base, rowIds, emailOverrides: Object.fromEntries(Object.entries(base.emailOverrides).filter(([id]) => rowIds.includes(id))), requestId: crypto.randomUUID() }, completed: false })
    }
    sendPlan.current = { key: batch.id, chunks }
    inFlight.current = true; setBusy(true); setActionError('')
    try {
      for (const [index, chunk] of sendPlan.current.chunks.entries()) {
        if (chunk.completed) continue
        setSendProgress(`Sending group ${index + 1} of ${sendPlan.current.chunks.length}`)
        const result = await sendExcelPayslips(clientId, batch.id, chunk.request)
        if (!result.ok) { setPendingSendIds(chunk.request.rowIds); setActionError(`${result.error || 'Email request failed.'} Retry keeps this request reference so already queued mail is not duplicated. Pending recipients and delivery options stay locked until the response is resolved.`); break }
        chunk.completed = true
        setPendingSendIds(current => current.filter(id => !chunk.request.rowIds.includes(id)))
        setDelivery(current => [...current.filter(item => !chunk.request.rowIds.includes(item.rowId)), ...result.data.items])
      }
    } finally { inFlight.current = false; setBusy(false); setSendProgress('') }
  }

  return <section className="excel-payslips recruitment-viewport" aria-label="Excel payslips workspace">
    <PageHeaderPortal slot="excel-payslips-client-controls">{clients.length === 1 ? <span className="scoped-client-chip">{clients[0].name}</span> : <Select aria-label="Excel payslips client" value={clientId || undefined} disabled={busy || loading} placeholder="Select client" options={clients.map(client => ({ value: client.id, label: client.name }))} onChange={setPreferredClient} style={{ width: 240, maxWidth: '100%' }} />}</PageHeaderPortal>
    <PageHeaderPortal slot="excel-payslips-view-controls"><Segmented aria-label="Excel payslips view" options={['Table', 'Cards']} value={view} onChange={value => setView(value as 'Table' | 'Cards')} /></PageHeaderPortal>
    <PageHeaderPortal slot="excel-payslips-page-controls"><Space wrap>
      <Button icon={<HistoryOutlined />} disabled={!clientId || loading || busy} onClick={() => setHistoryOpen(true)}>Batch history</Button>
      {canManage && <Button type="primary" icon={<UploadOutlined />} disabled={!clientId || loading || busy} onClick={() => { setImportOpen(true); setImportError('') }}>Import spreadsheet</Button>}
      <Button icon={<FilePdfOutlined />} disabled={!rows.length || busy} onClick={() => beginAction('download', validSelection.length ? validSelection : rows.map(row => row.id))}>{validSelection.length ? `PDF selected (${validSelection.length})` : 'PDF all'}</Button>
      {canManage && <Button disabled={!rows.length || busy} onClick={() => beginAction('send', validSelection.length ? validSelection : rows.map(row => row.id))}>{validSelection.length ? `Email selected (${validSelection.length})` : 'Email batch'}</Button>}
    </Space></PageHeaderPortal>
    {error && <Alert type="error" showIcon message={error} />}
    {notice && <Alert type="success" showIcon closable message={notice} />}
    {batch && <div className="excel-payslip-summary"><Space wrap><strong>{batch.month}</strong><span>{batch.sourceFileName}</span><Tag>{rows.length} payslips</Tag><Tag color={rows.some(row => row.warnings.length) ? 'orange' : 'green'}>{rows.filter(row => row.warnings.length).length} with warnings</Tag></Space><Space wrap><Button size="small" disabled={busy} onClick={() => setSelectedIds(rows.map(row => row.id))}>Select all {rows.length}</Button><Button size="small" disabled={!validSelection.length || busy} onClick={() => setSelectedIds([])}>Clear selection</Button></Space></div>}
    {loading ? <div className="excel-payslip-empty"><Spin tip="Loading Excel payslips..." /></div> : !rows.length ? <div className="excel-payslip-empty"><Empty description={clients.length ? 'Import a spreadsheet or reopen a saved batch.' : 'No authorized clients are available.'} /></div> : <DataTable key={`${clientId}:${batch?.id}`} rows={rows} fillHeight view={view} title="Imported payslips" getRowId={row => row.id} exportFileName={`excel-payslips-${batch?.month}`} rowSelection={{ selectedRowKeys: validSelection, onChange: keys => setSelectedIds(keys.map(String)), getCheckboxProps: () => ({ disabled: busy }) }} columns={[
      { key: 'sourceRow', label: 'Source row', width: 105 }, { key: 'employeeName', label: 'Employee', width: 200 }, { key: 'employeeCode', label: 'Employee code' },
      { key: 'earnings', label: 'Earnings', value: row => sum(row.earnings), render: row => amount(sum(row.earnings)) }, { key: 'deductions', label: 'Deductions', value: row => sum(row.deductions), render: row => amount(sum(row.deductions)) }, { key: 'netPay', label: 'Net pay as stated', render: row => amount(row.netPay) },
      { key: 'warnings', label: 'Review', value: row => row.warnings.join('; ') || 'No warnings', render: row => <Tag color={row.warnings.length ? 'orange' : 'green'}>{row.warnings.length ? `${row.warnings.length} warnings` : 'No warnings'}</Tag> },
      { key: 'email', label: 'Email', render: row => row.email || <span className="excel-payslip-muted">Optional · add when sending</span> },
    ]} actionsWidth={240} actions={row => <Space wrap size={4}><Button size="small" disabled={busy} onClick={() => setReview(row)}>Details</Button><Button size="small" disabled={busy} onClick={() => beginAction('preview', [row.id])}>Preview</Button>{canManage && <Button size="small" disabled={busy} onClick={() => beginAction('send', [row.id])}>Email</Button>}</Space>} />}

    <Drawer title="Import Excel payslips" open={importOpen} width="min(1120px, 98vw)" onClose={() => { if (!busy) setImportOpen(false) }} closable={!busy} maskClosable={!busy} className="excel-payslip-import" footer={<Space wrap className="excel-payslip-footer"><Button disabled={busy} onClick={() => setImportOpen(false)}>Cancel</Button><Button disabled={!signature || busy || profileLoading} onClick={() => void saveMapping()}>Save mapping</Button><Button type="primary" loading={busy} disabled={!canManage || !imported.rows.length || !!imported.issues.length || profileLoading} onClick={() => void saveBatch()}>Save batch ({imported.rows.length})</Button></Space>}>
      <Form component="div" layout="vertical" disabled={busy}>
        <FileDropZone accept=".xlsx,.csv" title="Upload salary spreadsheet" hint="XLSX or CSV, up to 30 MB" fileName={fileName} onFile={file => { if (!busy) void upload(file) }} />
        {importError && <Alert type="error" showIcon message={importError} />}{mappingNotice && <Alert type="success" showIcon message={mappingNotice} />}
        {sheet && <><div className="excel-payslip-form-grid"><Form.Item label="Worksheet"><Select aria-label="Payslip worksheet" value={sheetName} options={sheets.map(item => ({ value: item.name, label: item.name }))} onChange={value => { setSheetName(value); setMonth(inferPayslipMonth(sheets.find(item => item.name === value)!)) }} /></Form.Item><Form.Item label="Header row"><InputNumber aria-label="Payslip header row" min={1} max={100000} value={headerRow} onChange={value => setHeaderRow(value || 1)} /></Form.Item><Form.Item label="Salary month" required><Input aria-label="Payslip salary month" type="month" value={month} onChange={event => setMonth(event.target.value)} /></Form.Item></div>
          <Form.Item label="Client salary template"><Select aria-label="Payslip salary template" value={templateId || ''} options={[{ value: '', label: 'Manual mapping' }, ...templates.map(item => ({ value: String(item.id), label: item.name }))]} onChange={value => { setTemplateId(value); setColumns(current => current.map(column => ({ ...column, componentId: undefined }))) }} /></Form.Item>
          <Alert type="info" showIcon message="Review the source, then map columns to salary-template components." description="Component names and categories label the payslip. All amounts come from saved spreadsheet values; template formulas are not recalculated. Serial numbers are row references, not employee codes." />
          <div className="excel-payslip-mapping"><DataTable rows={columns} loading={profileLoading} title="Column mapping" getRowId={column => column.columnIndex} hideSearch exportDisabled pageSizeOptions={[10, 25, 50]} columns={[
            { key: 'sourceHeader', label: 'Source column', width: 210, render: column => <><strong>{excelColumnLetter(column.columnIndex)}</strong> · {column.sourceHeader || '(blank heading)'}</> },
            { key: 'sample', label: 'First employee value', width: 170, value: column => sheet.rows.find(row => row.sourceRow > headerRow && row.cells.some(cell => cell?.value))?.cells[column.columnIndex]?.value || '' },
            { key: 'componentId', label: 'Template component', width: 240, filterable: false, sortable: false, render: column => <Select aria-label={`Column ${excelColumnLetter(column.columnIndex)} template component`} value={column.componentId || ''} disabled={!template || profileLoading} showSearch optionFilterProp="label" options={[{ value: '', label: 'Manual / not mapped' }, ...(template?.components || []).map(component => ({ value: String(component.id), label: `${component.code} · ${component.name} (${component.category})` }))]} onChange={value => { const component = template?.components.find(item => String(item.id) === value); patchColumn(column.columnIndex, component ? { componentId: value, label: component.name, kind: categoryKind(component.category) } : { componentId: undefined }) }} style={{ width: '100%' }} /> },
            { key: 'kind', label: 'Use as', width: 205, filterable: false, sortable: false, render: column => <Select aria-label={`Column ${excelColumnLetter(column.columnIndex)} use as`} value={column.kind} disabled={profileLoading} options={payslipColumnKinds} onChange={kind => patchColumn(column.columnIndex, { kind, componentId: undefined })} style={{ width: '100%' }} /> },
            { key: 'label', label: 'Payslip label', width: 205, filterable: false, sortable: false, render: column => <Input aria-label={`Column ${excelColumnLetter(column.columnIndex)} label`} value={column.label} maxLength={80} disabled={profileLoading} onChange={event => patchColumn(column.columnIndex, { label: event.target.value })} /> },
          ]} /></div>
          <Divider />
          <Space wrap><Tag color="blue">{imported.rows.length} employee rows</Tag><Tag>{imported.excludedRows.length} blank-name / total / excluded rows</Tag><Tag color={imported.issues.length ? 'red' : 'green'}>{imported.issues.length} import issues</Tag></Space>
          {!!imported.excludedRows.length && <p className="excel-payslip-muted">Excluded source rows: {imported.excludedRows.slice(0, 30).join(', ')}{imported.excludedRows.length > 30 ? '…' : ''}. Empty formatting rows are omitted.</p>}
          {!!imported.excludedDetails.length && <DataTable rows={imported.excludedDetails} title="Excluded row review" getRowId={row => row.sourceRow} exportDisabled columns={[{ key: 'sourceRow', label: 'Source row', width: 100 }, { key: 'reason', label: 'Reason', wrap: true }, { key: 'amounts', label: 'Saved values in mapped amount columns', wrap: true }]} />}
          {imported.issues.length > 0 && <DataTable rows={imported.issues} title="Resolve before saving" getRowId={(item, index) => `${item.sourceRow}:${item.column}:${index}`} exportDisabled columns={[{ key: 'sourceRow', label: 'Source row' }, { key: 'column', label: 'Column' }, { key: 'message', label: 'Issue', wrap: true }]} />}
          <DataTable rows={imported.rows} title="Employee preview" getRowId={row => row.id} exportDisabled columns={[{ key: 'sourceRow', label: 'Source row' }, { key: 'employeeName', label: 'Employee' }, { key: 'employeeCode', label: 'Employee code' }, { key: 'netPay', label: 'Net pay as stated', render: row => amount(row.netPay) }]} actions={row => <Space size={4}><Button size="small" onClick={() => setReview(row)}>Details</Button><Button size="small" danger onClick={() => setExcludedRows(current => [...current, row.sourceRow])}>Exclude row</Button></Space>} />
          {!!excludedRows.length && <Button onClick={() => setExcludedRows([])}>Restore manually excluded rows</Button>}
        </>}
      </Form>
    </Drawer>

    <Drawer title="Saved Excel payslip batches" open={historyOpen} width="min(940px, 96vw)" onClose={() => { if (!busy) setHistoryOpen(false) }}><DataTable rows={history} getRowId={row => row.id} title="Batch history" emptyText="No saved batches for this client." columns={[{ key: 'month', label: 'Salary month' }, { key: 'sourceFileName', label: 'Source file' }, { key: 'sheetName', label: 'Worksheet' }, { key: 'rowCount', label: 'Payslips' }, { key: 'createdAtUtc', label: 'Saved at', value: row => new Date(row.createdAtUtc).toLocaleString() }, { key: 'createdBy', label: 'Saved by' }]} actions={row => <Button size="small" loading={busy} onClick={() => void openHistoryBatch(row.id)}>Open batch</Button>} /></Drawer>
    <Drawer title={review ? `${review.employeeName} · Source row ${review.sourceRow}` : 'Payslip details'} open={!!review} width="min(720px, 96vw)" onClose={() => setReview(null)}>{review && <><Descriptions bordered column={1} size="small"><Descriptions.Item label="Employee code">{review.employeeCode || 'Not supplied'}</Descriptions.Item>{review.information.map((item, index) => <Descriptions.Item key={index} label={item.label}>{item.value || '—'}</Descriptions.Item>)}</Descriptions>{[['Earnings', review.earnings], ['Deductions', review.deductions], ['Employer contributions', review.employerContributions]].map(([label, items]) => <div key={String(label)}><Divider orientation="left">{String(label)}</Divider>{(items as { label: string; amount: number }[]).map((item, index) => <p className="excel-payslip-amount" key={index}><span>{item.label}</span><strong>{amount(item.amount)}</strong></p>)}</div>)}<Divider /><p className="excel-payslip-amount"><strong>Net pay as stated</strong><strong>{amount(review.netPay)}</strong></p>{review.warnings.map((warning, index) => <Alert key={index} type="warning" showIcon message={warning} />)}</>}</Drawer>

    <Drawer title={action?.kind === 'send' ? 'Email imported payslips' : 'Payslip PDF'} open={!!action} width="min(960px, 98vw)" onClose={() => { if (!busy) { setAction(null); setPdfUrl('') } }} closable={!busy} maskClosable={!busy} className="excel-payslip-action" footer={<Space wrap className="excel-payslip-footer"><Button disabled={busy} onClick={() => { setAction(null); setPdfUrl('') }}>Close</Button>{pdfUrl && <><Button onClick={() => iframe.current?.contentWindow?.print()}>Print PDF</Button><Button href={pdfUrl} download={`payslips-${batch?.month}.pdf`}>Download PDF</Button></>}{action?.kind === 'send' ? <Button type="primary" loading={busy} disabled={!canManage || needsAck || !actionRows.length} onClick={() => void send()}>{sendProgress || (delivery.length ? 'Retry / check email request' : 'Send email')}</Button> : <Button type="primary" loading={busy} disabled={needsAck || !actionRows.length} onClick={() => void createPdf()}>{action?.kind === 'download' ? 'Generate & download PDF' : 'Generate preview'}</Button>}</Space>}>
      <Space wrap><Tag>{actionRows.length} payslips</Tag><Tag>{batch?.month}</Tag><Checkbox checked={includeSeal} disabled={busy || pendingSendIds.length > 0} onChange={event => { setIncludeSeal(event.target.checked); setPdfUrl('') }}>Include company seal</Checkbox></Space>
      <Form.Item label="Amount display" style={{ marginTop: 16 }}><Select aria-label="Payslip amount display" value={amountDecimalPlaces} disabled={busy || pendingSendIds.length > 0} options={[{ value: 0, label: 'Whole rupees' }, { value: 2, label: '2 decimal places' }]} onChange={value => { setAmountDecimalPlaces(value); setPdfUrl('') }} style={{ width: 210 }} /></Form.Item>
      {actionError && <Alert type="error" showIcon message={actionError} />}
      {!!warningRows.length && <><Alert type="warning" showIcon message={`${warningRows.length} payslips have financial review warnings. The imported net pay remains unchanged.`} /><DataTable rows={warningRows} getRowId={row => row.id} exportDisabled columns={[{ key: 'sourceRow', label: 'Source row' }, { key: 'employeeName', label: 'Employee' }, { key: 'warnings', label: 'Warnings', value: row => row.warnings.join('; '), wrap: true }]} /><Checkbox checked={acknowledgeWarnings} disabled={busy} onChange={event => setAcknowledgeWarnings(event.target.checked)}>I reviewed these warnings and want to use the imported amounts.</Checkbox></>}
      {action?.kind === 'send' && <>{sendMode === 'Individual' && missingEmailRows.length > 0 && <Alert type="warning" showIcon message={`${missingEmailRows.length} recipients need an email. Bulk sending queues valid recipients and reports missing or invalid emails per row.`} />}<Divider /><Form component="div" layout="vertical" disabled={busy}><Form.Item label="Email delivery"><Select aria-label="Payslip email delivery" disabled={pendingSendIds.length > 0} value={sendMode} options={[{ value: 'Individual', label: 'Individual PDF to each employee' }, { value: 'Combined', label: 'Combined PDF to one email address' }]} onChange={setSendMode} /></Form.Item>{sendMode === 'Combined' ? <Form.Item label="Recipient email" required><Input aria-label="Combined payslip recipient" disabled={pendingSendIds.length > 0} type="email" value={combinedEmail} onChange={event => setCombinedEmail(event.target.value)} /></Form.Item> : <DataTable rows={actionRows} getRowId={row => row.id} title="Recipients for this send" exportDisabled columns={[{ key: 'sourceRow', label: 'Source row' }, { key: 'employeeName', label: 'Employee' }, { key: 'email', label: 'Email override', render: row => <Input aria-label={`Email for source row ${row.sourceRow}`} disabled={pendingSendIds.includes(row.id) || delivery.some(item => item.rowId === row.id && item.status !== 'Error')} value={emailOverrides[row.id] ?? row.email} placeholder="Email required only for sending" onChange={event => setEmailOverrides(current => ({ ...current, [row.id]: event.target.value }))} /> }]} />}</Form><p className="excel-payslip-muted">Email overrides apply to this send. They do not edit the saved batch or employee records.</p>{!!delivery.length && <DataTable rows={delivery} getRowId={(row, index) => `${row.rowId}:${index}`} title="Email results" columns={[{ key: 'employeeName', label: 'Employee' }, { key: 'email', label: 'Recipient' }, { key: 'status', label: 'Status' }, { key: 'message', label: 'Details', wrap: true }]} />}</>}
      {pdfUrl && <iframe ref={iframe} title="Imported payslip PDF preview" src={pdfUrl} className="excel-payslip-pdf" />}
    </Drawer>
  </section>
}
