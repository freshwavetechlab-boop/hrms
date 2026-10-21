import { useEffect, useMemo, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Alert, Button, Checkbox, Descriptions, Divider, Drawer, Dropdown, Empty, Form, Input, InputNumber, Segmented, Select, Space, Spin, Tag, Tooltip } from 'antd'
import { CloseOutlined, DownOutlined, DownloadOutlined, EditOutlined, FilePdfOutlined, HistoryOutlined, ReloadOutlined, SendOutlined, UploadOutlined } from '@ant-design/icons'
import { useSessionPreference } from '../hooks/useRecruitmentPreferences'
import { useExcelPayslipEmailStatus } from '../hooks/useExcelPayslipEmailStatus'
import { useExcelPayslipMailJobs } from '../hooks/useExcelPayslipMailJobs'
import { buildExcelPayslipRows, buildPayslipCalculationSource, defaultPayslipMapping, excelColumnLetter, formatExcelPayslipAmount, inferPayslipMonth, isPayslipAmountKind, parseExcelPayslipFile, payslipColumnKinds, payslipHeaders, payslipHeaderSignature, payslipPhoneValue, type PayslipColumn, type PayslipColumnKind, type PayslipSheet } from '../utils/excelPayslipImport'
import ExcelPayslipMonthTools from './ExcelPayslipMonthTools'
import ExcelPayslipMailProgress from './ExcelPayslipMailProgress'
import ExcelPayslipBulkAmountEditor from './ExcelPayslipBulkAmountEditor'
import { canQueuePayslipEmail, payslipSentCopyLabel } from '../utils/excelPayslipDelivery'
import { buildEditablePayslipSheet, downloadExcelPayslipTemplate } from '../utils/excelPayslipTemplate'
import { getExcelPayslipBatch, getExcelPayslipCalculation, getExcelPayslipClients, getExcelPayslipHistory, getExcelPayslipPdf, getExcelPayslipProfile, getExcelPayslipTemplates, saveExcelPayslipBatch, saveExcelPayslipProfile, startExcelPayslipMailJob, requestExcelPayslipEmailIds, type ExcelPayslipBatch, type ExcelPayslipCalculationSource, type ExcelPayslipDelivery, type ExcelPayslipRow, type ExcelPayslipSendRequest, type ExcelPayslipSummary, type ExcelPayslipTemplate } from '../services/excelPayslipService'
import { useAuthSession } from './AuthGate'
import DataTable from './DataTable'
import FileDropZone from './FileDropZone'
import ExcelPayrollSetup from './ExcelPayrollSetupWorkspace'
import ExcelPayslipAmountEditor, { ExcelPayslipWarnings, PayslipReconciliation } from './ExcelPayslipReview'
import { correctPayslipAmounts, correctPayslipBatchAmounts } from '../utils/excelPayslipReview'
import EmployeeResignationAction from './EmployeeResignationAction'
import { buildPayslipMappingEvidence } from '../utils/excelPayslipMapping'
import { consolidatePayslipWorkbooks, suggestPayslipWorksheet, type PayslipWorkbookPart } from '../utils/excelPayslipConsolidation'
import { suggestExcelPayslipMapping } from '../services/excelPayslipService'
import './EmployeeResignation.css'
import { PageHeaderPortal } from './layout/AppPageHeader'
import './ExcelPayslips.css'

type Action = { kind: 'preview' | 'download' | 'send'; rowIds: string[] }
type MailTab = 'All' | 'Pending' | 'Queued' | 'Sent' | 'Attention' | 'Missing'
const mailLabels = { Missing: 'Email missing', All: 'All', Pending: 'Not sent', Queued: 'Queued', Sent: 'Employee SMTP accepted / delivered', Bounced: 'Not delivered', Deferred: 'Delayed', Delivered: 'Delivered', Attention: 'Needs attention', Failed: 'Failed', Unknown: 'Needs attention' }
const mailColors = { Pending: 'default', Queued: 'processing', Sent: 'success', Failed: 'error', Unknown: 'warning', Bounced: 'error', Deferred: 'warning', Delivered: 'success' }
const amount = formatExcelPayslipAmount
const sum = (items: { amount: number }[]) => items.reduce((total, item) => total + item.amount, 0)
const emailValid = (value: string) => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value)
const categoryKind = (category: string): PayslipColumnKind => ({ Earning: 'earning', Deduction: 'deduction', Employer: 'employer', Information: 'info' }[category] as PayslipColumnKind) || 'info'
const batchSummary = ({ rows, ...batch }: ExcelPayslipBatch): ExcelPayslipSummary => ({ ...batch, rowCount: rows.length })
const templateWarningSummary = (warnings: string[]) => [...warnings.slice(0, 3), ...(warnings.length > 3 ? [`${warnings.length - 3} more source review notes. Review exclusions and original source rows before saving.`] : [])].join(' ')

export default function ExcelPayslips() {
  const session = useAuthSession()
  const [searchParams, setSearchParams] = useSearchParams()
  const linkedClientId = Number(searchParams.get('clientId') || 0), linkedBatchId = searchParams.get('batchId') || ''
  const linkedMailJobId = searchParams.get('mailJobId') || ''
  const shellClientContext = Boolean(session?.user.clientId && !session.user.permissions.includes('security.manage'))
  const canManage = Boolean(session?.user.permissions.some(permission => ['payroll.run', 'payroll.approve', 'payroll.payments'].includes(permission)))
  const canConfigurePayroll = ['settings.manage', 'employees.manage', 'payroll.run'].every(permission => session?.user.permissions.includes(permission))
  const canManageEmployees = Boolean(session?.user.permissions.includes('employees.manage'))
  const [setupRows, setSetupRows] = useState<string[] | null>(null)
  const [compactActions, setCompactActions] = useState(() => window.matchMedia('(max-width: 900px)').matches)
  useEffect(() => { const media = window.matchMedia('(max-width: 900px)'); const update = () => setCompactActions(media.matches); media.addEventListener('change', update); return () => media.removeEventListener('change', update) }, [])
  const [clients, setClients] = useState<{ id: number; name: string }[]>([])
  const [preferredClient, setPreferredClient] = useSessionPreference(`excel-payslips:${session?.user.id}:client`, 0)
  const clientId = linkedClientId ? clients.find(client => client.id === linkedClientId)?.id || 0 : clients.find(client => client.id === preferredClient)?.id || clients[0]?.id || 0
  const [view, setView] = useSessionPreference<'Table' | 'Cards'>(`excel-payslips:${session?.user.id}:${clientId}:view`, 'Table')
  const [lastBatchId, setLastBatchId] = useSessionPreference(`excel-payslips:${session?.user.id}:${clientId}:batch`, '')
  const [selectedIds, setSelectedIds] = useSessionPreference<string[]>(`excel-payslips:${session?.user.id}:${clientId}:${lastBatchId}:selection`, [])
  const [history, setHistory] = useState<ExcelPayslipSummary[]>([]), [templates, setTemplates] = useState<ExcelPayslipTemplate[]>([])
  const latestByMonth = useMemo(() => {
    const latest = new Map<string, string>()
    for (const row of [...history].sort((a, b) => ((Date.parse(b.createdAtUtc) || 0) - (Date.parse(a.createdAtUtc) || 0)) || b.id.localeCompare(a.id))) {
      if (!latest.has(row.month)) latest.set(row.month, row.id)
    }
    return latest
  }, [history])
  const [batch, setBatch] = useState<ExcelPayslipBatch | null>(null)
  const emailStatus = useExcelPayslipEmailStatus(clientId, batch)
  const mailJobs = useExcelPayslipMailJobs(clientId, batch?.clientId === clientId ? batch.id : '')
  const [preferredMailJob, setPreferredMailJob] = useSessionPreference(`excel-payslips:${session?.user.id}:${clientId}:${batch?.id}:mail-job`, '')
  const [hiddenProgress, setHiddenProgress] = useSessionPreference(`excel-payslips:${session?.user.id}:${clientId}:${batch?.id}:hidden-progress`, '')
  const [progressExpanded, setProgressExpanded] = useSessionPreference(`excel-payslips:${session?.user.id}:${clientId}:${batch?.id}:progress-expanded`, false)
  const progressKey = mailJobs.jobs.map(job => job.id).sort().join(',') || (mailJobs.error ? 'error' : '')
  const progressVisible = Boolean(progressKey) && hiddenProgress !== progressKey
  const [tableMailTab, setTableMailTab] = useSessionPreference<MailTab>(`excel-payslips:${session?.user.id}:${clientId}:${batch?.id}:table-mail-status`, 'All')
  const [mailTab, setMailTab] = useState<MailTab>('All')
  const [loading, setLoading] = useState(false), [clientsLoading, setClientsLoading] = useState(true), [historyLoading, setHistoryLoading] = useState(false)
  const [templatesReady, setTemplatesReady] = useState(false), [templatesLoading, setTemplatesLoading] = useState(false), [templatesError, setTemplatesError] = useState(''), [templatesReload, setTemplatesReload] = useState(0)
  const [busy, setBusy] = useState(false), [error, setError] = useState(''), [notice, setNotice] = useState('')
  const [exportWarnings, setExportWarnings] = useState<string[]>([])
  const [historyOpen, setHistoryOpen] = useState(false), [importOpen, setImportOpen] = useState(false)
  const [editingBatchId, setEditingBatchId] = useState('')
  const [monthTools, setMonthTools] = useState<'calculate' | 'variance' | null>(null)
  const [sheets, setSheets] = useState<PayslipSheet[]>([]), [sheetName, setSheetName] = useState(''), [fileName, setFileName] = useState('')
  const [sourceParts, setSourceParts] = useState<PayslipWorkbookPart[]>([]), [reviewSourceSplits, setReviewSourceSplits] = useState(false)
  const [headerRow, setHeaderRow] = useState(5), [month, setMonth] = useState(''), [templateId, setTemplateId] = useState('')
  const [simpleLayout, setSimpleLayout] = useState(false)
  const [columns, setColumns] = useState<PayslipColumn[]>([]), [signature, setSignature] = useState(''), [profileLoading, setProfileLoading] = useState(false)
  const [suggesting, setSuggesting] = useState(false), [mappingReview, setMappingReview] = useState<number[]>([])
  const suggestionRequest = useRef<AbortController | null>(null)
  const mappingBusy = profileLoading || suggesting
  const [excludedRows, setExcludedRows] = useState<number[]>([]), [importError, setImportError] = useState(''), [mappingNotice, setMappingNotice] = useState('')
  const [review, setReview] = useState<ExcelPayslipRow | null>(null)
  const [warningsOpen, setWarningsOpen] = useState(false)
  const [correction, setCorrection] = useState<{ sheet: PayslipSheet; columns: PayslipColumn[]; sourceRow: number; sourceRows?: number[]; clientId: number } | null>(null)
  const [action, setAction] = useState<Action | null>(null), [acknowledgeWarnings, setAcknowledgeWarnings] = useState(false), [includeSeal, setIncludeSeal] = useState(false)
  const [amountDecimalPlaces, setAmountDecimalPlaces] = useState<0 | 2>(0)
  const [sendMode, setSendMode] = useState<'Individual' | 'Combined'>('Combined'), [combinedEmail, setCombinedEmail] = useState(''), [emailOverrides, setEmailOverrides] = useState<Record<string, string>>({})
  const [excludedRecipientIds, setExcludedRecipientIds] = useState<string[]>([]), [excludeMissingRecipients, setExcludeMissingRecipients] = useState(true)
  const [nextEmailMode, setNextEmailMode] = useState<'Individual' | 'Combined'>('Combined')
  const [requestMissingEmails, setRequestMissingEmails] = useState(false)
  const [attachRequestPayslips, setAttachRequestPayslips] = useState(false)
  const pendingEmailRequest = useRef<import('../services/excelPayslipService').ExcelPayslipEmailRequest | null>(null)
  const [actionError, setActionError] = useState(''), [pdfUrl, setPdfUrl] = useState(''), [delivery, setDelivery] = useState<ExcelPayslipDelivery[]>([]), [sendProgress, setSendProgress] = useState(''), [pendingSendIds, setPendingSendIds] = useState<string[]>([])
  const iframe = useRef<HTMLIFrameElement>(null), scope = useRef(0), batchSequence = useRef(0), profileSequence = useRef(0), inFlight = useRef(false)
  const pendingJobRequest = useRef<ExcelPayslipSendRequest | null>(null)
  const editingCalculation = useRef<Pick<ExcelPayslipCalculationSource, 'sourceBatchId' | 'attendanceColumnIndex' | 'monthDaysCell'>>({})
  const currentClient = useRef(clientId)
  const lastLink = useRef({ clientId, batchId: linkedBatchId })
  const chooseClient = (value: number) => {
    setPreferredClient(value)
    if (linkedClientId || linkedBatchId || linkedMailJobId) { const next = new URLSearchParams(searchParams); next.delete('clientId'); next.delete('batchId'); next.delete('mailJobId'); setSearchParams(next, { replace: true }) }
  }
  const rememberBatch = (id: string) => {
    setLastBatchId(id); setPreferredClient(clientId)
    if (linkedBatchId) { const next = new URLSearchParams(searchParams); next.delete('batchId'); setSearchParams(next, { replace: true }) }
  }
  const chooseMailJob = (id: string) => {
    setPreferredMailJob(id)
    if (linkedMailJobId) { const next = new URLSearchParams(searchParams); next.delete('mailJobId'); setSearchParams(next, { replace: true }) }
  }
  const closeBatch = () => {
    if (busy) return
    ++batchSequence.current
    setBatch(null); setLastBatchId(''); setSelectedIds([]); setPreferredMailJob(''); setSetupRows(null)
    setReview(null); setWarningsOpen(false); setCorrection(null); setAction(null); setMonthTools(null); setImportOpen(false); setLoading(false); setNotice(''); setError('')
    const next = new URLSearchParams(searchParams); next.delete('batchId'); next.delete('mailJobId')
    setSearchParams(next, { replace: true })
  }
  const sheet = sheets.find(item => item.name === sheetName)
  const workbookTemplate = sheet?.template
  const headers = useMemo(() => sheet ? payslipHeaders(sheet, headerRow) : [], [sheet, headerRow])
  const imported = useMemo(() => sheet ? buildExcelPayslipRows({ ...sheet, ...(sheet.template ? { template: { ...sheet.template, month } } : {}) }, headerRow, columns, excludedRows) : { rows: [], issues: [], excludedRows: [], excludedDetails: [] }, [sheet, headerRow, columns, excludedRows, month])
  const template = templates.find(item => String(item.id) === templateId)
  const rows = batch?.clientId === clientId ? batch.rows : []
  const activeMailTab = view === 'Cards' ? mailTab : tableMailTab
  const visibleRows = rows.filter(row => activeMailTab === 'All' || (activeMailTab === 'Missing' ? !emailValid(row.email.trim()) : activeMailTab === 'Attention' ? ['Failed', 'Unknown', 'Bounced', 'Deferred'].includes(emailStatus.statuses.get(row.id)?.status || '') : activeMailTab === 'Sent' ? ['Sent', 'Delivered'].includes(emailStatus.statuses.get(row.id)?.status || '') : emailStatus.statuses.get(row.id)?.status === activeMailTab))
  const validSelection = selectedIds.filter(id => visibleRows.some(row => row.id === id))
  const eligibleRows = rows.filter(row => !row.employment?.blockedForMonth)
  const exitCandidates = rows.filter(row => validSelection.includes(row.id))
  const exitEmployees = exitCandidates.every(row => row.employment?.employeeId && !row.employment.lastWorkingDate && row.employment.status === 'Active' && !row.employment.identityReviewRequired)
    ? [...new Map(exitCandidates.map(row => [row.employment!.employeeId!, { id: row.employment!.employeeId!, employeeCode: row.employment!.employeeCode, name: row.employeeName }])).values()] : []
  const canEmailRow = (id: string, mode: 'Individual' | 'Combined' = 'Individual', recipient = '') => {
    const row = rows.find(row => row.id === id)
    return !row?.employment?.blockedForMonth && emailStatus.ready && canQueuePayslipEmail(emailStatus.statuses.get(id), mode, mode === 'Individual' ? emailOverrides[id] ?? row?.email ?? '' : recipient)
  }
  const emailTargets = (validSelection.length ? validSelection : visibleRows.map(row => row.id)).filter(id => !rows.find(row => row.id === id)?.employment?.blockedForMonth && emailStatus.ready)
  const tabCounts = { All: rows.length, Pending: 0, Queued: 0, Sent: 0, Attention: 0, Missing: rows.filter(row => !emailValid(row.email.trim())).length }
  for (const row of rows) { const status = emailStatus.statuses.get(row.id)?.status; if (status) tabCounts[['Failed', 'Unknown', 'Bounced', 'Deferred'].includes(status) ? 'Attention' : status === 'Delivered' ? 'Sent' : status as 'Pending' | 'Queued' | 'Sent']++ }
  const actionRows = action ? rows.filter(row => action.rowIds.includes(row.id)) : []
  const selectedMailRows = actionRows.filter(row => !excludedRecipientIds.includes(row.id) && (sendMode !== 'Individual' || canEmailRow(row.id) && (!excludeMissingRecipients || emailValid((emailOverrides[row.id] ?? row.email).trim()))))
  const missingEmailRows = actionRows.filter(row => !emailValid((emailOverrides[row.id] ?? row.email).trim()))
  const emailRequestRows = actionRows.filter(row => !emailValid(row.email.trim()) && (!emailValid(combinedEmail) || canEmailRow(row.id, 'Combined', combinedEmail)))
  const requestingEmailIds = sendMode === 'Combined' && requestMissingEmails
  const warningRows = (requestingEmailIds ? attachRequestPayslips ? emailRequestRows : [] : action?.kind === 'send' ? selectedMailRows : actionRows).filter(row => row.warnings.length)
  const needsAck = warningRows.length > 0 && !acknowledgeWarnings
  useEffect(() => { currentClient.current = clientId }, [clientId])

  useEffect(() => {
    let active = true
    void getExcelPayslipClients().then(result => { if (!active) return; setClients(result.data); if (!result.ok) setError(result.error); setClientsLoading(false) })
    return () => { active = false }
  }, [])
  useEffect(() => {
    const token = ++scope.current
    void Promise.resolve().then(async () => {
      if (token !== scope.current) return
      setSetupRows(null)
      setWarningsOpen(false); setCorrection(null)
      const batchToken = ++batchSequence.current
      setBatch(null); setHistory([]); setTemplates([]); setTemplatesReady(false); setTemplatesError(''); setTemplatesLoading(false); setAction(null); setImportOpen(false); setReview(null); setHistoryOpen(false); setMonthTools(null); setError(''); setNotice(''); setSheets([])
      setLoading(false); setBusy(false); setHistoryLoading(false); setExportWarnings([]); setMailTab('All'); setEditingBatchId(''); setSimpleLayout(false); setSourceParts([]); setReviewSourceSplits(false)
      if (!clientId) return
      setHistoryLoading(true)
      void getExcelPayslipHistory(clientId).then(result => {
        if (token !== scope.current) return
        if (result.ok) setHistory(result.data)
        else setError(result.error || 'Unable to load batch history.')
        setHistoryLoading(false)
      })
      const batchToOpen = linkedBatchId || lastBatchId
      if (batchToOpen) {
        setLoading(true)
        void getExcelPayslipBatch(clientId, batchToOpen).then(result => {
          if (token !== scope.current || batchToken !== batchSequence.current) return
          if (result.ok && result.data) { setBatch(result.data); rememberBatch(result.data.id) }
          else setError(result.error || 'Unable to reopen the saved batch.')
          setLoading(false)
        })
      }
    })
    return () => { scope.current = token + 1 }
    // Client scope changes reset the workspace; the last batch is read once for that scope.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [clientId])
  useEffect(() => {
    if (!importOpen || !clientId || templatesReady) return
    let active = true
    setTemplatesLoading(true); setTemplatesError('')
    void getExcelPayslipTemplates(clientId).then(result => {
      if (!active) return
      if (result.ok) { setTemplates(result.data); setTemplatesReady(true) }
      else setTemplatesError(result.error || 'Unable to load salary templates.')
      setTemplatesLoading(false)
    })
    return () => { active = false }
  }, [clientId, importOpen, templatesReady, templatesReload])
  useEffect(() => {
    let active = true
    const controller = new AbortController()
    const sequence = ++profileSequence.current
    void Promise.resolve().then(async () => {
      if (!active) return
      setColumns(defaultPayslipMapping(headers, sheet, headerRow)); setSignature(''); setExcludedRows([]); setMappingNotice(''); setTemplateId('')
      setProfileLoading(false)
      if (!headers.length || !clientId) return
      setProfileLoading(true)
      try {
        const nextSignature = await payslipHeaderSignature(headers)
        if (!active || sequence !== profileSequence.current) return
        setSignature(nextSignature)
        if (workbookTemplate?.clientId === clientId && workbookTemplate.headerRow === headerRow) {
          setColumns(workbookTemplate.columns); setExcludedRows(workbookTemplate.excludedSourceRows)
          setTemplateId(workbookTemplate.salaryTemplateId || '')
          setMappingNotice('Saved worksheet mapping restored. Review the column mapping and employee preview before saving a new batch.')
          return
        }
        const result = await getExcelPayslipProfile(clientId, nextSignature, controller.signal)
        if (!active || sequence !== profileSequence.current) return
        setSignature(nextSignature)
        if (result.ok && result.data?.headerSignature === nextSignature && Array.isArray(result.data.columns) && result.data.columns.length === headers.length && result.data.columns.every((column, index) => column.columnIndex === index && column.sourceHeader === headers[index] && payslipColumnKinds.some(kind => kind.value === column.kind))) {
          setColumns(result.data.columns); setTemplateId(result.data.salaryTemplateId || ''); setMappingNotice('Saved mapping restored for these column headings.')
        } else if (!result.ok && result.status !== 404) setMappingNotice('Saved mapping is temporarily unavailable. Suggested mapping is ready; review each column and the employee preview before saving. The saved preset has not been changed.')
      } catch (failure) { if (active) setImportError(failure instanceof Error ? failure.message : 'Unable to prepare column mapping.') }
      finally { if (active) setProfileLoading(false) }
    })
    return () => { active = false; controller.abort() }
  }, [headers, clientId, headerRow, workbookTemplate])
  useEffect(() => {
    suggestionRequest.current?.abort(); suggestionRequest.current = null
    setSuggesting(false); setMappingReview([])
    return () => { suggestionRequest.current?.abort(); suggestionRequest.current = null }
  }, [clientId, sheet, headerRow, templateId, importOpen])
  useEffect(() => { return () => { if (pdfUrl) URL.revokeObjectURL(pdfUrl) } }, [pdfUrl])

  const upload = async (file: File) => {
    if (!canManage || busy) return
    const requestedScope = scope.current
    setBusy(true); setImportError(''); setMappingNotice('')
    try {
      const parsed = await parseExcelPayslipFile(file)
      if (requestedScope !== scope.current) return
      setSourceParts([]); setReviewSourceSplits(false)
      if (parsed.some(item => item.template && item.template.clientId !== clientId)) throw new Error('This salary template belongs to another client. Select its client before importing.')
      const proposed = suggestPayslipWorksheet(parsed)
      const selected = parsed.find(item => item.template) || proposed?.sheet || parsed.find(item => item.name.toLowerCase() === 'calcultion') || parsed[0]
      const proposedHeader = selected.template?.headerRow || (proposed?.sheet === selected ? proposed.headerRow : undefined) || selected.rows.find(row => row.cells.some(cell => cell?.value.toLowerCase().trim() === 'name of the person'))?.sourceRow || selected.rows.find(row => row.cells.some(cell => cell?.value.trim()))?.sourceRow || 1
      setSheets(parsed); setSheetName(selected.name); setHeaderRow(proposedHeader); setMonth(selected.template?.month || inferPayslipMonth(selected)); setFileName(file.name); setEditingBatchId(''); setSimpleLayout(selected.template?.simpleLayout === true)
    } catch (failure) { if (requestedScope === scope.current) { setImportError(failure instanceof Error ? failure.message : 'Unable to read this file.'); setSheets([]) } }
    finally { if (requestedScope === scope.current) setBusy(false) }
  }
  const uploadFiles = async (files: File[]) => {
    if (files.length === 1) return upload(files[0])
    if (!canManage || busy || !files.length) return
    const requestedScope = scope.current
    setBusy(true); setImportError(''); setMappingNotice('')
    try {
      const parts = []
      if (files.length > 50) throw new Error('Choose up to 50 salary workbooks.')
      if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(month)) throw new Error('Choose the salary month before combining location workbooks.')
      for (const file of files) {
        const parsed = await parseExcelPayslipFile(file), selected = suggestPayslipWorksheet(parsed)
        if (requestedScope !== scope.current) return
        if (!selected) throw new Error(`${file.name}: a salary sheet with employee name and net pay is required. Review this workbook separately.`)
        if (parsed.some(s => s.template && s.template.clientId !== clientId)) throw new Error(`${file.name} belongs to another client.`)
        const headers = payslipHeaders(selected.sheet, selected.headerRow), nextSignature = await payslipHeaderSignature(headers)
        const saved = await getExcelPayslipProfile(clientId, nextSignature)
        if (requestedScope !== scope.current) return
        const columns = saved.ok && saved.data?.headerSignature === nextSignature && Array.isArray(saved.data.columns) && saved.data.columns.length === headers.length && saved.data.columns.every((c, i) => c.columnIndex === i && c.sourceHeader === headers[i] && payslipColumnKinds.some(k => k.value === c.kind))
          ? saved.data.columns : defaultPayslipMapping(headers, selected.sheet, selected.headerRow)
        parts.push({ fileName: file.name, ...selected, columns })
      }
      const combined = consolidatePayslipWorkbooks(parts, clientId, month)
      setSourceParts(parts); setReviewSourceSplits(false)
      setSheets([combined]); setSheetName(combined.name); setHeaderRow(1); setFileName(`Combined salary - ${files.length} workbooks.xlsx`); setEditingBatchId(''); setSimpleLayout(true)
      setMappingNotice(`${files.length} salary workbooks combined for review. Source files and Excel rows are retained. Review blank amounts, duplicates and employee matching before saving.`)
    } catch (failure) { if (requestedScope === scope.current) setImportError(failure instanceof Error ? failure.message : 'Unable to combine these workbooks.') }
    finally { if (requestedScope === scope.current) setBusy(false) }
  }
  const reviewSplitSources = (confirmed: boolean) => {
    try {
      const combined = consolidatePayslipWorkbooks(sourceParts, clientId, month, confirmed)
      setSheets([combined]); setReviewSourceSplits(confirmed)
      setMappingNotice(confirmed ? 'Source payroll units confirmed for separate slips. Original work locations remain unchanged; matching identity and split attendance are still required.' : 'Separate source-unit slips are not approved. Duplicate employee checks remain enabled.')
    } catch (failure) { setImportError(failure instanceof Error ? failure.message : 'Unable to review source payroll units.') }
  }
  const patchColumn = (index: number, patch: Partial<PayslipColumn>) => setColumns(current => current.map(column => column.columnIndex === index ? { ...column, ...patch } : column))
  const suggestMapping = async () => {
    if (!sheet || !clientId || mappingBusy || busy || !canManage) return
    const controller = new AbortController(), requestedClient = clientId
    suggestionRequest.current?.abort(); suggestionRequest.current = controller
    const baseline = defaultPayslipMapping(headers, sheet, headerRow)
    setSuggesting(true); setImportError(''); setMappingReview([])
    try {
      const result = await suggestExcelPayslipMapping(clientId, buildPayslipMappingEvidence(sheet, headerRow, baseline, templateId, imported.excludedRows), controller.signal)
      if (controller.signal.aborted || suggestionRequest.current !== controller || currentClient.current !== requestedClient) return
      const suggestion = result.data
      if (!result.ok || !suggestion || suggestion.columns.length !== baseline.length || suggestion.columns.some((c, i) => c.columnIndex !== i || !payslipColumnKinds.some(k => k.value === c.kind))) {
        setMappingNotice('AI suggestions are temporarily unavailable. Your current mapping is unchanged; you can continue reviewing it manually.')
        return
      }
      if (suggestion.method === 'rules') {
        setMappingNotice('AI is temporarily unavailable. Your current mapping is unchanged; you can continue reviewing it manually.')
        return
      }
      // Restore local source headings; the request used redacted metadata, not original identifiers/cell values.
      setColumns(suggestion.columns.map((column, index) => {
        const component = template?.components.find(c => c.id === column.componentId)
        return { ...baseline[index], kind: column.kind, ...(component ? { componentId: component.id, label: component.name } : {}) }
      }))
      setMappingReview(suggestion.reviewColumnIndexes)
      setMappingNotice(`${suggestion.message}${suggestion.method === 'ai' ? ` Model: ${suggestion.model}. ${suggestion.references.length} reference sections used.` : ''}${suggestion.reviewColumnIndexes.length ? ` Review columns: ${suggestion.reviewColumnIndexes.map(excelColumnLetter).join(', ')}.` : ''}`)
    } finally {
      if (suggestionRequest.current === controller) { setSuggesting(false); suggestionRequest.current = null }
    }
  }
  const saveMapping = async () => {
    if (!canManage || !clientId || !signature || busy) return
    setBusy(true); setImportError(''); setMappingNotice('')
    const result = await saveExcelPayslipProfile(clientId, { headerSignature: signature, columns, salaryTemplateId: templateId || undefined })
    setBusy(false)
    if (result.ok) setMappingNotice('Mapping saved for this client and these column headings.')
    else setImportError(result.error || 'Unable to save mapping.')
  }
  const saveBatch = async (correctedSheet?: PayslipSheet) => {
    if (!canManage || !clientId || busy || mappingBusy || !templatesReady) return false
    const source = correctedSheet || sheet
    const preview = correctedSheet ? buildExcelPayslipRows({ ...correctedSheet, ...(correctedSheet.template ? { template: { ...correctedSheet.template, month } } : {}) }, headerRow, columns, excludedRows) : imported
    if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(month)) { setImportError('Choose the salary month from the source before saving.'); return false }
    if (preview.issues.length || !preview.rows.length) { setImportError(preview.issues[0]?.message || 'Resolve the import issues before saving this batch.'); return false }
    if (templateId && !template) { setImportError('Choose a currently available client salary template, or manual mapping.'); return false }
    const requestedScope = scope.current, requestedBatch = batchSequence.current
    setBusy(true); setImportError('')
    const result = await saveExcelPayslipBatch(clientId, { month, sourceFileName: fileName, sheetName, headerRow, simpleLayout, salaryTemplateId: templateId || undefined, salaryTemplateName: template?.name, rows: preview.rows.map(row => ({ ...row, id: crypto.randomUUID() })), calculationSource: source ? { ...buildPayslipCalculationSource(source, headerRow, columns, preview.excludedRows), ...(editingBatchId ? editingCalculation.current : {}) } : undefined })
    if (requestedScope !== scope.current || requestedBatch !== batchSequence.current) return false
    setBusy(false)
    if (!result.ok || !result.data) { setImportError(result.error || 'Unable to save this batch. Check batch history before retrying an interrupted request.'); return false }
    setBatch(result.data); setHistory(current => [batchSummary(result.data!), ...current.filter(item => item.id !== result.data!.id)]); rememberBatch(result.data.id); setSelectedIds([]); setImportOpen(false); setNotice(`${result.data.rows.length} payslips saved. Saved batches retain their imported amounts.`)
    setWarningsOpen(false); setCorrection(null); setReview(null)
    return true
  }
  const openHistoryBatch = async (id: string, fromLink = false, configurePayroll = false) => {
    if (busy && !fromLink) return
    const requestedClient = clientId
    const token = ++batchSequence.current
    setBusy(true); setError('')
    const result = await getExcelPayslipBatch(requestedClient, id)
    if (currentClient.current !== requestedClient || token !== batchSequence.current) return
    setBusy(false); setLoading(false)
    if (result.ok && result.data) { setBatch(result.data); rememberBatch(result.data.id); setSelectedIds([]); setHistoryOpen(false); setNotice(''); if (configurePayroll) setSetupRows(result.data.rows.map(row => row.id)) }
    else setError(result.error || 'Unable to open this saved batch.')
  }
  useEffect(() => {
    const previous = lastLink.current
    lastLink.current = { clientId, batchId: linkedBatchId }
    if (clientId && linkedBatchId && previous.clientId === clientId && previous.batchId !== linkedBatchId) void openHistoryBatch(linkedBatchId, true)
    // A new client loads its link in the scope effect; only a new same-client link loads here.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [clientId, linkedBatchId])
  const exportTemplate = async () => {
    if (!batch || batch.clientId !== clientId || busy || inFlight.current) return
    const selectedBatch = batch, requestedClient = clientId
    inFlight.current = true; setBusy(true); setError(''); setNotice(''); setExportWarnings([])
    try {
      const result = await getExcelPayslipCalculation(requestedClient, selectedBatch.id)
      if (currentClient.current !== requestedClient) return
      if (!result.ok || !result.data) throw new Error(result.error || 'Import the original salary workbook once to save its formulas before exporting a template.')
      setExportWarnings(downloadExcelPayslipTemplate(selectedBatch, result.data))
      setNotice('Salary template exported. Keep existing employee codes; leave the code blank for a new employee. Recalculate and save in Excel, then import and select the salary month. Saving creates a new batch.')
    } catch (failure) { if (currentClient.current === requestedClient) setError(failure instanceof Error ? failure.message : 'Unable to export salary template.') }
    finally { inFlight.current = false; setBusy(false) }
  }
  const editMapping = async (id = batch?.id, correctSourceRow?: number | 'warnings') => {
    if (!canManage || !clientId || !id || busy || inFlight.current) return
    const requestedScope = scope.current, requestedBatch = batchSequence.current
    inFlight.current = true; setBusy(true); setError(''); setNotice(''); setExportWarnings([])
    try {
      const selectedBatch = batch?.id === id && batch.clientId === clientId ? batch : (await getExcelPayslipBatch(clientId, id)).data
      if (requestedScope !== scope.current || requestedBatch !== batchSequence.current) return
      if (!selectedBatch || selectedBatch.clientId !== clientId) throw new Error('Unable to open this saved batch for editing.')
      const result = await getExcelPayslipCalculation(clientId, selectedBatch.id)
      if (requestedScope !== scope.current || requestedBatch !== batchSequence.current) return
      if (!result.ok || !result.data) throw new Error(result.error || 'This batch has no saved worksheet. Import the original salary workbook to change its mapping.')
      const editable = buildEditablePayslipSheet(selectedBatch, result.data)
      if (typeof correctSourceRow === 'number' && !editable.rows.some(row => row.sourceRow === correctSourceRow)) throw new Error('The saved employee source row is unavailable. Re-import the original workbook to correct it.')
      const { sourceBatchId, attendanceColumnIndex, monthDaysCell } = result.data
      editingCalculation.current = { sourceBatchId, attendanceColumnIndex, monthDaysCell }
      setSheets([editable]); setSheetName(editable.name); setHeaderRow(editable.template.headerRow)
      setMonth(selectedBatch.month); setFileName(selectedBatch.sourceFileName); setEditingBatchId(selectedBatch.id); setSimpleLayout(selectedBatch.simpleLayout === true)
      setImportError(''); setHistoryOpen(false); setImportOpen(true)
      setWarningsOpen(false); setReview(null); setAction(null); setPdfUrl('')
      const warningSourceRows = correctSourceRow === 'warnings' ? selectedBatch.rows.filter(row => row.warnings.length).map(row => row.sourceRow) : undefined
      setCorrection(correctSourceRow === undefined ? null : { sheet: editable, columns: editable.template.columns, sourceRow: typeof correctSourceRow === 'number' ? correctSourceRow : warningSourceRows?.[0] || 0, sourceRows: warningSourceRows, clientId })
    } catch (failure) {
      if (requestedScope === scope.current && requestedBatch === batchSequence.current) setError(failure instanceof Error ? failure.message : 'Unable to open the saved mapping.')
    } finally { inFlight.current = false; if (requestedScope === scope.current && requestedBatch === batchSequence.current) setBusy(false) }
  }
  const correctRow = (row: ExcelPayslipRow) => {
    if (!canManage || busy) return
    if (!importOpen || !sheet) { void editMapping(batch?.id, row.sourceRow); return }
    const prepared = { ...sheet, template: { version: 1 as const, clientId, sourceBatchId: sheet.template?.sourceBatchId || '', month, headerRow, columns, excludedSourceRows: excludedRows, salaryTemplateId: templateId, simpleLayout, warnings: sheet.template?.warnings || [] } }
    setReview(null); setImportError(''); setCorrection({ sheet: prepared, columns, sourceRow: row.sourceRow, clientId })
  }
  const saveCorrection = async (edits: Record<number, string | null>) => {
    if (!correction || correction.clientId !== clientId || !canManage || busy || mappingBusy || !templatesReady) return
    try {
      const updated = correctPayslipAmounts(correction.sheet, correction.sourceRow, correction.columns, edits)
      if (updated.changed) await saveBatch(updated.sheet)
    } catch (failure) { setImportError(failure instanceof Error ? failure.message : 'Unable to save corrected amounts.') }
  }
  const beginAction = (kind: Action['kind'], rowIds: string[]) => {
    if (!batch || !rowIds.length || busy) return
    rowIds = rowIds.filter(id => eligibleRows.some(row => row.id === id))
    if (!rowIds.length) return
    if (kind === 'send' && (!canManage || !emailStatus.ready)) return
    setExcludedRecipientIds([]); setExcludeMissingRecipients(true)
    setAction({ kind, rowIds }); setAcknowledgeWarnings(false); setIncludeSeal(false); setAmountDecimalPlaces(0); setSendMode(nextEmailMode); setRequestMissingEmails(false); setAttachRequestPayslips(false); pendingEmailRequest.current = null; setCombinedEmail(''); setEmailOverrides({}); setActionError(''); setPdfUrl(''); setDelivery([]); setSendProgress(''); setPendingSendIds([]); pendingJobRequest.current = null
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
    if (requestingEmailIds) {
      if (!canManage || !batch || inFlight.current || !emailRequestRows.length) return
      if (attachRequestPayslips && needsAck) return setActionError('Review and acknowledge the payslip warnings before attaching salary PDFs.')
      if (!emailValid(combinedEmail.trim())) return setActionError('Enter a valid recipient email for the request.')
      const request = pendingEmailRequest.current || { requestId: crypto.randomUUID(), rowIds: emailRequestRows.map(row => row.id), email: combinedEmail.trim(),
        attachPayslips: attachRequestPayslips, includeSeal, amountDecimalPlaces, acknowledgeWarnings }
      pendingEmailRequest.current = request
      const requestScope = scope.current
      inFlight.current = true; setBusy(true); setActionError('')
      try {
        const result = await requestExcelPayslipEmailIds(clientId, batch.id, request)
        if (requestScope !== scope.current) return
        if (!result.ok || !result.data) return setActionError(`${result.error || 'Unable to confirm the request.'} Retry uses the same reference to avoid duplicate mail.`)
        pendingEmailRequest.current = null; setAction(null)
        setNotice(`Email IDs requested for ${result.data.employeeCount} employee(s) from ${result.data.email}.${result.data.includesPayslips ? ' Their salary slips are included as one combined PDF.' : ''} Track request #${result.data.queueId} in Notifications. Personal employee email delivery remains unsent.`)
        window.dispatchEvent(new Event('hrms:actions-changed'))
      } finally { inFlight.current = false; if (requestScope === scope.current) setBusy(false) }
      return
    }
    if (!canManage || !batch || !action || needsAck || inFlight.current || !selectedMailRows.length) return
    if (sendMode === 'Combined' && !emailValid(combinedEmail.trim())) return setActionError('Enter a valid recipient email for the combined PDF.')
    if (sendMode === 'Individual' && actionRows.length === 1 && missingEmailRows.length) return setActionError('Enter a valid email before sending this payslip. PDF preview and download remain available.')
    const handledIds = new Set(delivery.filter(item => item.status !== 'Error').map(item => item.rowId))
    const eligibleIds = selectedMailRows.map(row => row.id).filter(id => !handledIds.has(id) && canEmailRow(id, sendMode, combinedEmail))
    if (!eligibleIds.length && !pendingJobRequest.current) return setActionError('No unsent payslips remain in this selection. Refresh email status to check delivery progress.')
    const request = pendingJobRequest.current || {
      rowIds: eligibleIds, includeSeal, acknowledgeWarnings, amountDecimalPlaces, mode: sendMode, email: combinedEmail.trim(),
      emailOverrides: Object.fromEntries(Object.entries(emailOverrides).filter(([id]) => eligibleIds.includes(id)).map(([id, value]) => [id, value.trim()])),
      requestId: crypto.randomUUID(),
    }
    const requestScope = scope.current
    pendingJobRequest.current = request
    inFlight.current = true; setBusy(true); setActionError(''); setSendProgress('Queuing payslips...')
    try {
      const result = await startExcelPayslipMailJob(clientId, batch.id, request)
      if (result.ok && result.data) window.dispatchEvent(new Event('hrms:actions-changed'))
      if (requestScope !== scope.current) return
      if (!result.ok || !result.data) {
        setPendingSendIds(request.rowIds)
        setActionError(`${result.error || 'The email job could not be confirmed.'} Retry keeps the same request reference. If it was already accepted, the saved job is returned without queuing duplicate mail.`)
        return
      }
      pendingJobRequest.current = null; setPendingSendIds([])
      setDelivery(current => [...current.filter(item => !request.rowIds.includes(item.rowId)), ...result.data!.items])
      chooseMailJob(result.data.job.id)
      setNotice('')
    } finally {
      inFlight.current = false
      if (requestScope === scope.current) { setBusy(false); setSendProgress(''); emailStatus.refresh() }
    }
  }

  return <section className="excel-payslips excel-payslip-workspace recruitment-viewport" aria-label="Excel payslips workspace">
    <ExcelPayslipWarnings rows={rows} open={warningsOpen} busy={busy} canManage={canManage} error={error} onClose={() => { if (!busy) setWarningsOpen(false) }} onReview={setReview} onCorrect={correctRow} onMapping={() => void editMapping()} onBulkCorrect={() => void editMapping(batch?.id, 'warnings')} />
    {batch?.clientId === clientId && setupRows && <ExcelPayrollSetup key={`setup:${clientId}:${batch.id}:${[...setupRows].sort().join(',')}`} clientId={clientId} batch={batch} rowIds={setupRows} onClose={() => setSetupRows(null)} onApplied={result => { setTemplatesReady(false); setNotice(`Payroll setup saved: ${result.createdCount} employees created, ${result.updatedCount} updated. Verify Employee simulation before running payroll.`) }} />}
    {batch?.clientId === clientId && <ExcelPayslipMonthTools key={`month-tools:${clientId}:${batch.id}`} clientId={clientId} batch={batch} history={history} canManage={canManage} open={monthTools} onClose={() => setMonthTools(null)} onBusy={setBusy} onSaved={saved => { setBatch(saved); setHistory(current => [batchSummary(saved), ...current]); rememberBatch(saved.id); setSelectedIds([]); setNotice(`${saved.month} payslips saved as a new batch.`) }} />}
    <PageHeaderPortal slot="excel-payslips-client-controls">{!shellClientContext && (clients.length === 1 ? <span className="scoped-client-chip">{clients[0].name}</span> : <Select aria-label="Excel payslips client" showSearch optionFilterProp="label" value={clientId || undefined} loading={clientsLoading} disabled={busy || clientsLoading} placeholder="Search or select client" options={clients.map(client => ({ value: client.id, label: client.name }))} onChange={chooseClient} style={{ width: 240, maxWidth: '100%' }} />)}</PageHeaderPortal>
    <PageHeaderPortal slot="excel-payslips-view-controls"><Segmented aria-label="Excel payslips view" options={['Table', 'Cards']} value={view} onChange={value => setView(value as 'Table' | 'Cards')} /></PageHeaderPortal>
    <PageHeaderPortal slot="excel-payslips-page-controls"><Space wrap className="excel-payslip-header-actions">
      <Tooltip title="Batch history"><Button aria-label="Batch history" icon={<HistoryOutlined />} disabled={!clientId || busy} onClick={() => setHistoryOpen(true)}><span className="excel-payslip-action-label">Batch history</span></Button></Tooltip>
      {canConfigurePayroll && <Tooltip title="Review Master setup for this saved batch or the selected employee rows"><Button aria-label="Configure payroll" disabled={!batch?.rows.length || busy || loading} onClick={() => setSetupRows(validSelection.length ? [...validSelection] : batch!.rows.map(row => row.id))}>Configure payroll{validSelection.length ? ` (${validSelection.length})` : ''}</Button></Tooltip>}
      {canManageEmployees && <Tooltip title="Select rows matched to active Employee Master records. Review ambiguous or unmatched identities before marking an exit."><span><EmployeeResignationAction key={`${clientId}:${batch?.id}`} clientId={clientId} employees={exitEmployees} disabled={busy || loading} onSaved={async () => { if (batch) await openHistoryBatch(batch.id); emailStatus.refresh() }} /></span></Tooltip>}
      {canManage && <Tooltip title="Import spreadsheet"><Button aria-label="Import spreadsheet" type="primary" icon={<UploadOutlined />} disabled={!clientId || loading || busy} onClick={() => { setEditingBatchId(''); setSheets([]); setSourceParts([]); setReviewSourceSplits(false); setFileName(''); setSimpleLayout(false); setImportOpen(true); setImportError(''); setExportWarnings([]) }}><span className="excel-payslip-action-label">Import spreadsheet</span></Button></Tooltip>}
      <Tooltip title={validSelection.length ? `PDF selected (${validSelection.length})` : 'PDF all'}><Button aria-label={validSelection.length ? `PDF selected (${validSelection.length})` : 'PDF all'} icon={<FilePdfOutlined />} disabled={busy || !(validSelection.length ? eligibleRows.some(row => validSelection.includes(row.id)) : eligibleRows.length)} onClick={() => beginAction('download', validSelection.length ? validSelection : eligibleRows.map(row => row.id))}><span className="excel-payslip-action-label">{validSelection.length ? `PDF selected (${validSelection.length})` : 'PDF all'}</span></Button></Tooltip>
      {canManage && <Tooltip title={validSelection.length ? `Email selected (${emailTargets.length})` : `Email batch (${emailTargets.length})`}><Button aria-label={validSelection.length ? `Email selected (${emailTargets.length})` : `Email batch (${emailTargets.length})`} icon={<SendOutlined />} disabled={!emailTargets.length || busy} onClick={() => beginAction('send', emailTargets)}><span className="excel-payslip-action-label">{validSelection.length ? `Email selected (${emailTargets.length})` : `Email batch (${emailTargets.length})`}</span></Button></Tooltip>}
      {batch?.clientId === clientId && <Dropdown menu={{ items: [
        { key: 'export-template', label: 'Export salary template', icon: <DownloadOutlined />, disabled: busy || loading, onClick: () => void exportTemplate() },
        ...(canManage ? [{ key: 'edit-mapping', label: 'Edit mapping', icon: <EditOutlined />, disabled: busy || loading, onClick: () => void editMapping() },
          { key: 'create-month', label: 'Create another month', disabled: busy || loading, onClick: () => setMonthTools('calculate') }] : []),
        { key: 'variance', label: 'Previous-month variance', disabled: busy || loading, onClick: () => setMonthTools('variance') }
      ] }}><Button aria-label="More payslip actions">More <DownOutlined /></Button></Dropdown>}
    </Space></PageHeaderPortal>
    {error && <Alert type="error" showIcon message={error} />}
    {!clientsLoading && linkedClientId > 0 && !clientId && <Alert type="error" showIcon message="This batch's client is not available in your access scope." action={<Button onClick={() => chooseClient(clients[0]?.id || 0)}>Choose an available client</Button>} />}
    {notice && <Alert type="success" showIcon closable message={notice} />}
    {rows.some(row => row.employment?.blockedForMonth) && <Alert type="info" showIcon message="Rows after employment ended are retained for source history. Their new payslip and email actions are disabled for this month." />}
    {!!exportWarnings.length && <Alert type="warning" showIcon closable message="Review the exported template" description={templateWarningSummary(exportWarnings)} afterClose={() => setExportWarnings([])} />}
    {batch?.clientId === clientId && progressVisible && <ExcelPayslipMailProgress jobs={mailJobs.jobs} selectedId={linkedMailJobId || preferredMailJob} onSelect={chooseMailJob} loading={mailJobs.loading} error={mailJobs.error} onRefresh={mailJobs.refresh} expanded={progressExpanded} onToggle={() => setProgressExpanded(value => !value)} onHide={() => setHiddenProgress(progressKey)} />}
    {batch && <div className="excel-payslip-summary excel-payslip-batch-bar"><div className="excel-payslip-batch-identity"><strong>{batch.month}</strong>{latestByMonth.has(batch.month) && <Tag color={latestByMonth.get(batch.month) === batch.id ? 'green' : 'default'}>{latestByMonth.get(batch.month) === batch.id ? 'Latest' : 'Previous batch'}</Tag>}<span className="excel-payslip-batch-file" title={batch.sourceFileName}>{batch.sourceFileName}</span><Tag className="excel-payslip-batch-count">{rows.length} payslips</Tag>{rows.some(row => row.warnings.length) && <Button type="text" className="excel-payslip-warning-count" aria-label={`${rows.filter(row => row.warnings.length).length} payslips with warnings`} disabled={busy} onClick={() => { setError(''); setWarningsOpen(true) }}><Tag color="orange">{rows.filter(row => row.warnings.length).length} with warnings</Tag></Button>}</div><Space size={4}>{progressKey && !progressVisible && <Button size="small" aria-label="Show email progress" title="Show email progress" icon={<DownOutlined />} onClick={() => setHiddenProgress('')}><span className="excel-payslip-action-label">Show email progress</span></Button>}<Button size="small" aria-label="Close batch" icon={<CloseOutlined />} disabled={busy} onClick={closeBatch}>Close batch</Button></Space></div>}
    {!!rows.length && view === 'Cards' && <div className="excel-payslip-email-tabs">
      <Segmented aria-label="Payslip email status" value={mailTab} disabled={busy} options={(['All', 'Pending', 'Missing', 'Queued', 'Sent', 'Attention'] as MailTab[]).map(value => ({ value, label: `${mailLabels[value]} (${tabCounts[value]})` }))} onChange={value => { setMailTab(value as MailTab); setSelectedIds([]) }} />
    </div>}
    {!!rows.length && emailStatus.error && <Alert type="error" showIcon message="Email status could not be verified" description={emailStatus.error} action={<Button onClick={emailStatus.refresh}>Retry status</Button>} />}
    {clientsLoading || loading ? <div className="excel-payslip-empty"><Spin tip="Loading Excel payslips..." /></div> : !rows.length ? <div className="excel-payslip-empty"><Empty description={clients.length ? 'Import a spreadsheet or reopen a saved batch.' : 'No authorized clients are available.'} /></div> : <DataTable key={`payslips:${clientId}:${batch?.id}`} fillHeight rows={visibleRows} view={view} exportToolbar={<Space wrap size={4} className="excel-payslip-table-tools">
      {view === 'Table' && <Select aria-label="Table email status" value={tableMailTab} disabled={busy} style={{ width: 210 }} options={(['All', 'Pending', 'Missing', 'Queued', 'Sent', 'Attention'] as MailTab[]).map(value => ({ value, label: `${mailLabels[value]} (${tabCounts[value]})` }))} onChange={value => { setTableMailTab(value); setSelectedIds([]) }} />}
      <Button size="small" aria-label="Refresh email status" title="Refresh email status" icon={<ReloadOutlined />} loading={emailStatus.loading} disabled={busy} onClick={emailStatus.refresh} />
      <Button size="small" disabled={busy || !visibleRows.length} onClick={() => setSelectedIds(visibleRows.map(row => row.id))}>Select all {visibleRows.length}</Button>
      <Button size="small" disabled={busy || !emailStatus.ready} onClick={() => { setNextEmailMode('Individual'); setSelectedIds(visibleRows.filter(row => emailValid(row.email.trim()) && canEmailRow(row.id)).map(row => row.id)) }}>Select with email</Button>
      <Button size="small" disabled={busy || !emailStatus.ready} onClick={() => { setNextEmailMode('Combined'); setSelectedIds(visibleRows.filter(row => !emailValid(row.email.trim()) && !row.employment?.blockedForMonth).map(row => row.id)) }}>Select missing email</Button>
      <Button size="small" disabled={busy} onClick={() => { setNextEmailMode('Individual'); setSelectedIds((validSelection.length ? validSelection : visibleRows.map(row => row.id)).filter(id => emailValid(rows.find(row => row.id === id)?.email || '') && canEmailRow(id))) }}>Exclude missing email</Button>
      {!!validSelection.length && <Button size="small" disabled={busy} onClick={() => setSelectedIds([])}>Clear selection ({validSelection.length})</Button>}
    </Space>} getRowId={row => row.id} rowClassName={row => row.employment?.lastWorkingDate ? 'employee-exit-record' : ''} exportFileName={`excel-payslips-${batch?.month}`} rowSelection={{ selectedRowKeys: validSelection, onChange: keys => setSelectedIds(keys.map(String)), getCheckboxProps: () => ({ disabled: busy }) }} columns={[
      { key: 'sourceRow', cardHidden: true, label: 'Source row', width: 105 }, { key: 'employeeName', label: 'Employee', width: 200 },
      { key: 'employment', label: 'Employment', width: 185, value: row => row.employment?.identityReviewRequired ? 'Identity review required' : row.employment ? `${row.employment.status}${row.employment.lastWorkingDate ? ` · Last day ${row.employment.lastWorkingDate}` : ''}` : 'Not linked to Employee Master', render: row => <Space direction="vertical" size={2}><Tag color={row.employment?.lastWorkingDate ? 'red' : row.employment?.identityReviewRequired ? 'orange' : 'default'}>{row.employment?.identityReviewRequired ? 'Identity review' : row.employment?.status || 'Not linked'}</Tag>{row.employment?.lastWorkingDate && <span>Last day {row.employment.lastWorkingDate}</span>}</Space> },
      { key: 'emailStatus', label: 'Employee email status', width: 235, value: row => { const item = emailStatus.statuses.get(row.id); return item ? `${mailLabels[item.status]} ${item.email}`.trim() : 'Checking' }, render: row => {
        const item = emailStatus.statuses.get(row.id), timestamp = item?.sentAtUtc || item?.queuedAtUtc
        return item ? <Tooltip title={[item.message, timestamp ? new Date(timestamp).toLocaleString() : ''].filter(Boolean).join(' - ')}><div className="excel-payslip-email-status"><Tag color={item.status === 'Sent' && item.sentCopyStatus !== 'Saved' ? 'warning' : mailColors[item.status]}>{item.status === 'Sent' ? 'SMTP accepted' : item.status === 'Pending' && !emailValid(row.email) ? 'Email missing' : mailLabels[item.status]}</Tag>{item.email && item.status !== 'Pending' && <span className="excel-payslip-muted">To: {item.email}</span>}{['Sent', 'Delivered'].includes(item.status) && <Tag color={item.sentCopyStatus === 'Saved' ? 'green' : 'orange'}>{payslipSentCopyLabel(item.sentCopyStatus)}</Tag>}</div></Tooltip> : <span className="excel-payslip-muted">{emailStatus.error ? 'Unavailable' : 'Checking...'}</span>
      } },
      { key: 'coordinatorCopy', label: 'Coordinator copy', width: 245, value: row => (emailStatus.statuses.get(row.id)?.recipientDeliveries || []).filter(item => item.mode === 'Combined').map(item => `${item.email}: ${item.status}`).join('; '), render: row => <Space direction="vertical" size={2}>{(emailStatus.statuses.get(row.id)?.recipientDeliveries || []).filter(item => item.mode === 'Combined').map((item, i) => <Tooltip key={i} title={item.message}><div><Tag color={['Failed', 'Bounced'].includes(item.status) ? 'red' : 'blue'}>Coordinator copy: {item.status === 'Sent' ? 'SMTP accepted' : item.status}</Tag><div className="excel-payslip-muted">To: {item.email}</div>{['Sent', 'Delivered'].includes(item.status) && <Tag color={item.sentCopyStatus === 'Saved' ? 'green' : 'orange'}>{payslipSentCopyLabel(item.sentCopyStatus)}</Tag>}</div></Tooltip>)}</Space> },
      { key: 'deliveryReason', cardHidden: true, label: 'Delivery details', width: 320, wrap: true, value: row => emailStatus.statuses.get(row.id)?.message || '', render: row => <Tooltip title={emailStatus.statuses.get(row.id)?.message}><span className="excel-payslip-delivery-reason">{emailStatus.statuses.get(row.id)?.message || '-'}</span></Tooltip> },
      { key: 'employeeCode', label: 'Employee code' },
      { key: 'earnings', label: 'Earnings', value: row => sum(row.earnings), render: row => amount(sum(row.earnings)) }, { key: 'deductions', label: 'Deductions', value: row => sum(row.deductions), render: row => amount(sum(row.deductions)) }, { key: 'netPay', label: batch?.simpleLayout ? 'In Hand Salary' : 'Net pay as stated', render: row => amount(row.netPay) },
      { key: 'warnings', cardHidden: true, label: 'Review', value: row => row.warnings.join('; ') || 'No warnings', render: row => row.warnings.length ? <Button type="text" className="excel-payslip-warning-count" aria-label={`Review warnings for ${row.employeeName}`} onClick={() => setReview(row)} disabled={busy}><Tag color="orange">{row.warnings.length} warnings</Tag></Button> : <Tag color="green">No warnings</Tag> },
      { key: 'email', cardHidden: true, label: 'Email', render: row => row.email || <span className="excel-payslip-muted">Optional · add when sending</span> },
      { key: 'phone', cardHidden: true, label: 'Phone', width: 155, value: payslipPhoneValue },

    ]} actionsWidth={compactActions ? 88 : 240} actions={row => compactActions ? <Dropdown menu={{ items: [
      { key: 'details', label: 'Details', disabled: busy, onClick: () => setReview(row) },
      { key: 'preview', label: 'Preview', disabled: busy || row.employment?.blockedForMonth, onClick: () => beginAction('preview', [row.id]) },
      ...(canManage ? [{ key: 'email', label: 'Email', disabled: busy || !canEmailRow(row.id), onClick: () => beginAction('send', [row.id]) }] : [])
    ] }}><Button size="small" aria-label={`Actions for ${row.employeeName}`}>Actions <DownOutlined /></Button></Dropdown> : <Space wrap size={4}><Button size="small" disabled={busy} onClick={() => setReview(row)}>Details</Button><Button size="small" disabled={busy || row.employment?.blockedForMonth} onClick={() => beginAction('preview', [row.id])}>Preview</Button>{canManage && <Button size="small" disabled={busy || !canEmailRow(row.id)} onClick={() => beginAction('send', [row.id])}>Email</Button>}</Space>} />}

    <Drawer title={editingBatchId ? 'Edit payslip mapping' : 'Import Excel payslips'} open={importOpen} width="min(1120px, 98vw)" onClose={() => { if (!busy) setImportOpen(false) }} closable={!busy} maskClosable={!busy} className="excel-payslip-import" footer={<Space wrap className="excel-payslip-footer"><Button disabled={busy} onClick={() => setImportOpen(false)}>Cancel</Button><Tooltip title="Save these column choices for future imports. This does not change a saved batch."><Button disabled={!canManage || !signature || busy || mappingBusy || !templatesReady} onClick={() => void saveMapping()}>{editingBatchId ? 'Save mapping preset' : 'Save mapping'}</Button></Tooltip><Button type="primary" loading={busy} disabled={!canManage || !imported.rows.length || !!imported.issues.length || mappingBusy || !templatesReady} onClick={() => void saveBatch()}>{editingBatchId ? 'Save as new batch' : 'Save batch'} ({imported.rows.length})</Button></Space>}>
      <Form component="div" layout="vertical" disabled={busy}>
        {editingBatchId ? <Alert type="info" showIcon message={`Editing ${month} · ${fileName}`} description="Change Use as or Payslip label below, then review the preview and Save as new batch. The previous batch and its email history remain unchanged; email tracking is separate for the new batch." /> : <FileDropZone accept=".xlsx,.csv" title="Upload salary spreadsheets" hint="Select one file, or combine location files for one salary month" fileName={fileName} onFile={file => { if (!busy) void upload(file) }} onFiles={files => { if (!busy) void uploadFiles(files) }} />}
        {templatesLoading && <Spin tip="Loading salary templates..." />}
        {templatesError && <Alert type="error" showIcon message={templatesError} action={<Button onClick={() => setTemplatesReload(value => value + 1)}>Retry templates</Button>} />}
        {importError && <Alert type="error" showIcon message={importError} />}{mappingNotice && <Alert type={mappingNotice.startsWith('Saved mapping is temporarily unavailable.') ? 'warning' : 'success'} showIcon message={mappingNotice} />}
        {!!workbookTemplate?.warnings.length && <Alert type="warning" showIcon message="Template review" description={templateWarningSummary(workbookTemplate.warnings)} />}
        {!sheet && <Form.Item label="Salary month" required extra="Choose the payroll month before uploading multiple location workbooks."><Input aria-label="Payslip salary month" type="month" value={month} onChange={event => setMonth(event.target.value)} /></Form.Item>}
        {!!sourceParts.length && !editingBatchId && <Form.Item extra="Review the source payroll units before enabling this. Matching identity, different source workbooks and split attendance within the month are required; the original work location is preserved."><Checkbox checked={reviewSourceSplits} onChange={event => reviewSplitSources(event.target.checked)}>Allow verified transfers between source payroll units</Checkbox></Form.Item>}
        {sheet && <><div className="excel-payslip-form-grid"><Form.Item label="Worksheet"><Select aria-label="Payslip worksheet" disabled={!!editingBatchId} value={sheetName} options={sheets.map(item => ({ value: item.name, label: item.name }))} onChange={value => { const next = sheets.find(item => item.name === value)!; setSheetName(value); setMonth(next.template?.month || inferPayslipMonth(next)); setSimpleLayout(next.template?.simpleLayout === true) }} /></Form.Item><Form.Item label="Header row"><InputNumber aria-label="Payslip header row" disabled={!!editingBatchId} min={1} max={100000} value={headerRow} onChange={value => setHeaderRow(value || 1)} /></Form.Item><Form.Item label="Salary month" required><Input aria-label="Payslip salary month" disabled={!!editingBatchId} type="month" value={month} onChange={event => setMonth(event.target.value)} /></Form.Item></div>
          <Form.Item label="Client salary template"><Select aria-label="Payslip salary template" loading={templatesLoading} disabled={!templatesReady || suggesting} value={templateId || ''} options={[{ value: '', label: 'Manual mapping' }, ...templates.map(item => ({ value: String(item.id), label: item.name }))]} onChange={value => { setTemplateId(value); setColumns(current => current.map(column => ({ ...column, componentId: undefined }))) }} /></Form.Item>
          <Form.Item extra="Use the compact salary slip format for this batch."><Checkbox checked={simpleLayout} onChange={event => setSimpleLayout(event.target.checked)}>Simple salary slip</Checkbox></Form.Item>
          <Alert type="info" showIcon message="Common headings are mapped automatically. Use AI suggestions for unfamiliar salary sheets." description="Saved mappings take priority. Amounts stay as stated in Excel; gross totals and employer contributions stay separate from earnings. Blank monetary cells block saving unless Treat blanks as zero is explicitly approved for that column. Original source cells remain unchanged. Blank employee codes are assigned on Save batch; serial numbers are not employee codes." action={<Button size="small" loading={suggesting} disabled={profileLoading || busy || !canManage} onClick={() => void suggestMapping()}>Use suggested mapping</Button>} />
          <div className="excel-payslip-mapping"><DataTable rows={columns} loading={mappingBusy} title="Column mapping" getRowId={column => column.columnIndex} hideSearch exportDisabled pageSizeOptions={[10, 25, 50]} columns={[
            { key: 'sourceHeader', label: 'Source column', width: 210, render: column => <><strong>{excelColumnLetter(column.columnIndex)}</strong> · {column.sourceHeader || '(blank heading)'} {mappingReview.includes(column.columnIndex) && <Tag color="orange">Review</Tag>}</> },
            { key: 'sample', label: 'First employee value', width: 170, value: column => sheet.rows.find(row => row.sourceRow > headerRow && row.cells.some(cell => cell?.value))?.cells[column.columnIndex]?.value || '' },
            { key: 'componentId', label: 'Template component', width: 240, filterable: false, sortable: false, render: column => <Select aria-label={`Column ${excelColumnLetter(column.columnIndex)} template component`} value={column.componentId || ''} disabled={!template || mappingBusy} showSearch optionFilterProp="label" options={[{ value: '', label: 'Manual / not mapped' }, ...(template?.components || []).map(component => ({ value: String(component.id), label: `${component.code} · ${component.name} (${component.category})` }))]} onChange={value => { const component = template?.components.find(item => String(item.id) === value); patchColumn(column.columnIndex, component ? { componentId: value, label: component.name, kind: categoryKind(component.category) } : { componentId: undefined }) }} style={{ width: '100%' }} /> },
            { key: 'kind', label: 'Use as', width: 205, filterable: false, sortable: false, render: column => <Select aria-label={`Column ${excelColumnLetter(column.columnIndex)} use as`} value={column.kind} disabled={mappingBusy} options={payslipColumnKinds} onChange={kind => patchColumn(column.columnIndex, { kind, componentId: undefined, blankAsZero: isPayslipAmountKind(kind) ? column.blankAsZero : undefined })} style={{ width: '100%' }} /> },
            { key: 'label', label: 'Payslip label', width: 205, filterable: false, sortable: false, render: column => <Input aria-label={`Column ${excelColumnLetter(column.columnIndex)} label`} value={column.label} maxLength={80} disabled={mappingBusy} onChange={event => patchColumn(column.columnIndex, { label: event.target.value })} /> },
            { key: 'blankAsZero', label: 'Blank amounts', width: 220, filterable: false, sortable: false, render: column => <Checkbox aria-label={`Column ${excelColumnLetter(column.columnIndex)} blanks as zero`} checked={column.blankAsZero === true} disabled={mappingBusy || !isPayslipAmountKind(column.kind)} onChange={event => patchColumn(column.columnIndex, { blankAsZero: event.target.checked })}>Treat blanks as zero</Checkbox> },
          ]} /></div>
          <Divider />
          <Space wrap><Tag color="blue">{imported.rows.length} employee rows</Tag><Tag>{imported.excludedRows.length} blank-name / total / excluded rows</Tag><Tag color={imported.issues.length ? 'red' : 'green'}>{imported.issues.length} import issues</Tag></Space>
          {!!imported.excludedRows.length && <p className="excel-payslip-muted">Excluded source rows: {imported.excludedRows.slice(0, 30).join(', ')}{imported.excludedRows.length > 30 ? '…' : ''}. Empty formatting rows are omitted.</p>}
          {!!imported.excludedDetails.length && <DataTable rows={imported.excludedDetails} title="Excluded row review" getRowId={row => row.sourceRow} exportDisabled columns={[{ key: 'sourceRow', label: 'Source row', width: 100 }, { key: 'reason', label: 'Reason', wrap: true }, { key: 'amounts', label: 'Saved values in mapped amount columns', wrap: true }]} />}
          {imported.issues.length > 0 && <DataTable rows={imported.issues} title="Resolve before saving" getRowId={(item, index) => `${item.sourceRow}:${item.column}:${index}`} exportDisabled columns={[{ key: 'sourceRow', label: 'Source row' }, { key: 'column', label: 'Column' }, { key: 'message', label: 'Issue', wrap: true }]} actions={issue => issue.sourceRow > headerRow && sheet.rows.some(row => row.sourceRow === issue.sourceRow && row.cells[columns.find(c => c.kind === 'employeeName')?.columnIndex ?? -1]?.value.trim()) ? <Tooltip title="Keep the original source row for review and prepare the other employees."><Button size="small" danger onClick={() => setExcludedRows(current => [...new Set([...current, issue.sourceRow])])}>Keep row pending</Button></Tooltip> : null} />}
          <DataTable rows={imported.rows} title="Employee preview" getRowId={row => row.id} exportDisabled columns={[{ key: 'sourceRow', label: 'Source row' }, { key: 'employeeName', label: 'Employee' }, { key: 'employeeCode', label: 'Employee code', render: row => row.employeeCode || <Tag color="blue">Auto on save</Tag> }, { key: 'netPay', label: simpleLayout ? 'In Hand Salary' : 'Net pay as stated', render: row => amount(row.netPay) }]} actions={row => <Space size={4}><Button size="small" onClick={() => setReview(row)}>Details</Button>{canManage && <Button size="small" disabled={busy || mappingBusy} onClick={() => correctRow(row)}>Correct amounts</Button>}<Button size="small" danger onClick={() => setExcludedRows(current => [...current, row.sourceRow])}>Exclude row</Button></Space>} />
          {!!excludedRows.length && <Button onClick={() => setExcludedRows([])}>Restore manually excluded rows</Button>}
        </>}
      </Form>
    </Drawer>

    <Drawer title="Saved Excel payslip batches" className="excel-payslip-history" open={historyOpen} width="min(1040px, 96vw)" onClose={() => { if (!busy) setHistoryOpen(false) }}><DataTable rows={history} loading={historyLoading} getRowId={row => row.id} title="Batch history" emptyText="No saved batches for this client." rowClassName={row => latestByMonth.get(row.month) === row.id ? 'excel-payslip-latest' : ''} columns={[{ key: 'month', label: 'Salary month' }, { key: 'version', label: 'Version', width: 112, value: row => latestByMonth.get(row.month) === row.id ? 'Latest' : 'Previous', render: row => <Tag color={latestByMonth.get(row.month) === row.id ? 'green' : 'default'}>{latestByMonth.get(row.month) === row.id ? 'Latest' : 'Previous'}</Tag> }, { key: 'sourceFileName', label: 'Source file' }, { key: 'sheetName', label: 'Worksheet' }, { key: 'rowCount', label: 'Payslips' }, { key: 'createdAtUtc', label: 'Saved at', value: row => new Date(row.createdAtUtc).toLocaleString() }, { key: 'createdBy', label: 'Saved by' }]} actionsWidth={155} actions={row => <Space direction="vertical" size={4}>{row.id === batch?.id && <Tag color="blue">Viewing</Tag>}<Button size="small" disabled={busy} onClick={() => void openHistoryBatch(row.id)}>Open batch</Button>{canConfigurePayroll && <Button size="small" disabled={busy} onClick={() => void openHistoryBatch(row.id, false, true)}>Master setup</Button>}{canManage && <Button size="small" disabled={busy} onClick={() => void editMapping(row.id)}>Edit mapping</Button>}</Space>} /></Drawer>
    <Drawer title={review ? `${review.employeeName} · Source row ${review.sourceRow}` : 'Payslip details'} open={!!review} width="min(720px, 96vw)" className="excel-payslip-row-review" onClose={() => { if (!busy) setReview(null) }} closable={!busy} footer={<Space wrap className="excel-payslip-footer"><Button disabled={busy} onClick={() => setReview(null)}>Close</Button>{canManage && review && <Button type="primary" disabled={busy || mappingBusy} onClick={() => correctRow(review)}>Correct amounts</Button>}</Space>}>{review && <><Descriptions bordered column={1} size="small"><Descriptions.Item label="Source row">{review.sourceRow}</Descriptions.Item><Descriptions.Item label="Email">{review.email || 'Not supplied'}</Descriptions.Item><Descriptions.Item label="Delivery details">{emailStatus.statuses.get(review.id)?.message || 'Not yet checked'}</Descriptions.Item><Descriptions.Item label="Employee code">{review.employeeCode || (importOpen ? 'Auto on save' : 'Not supplied')}</Descriptions.Item>{review.information.map((item, index) => <Descriptions.Item key={index} label={item.label}>{item.value || '—'}</Descriptions.Item>)}</Descriptions><PayslipReconciliation row={review} />{[['Earnings', review.earnings], ['Deductions', review.deductions], ['Employer contributions', review.employerContributions]].map(([label, items]) => <div key={String(label)}><Divider orientation="left">{String(label)}</Divider>{(items as { label: string; amount: number }[]).map((item, index) => <p className="excel-payslip-amount" key={index}><span>{item.label}</span><strong>{amount(item.amount)}</strong></p>)}</div>)}<Divider /><p className="excel-payslip-amount"><strong>{(importOpen ? simpleLayout : batch?.simpleLayout) ? 'In Hand Salary' : 'Net pay as stated'}</strong><strong>{amount(review.netPay)}</strong></p>{review.warnings.map((warning, index) => <Alert key={index} type="warning" showIcon message={warning} />)}</>}</Drawer>

    {correction?.clientId === clientId && correction.sourceRows && <ExcelPayslipBulkAmountEditor key={`bulk:${clientId}:${editingBatchId}`} sheet={correction.sheet} columns={correction.columns} sourceRows={correction.sourceRows} busy={busy} ready={templatesReady && !mappingBusy} error={importError} onClose={() => setCorrection(null)} onSave={edits => { if (!canManage || busy || !templatesReady || mappingBusy) return; try { const changed = correctPayslipBatchAmounts(correction.sheet, correction.columns, edits); if (changed.changed) void saveBatch(changed.sheet) } catch (failure) { setImportError(failure instanceof Error ? failure.message : 'Unable to save corrections.') } }} />}
    {correction?.clientId === clientId && !correction.sourceRows && <ExcelPayslipAmountEditor key={`${clientId}:${editingBatchId}:${correction.sourceRow}`} sheet={correction.sheet} columns={correction.columns} sourceRow={correction.sourceRow} busy={busy} ready={templatesReady && !mappingBusy} error={importError} onClose={() => setCorrection(null)} onSave={edits => { void saveCorrection(edits) }} />}

    <Drawer title={action?.kind === 'send' ? 'Email imported payslips' : 'Payslip PDF'} open={!!action} width="min(960px, 98vw)" onClose={() => { if (!busy) { setAction(null); setPdfUrl('') } }} closable={!busy} maskClosable={!busy} className="excel-payslip-action" footer={<Space wrap className="excel-payslip-footer"><Button disabled={busy} onClick={() => { setAction(null); setPdfUrl('') }}>Close</Button>{pdfUrl && <><Button onClick={() => iframe.current?.contentWindow?.print()}>Print PDF</Button><Button href={pdfUrl} download={`payslips-${batch?.month}.pdf`}>Download PDF</Button></>}{action?.kind === 'send' ? <Button type="primary" loading={busy} disabled={!canManage || (requestingEmailIds ? !emailRequestRows.length || needsAck : needsAck) || (!pendingSendIds.length && !selectedMailRows.some(row => canEmailRow(row.id, sendMode, combinedEmail) && !delivery.some(item => item.rowId === row.id && item.status !== 'Error')))} onClick={() => void send()}>{sendProgress || (requestingEmailIds ? attachRequestPayslips ? 'Request IDs & send combined PDF' : 'Request email IDs' : pendingSendIds.length ? 'Retry / check email request' : delivery.length ? 'Send remaining emails' : 'Send email')}</Button> : <Button type="primary" loading={busy} disabled={needsAck || !actionRows.length} onClick={() => void createPdf()}>{action?.kind === 'download' ? 'Generate & download PDF' : 'Generate preview'}</Button>}</Space>}>
      <Space wrap><Tag>{actionRows.length} payslips</Tag><Tag>{batch?.month}</Tag><Checkbox checked={includeSeal} disabled={busy || pendingSendIds.length > 0 || !!pendingEmailRequest.current} onChange={event => { setIncludeSeal(event.target.checked); setPdfUrl('') }}>Include company seal</Checkbox></Space>
      <Form.Item label="Amount display" style={{ marginTop: 16 }}><Select aria-label="Payslip amount display" value={amountDecimalPlaces} disabled={busy || pendingSendIds.length > 0 || !!pendingEmailRequest.current} options={[{ value: 0, label: 'Whole rupees' }, { value: 2, label: '2 decimal places' }]} onChange={value => { setAmountDecimalPlaces(value); setPdfUrl('') }} style={{ width: 210 }} /></Form.Item>
      {actionError && <Alert type="error" showIcon message={actionError} />}
      {action?.kind === 'send' && <ExcelPayslipMailProgress jobs={mailJobs.jobs} selectedId={linkedMailJobId || preferredMailJob} onSelect={chooseMailJob} loading={mailJobs.loading} error={mailJobs.error} onRefresh={mailJobs.refresh} />}
      {(!requestingEmailIds || attachRequestPayslips) && !!warningRows.length && <><Alert type="warning" showIcon message={`${warningRows.length} payslips have financial review warnings. The imported net pay remains unchanged.`} /><DataTable rows={warningRows} getRowId={row => row.id} exportDisabled columns={[{ key: 'sourceRow', label: 'Source row' }, { key: 'employeeName', label: 'Employee' }, { key: 'warnings', label: 'Warnings', value: row => row.warnings.join('; '), wrap: true }]} /><Checkbox checked={acknowledgeWarnings} disabled={busy} onChange={event => setAcknowledgeWarnings(event.target.checked)}>I reviewed these warnings and want to use the imported amounts.</Checkbox></>}
      {action?.kind === 'send' && <><Alert type="info" showIcon message="Only unsent payslips are included. You can also request missing email IDs without sending their payslips." description={requestingEmailIds ? attachRequestPayslips ? 'The employee details and one combined salary PDF for employees missing email IDs will go to the recipient below. Personal employee email delivery remains unsent.' : 'The request contains employee codes, names and work locations. No salary PDF is attached, and payslips remain unsent.' : `After saving the email job, delivery continues in the background. Track progress here or in Notifications.${sendMode === 'Combined' ? ' Combined delivery sends ONE coordinator email. It does not send employee emails or mark them as sent.' : ''}`} />{emailStatus.error && <Alert type="error" showIcon message={emailStatus.error} action={<Button onClick={emailStatus.refresh}>Retry status</Button>} />}{sendMode === 'Individual' && missingEmailRows.length > 0 && <Alert type="warning" showIcon message={`${missingEmailRows.length} recipients need an email. Bulk sending queues valid recipients and reports missing or invalid emails per row.`} />}<Divider /><Form component="div" layout="vertical" disabled={busy}><Form.Item label="Email delivery"><Select aria-label="Payslip email delivery" disabled={pendingSendIds.length > 0 || !!pendingEmailRequest.current} value={sendMode} options={[{ value: 'Individual', label: 'Individual PDF to each employee' }, { value: 'Combined', label: 'Combined PDF to one email address' }]} onChange={value => { setSendMode(value); setNextEmailMode(value); setDelivery([]); setExcludedRecipientIds([]) }} /></Form.Item>{sendMode === 'Combined' ? <><Form.Item label="Recipient email" required><Input aria-label="Combined payslip recipient" disabled={pendingSendIds.length > 0 || !!pendingEmailRequest.current} type="email" value={combinedEmail} onChange={event => setCombinedEmail(event.target.value)} /></Form.Item>{!!emailRequestRows.length && <Checkbox checked={requestMissingEmails} disabled={pendingSendIds.length > 0 || !!pendingEmailRequest.current} onChange={event => setRequestMissingEmails(event.target.checked)}>Request missing email IDs</Checkbox>}{requestingEmailIds && <><p><Checkbox checked={attachRequestPayslips} disabled={pendingSendIds.length > 0 || !!pendingEmailRequest.current} onChange={event => { setAttachRequestPayslips(event.target.checked); setAcknowledgeWarnings(false) }}>Attach their salary slips as one combined PDF</Checkbox></p><DataTable rows={emailRequestRows} getRowId={row => row.id} exportDisabled title="Employees needing email IDs" columns={[{ key: 'employeeCode', label: 'Employee Code' }, { key: 'employeeName', label: 'Employee Name' }, { key: 'location', label: 'Work Location', value: row => row.information.find(item => /location|tehsil/i.test(item.label))?.value || '' }]} /></>}</> : <><Space wrap style={{ marginBottom: 12 }}><Checkbox checked={excludeMissingRecipients} disabled={busy || pendingSendIds.length > 0} onChange={event => setExcludeMissingRecipients(event.target.checked)}>Exclude missing / invalid email</Checkbox><Button disabled={busy || pendingSendIds.length > 0} onClick={() => { setExcludedRecipientIds([]); setExcludeMissingRecipients(true) }}>Select all with email</Button><Button disabled={busy || pendingSendIds.length > 0} onClick={() => setExcludedRecipientIds(actionRows.map(row => row.id))}>Clear recipient selection</Button><Tag>{selectedMailRows.length} selected; {missingEmailRows.length} email missing / invalid</Tag></Space><DataTable rows={actionRows} getRowId={row => row.id} title="Recipients for this send" exportDisabled rowSelection={{ selectedRowKeys: selectedMailRows.map(row => row.id), onChange: keys => setExcludedRecipientIds(actionRows.filter(row => !keys.includes(row.id)).map(row => row.id)), getCheckboxProps: row => ({ disabled: busy || pendingSendIds.length > 0 || !canEmailRow(row.id) || excludeMissingRecipients && !emailValid((emailOverrides[row.id] ?? row.email).trim()) }) }} columns={[{ key: 'sourceRow', label: 'Source row' }, { key: 'employeeName', label: 'Employee' }, { key: 'email', label: 'Email override', render: row => <Input aria-label={`Email for source row ${row.sourceRow}`} disabled={pendingSendIds.includes(row.id) || delivery.some(item => item.rowId === row.id && item.status !== 'Error')} value={emailOverrides[row.id] ?? row.email} placeholder="Email required only for sending" onChange={event => setEmailOverrides(current => ({ ...current, [row.id]: event.target.value }))} /> }]} /></>}</Form><p className="excel-payslip-muted">Email overrides apply to this send. They do not edit the saved batch or employee records.</p>{!!delivery.length && <DataTable rows={delivery} getRowId={(row, index) => `${row.rowId}:${index}`} title="Email results" columns={[{ key: 'employeeName', label: 'Employee' }, { key: 'email', label: 'Recipient' }, { key: 'status', label: 'Status' }, { key: 'message', label: 'Details', wrap: true }]} />}</>}
      {pdfUrl && <iframe ref={iframe} title="Imported payslip PDF preview" src={pdfUrl} className="excel-payslip-pdf" />}
    </Drawer>
  </section>
}
