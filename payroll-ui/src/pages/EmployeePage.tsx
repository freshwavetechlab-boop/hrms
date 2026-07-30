import { useEffect, useState } from 'react'
import { Chk, F, Sel } from '../components/FormPrimitives'
import BulkUploadPreviewModal, { emptyBulkUploadPreview, type BulkUploadPreviewColumnMeta, type BulkUploadPreviewSheet, type BulkUploadPreviewState } from '../components/BulkUploadPreviewModal'
import BulkUploadProgressModal, { type BulkUploadState, type BulkUploadSummary } from '../components/BulkUploadProgressModal'
import DataTable, { type Column } from '../components/DataTable'
import SearchSelect, { selectOptions } from '../components/SearchSelect'
import { useToast } from '../components/ToastProvider'
import { employee0, setup0 } from '../data/payrollDefaults'
import { getClients, getEmployees } from '../services/payrollService'
import { deleteEmployee as removeEmployee, downloadEmployeeImportTemplate, getDropdowns, getEmployeeDeletePreview, getEmployeeImportJob, getEmployeeInfotypes, getEmployeeManagerUsers, getSetup, getWorkLocations, preflightEmployeeImport, processEmployeeAction, saveEmployee as persistEmployee, startEmployeeImport, type EmployeeImportDecision, type EmployeeImportPreflight } from '../services/settingsService'
import type { Client, Component, Drop, Employee, EmployeeActionRequest, EmployeeInfotypeRecord, EmployeePaymentDetails, EmployeePersonalDetails, Setup, Structure, WorkLocation, WorkflowApprover } from '../types/payroll'
import { calculateSalaryJson, calculateSalaryTotals, canOverrideSalaryComponent, money } from '../utils/salary'
import { parseImportPreviewSheets, validateImportPreview, type ImportPreviewData, type ImportPreviewIssue, type ImportPreviewRules, type ImportPreviewSheet } from '../utils/importPreview'
import { buildXlsxBlob } from '../utils/xlsx'
import { safeJsonRecord } from '../shared/json'
import EmployeeAttachmentPanel from '../components/EmployeeAttachmentPanel'
import EmployeeDynamicFields from '../components/EmployeeDynamicFields'
import EntityAttachmentPanel from '../components/EntityAttachmentPanel'
import SmartBulkUploadMapper from '../components/SmartBulkUploadMapper'
import EmployeeImportReviewModal from '../components/EmployeeImportReviewModal'
import { employeeBulkImportDefinition } from '../config/bulkImportDefinitions'
import type { BulkImportOperation, PreparedBulkImport } from '../utils/smartBulkImport'
import { getEmployeeActivity360 } from '../services/recruitmentTalentService'
import type { PersonActivityEvent } from '../types/payroll'
import '../components/Employee360.css'
import '../TemplateDesigner.css'

const employeeInfotypes = [
  { code: '0000', name: 'Actions' },
  { code: '0001', name: 'Organizational Assignment' },
  { code: '0002', name: 'Personal Data' },
  { code: '0006', name: 'Addresses' },
  { code: '0008', name: 'Basic Pay' },
  { code: '0009', name: 'Bank Details' },
  { code: 'DOCS', name: 'Documents' },
  { code: '360', name: 'Employee 360 Activity' }
] as const
type EmployeeInfotypeCode = typeof employeeInfotypes[number]['code']
const personal0 = employee0.personalDetails
const payment0 = employee0.paymentDetails
const wait = (ms: number) => new Promise(resolve => window.setTimeout(resolve, ms))

export type EmployeePageView = 'master' | 'org'

export default function EmployeePage({ view = 'master' }: { view?: EmployeePageView }) {
  const notify = useToast()
  const [clients, setClients] = useState<Client[]>([]), [locations, setLocations] = useState<WorkLocation[]>([]), [drops, setDrops] = useState<Drop[]>([]), [setup, setSetup] = useState<Setup>(setup0), [managerUsers, setManagerUsers] = useState<WorkflowApprover[]>([])
  const [employees, setEmployees] = useState<Employee[]>([]), [employee, setEmployee] = useState(employee0), [employeeInfotype, setEmployeeInfotype] = useState<EmployeeInfotypeCode>('0001')
  const [salaryOverrides, setSalaryOverrides] = useState<Record<string, string>>({})
  const [infotypes, setInfotypes] = useState<EmployeeInfotypeRecord[]>([])
  const [changeReason, setChangeReason] = useState('')
  const [modalOpen, setModalOpen] = useState(false), [clientFilter, setClientFilter] = useState(0), [locationFilter, setLocationFilter] = useState(0), [query, setQuery] = useState('')
  const [upload, setUpload] = useState<{ open: boolean; state: BulkUploadState; percent: number; summary: BulkUploadSummary }>({ open: false, state: 'uploading', percent: 0, summary: { totalRows: 0 } })
  const [templateDownloaded, setTemplateDownloaded] = useState(false)
  const [bulkMapperOpen, setBulkMapperOpen] = useState(false)
  const [preview, setPreview] = useState<BulkUploadPreviewState>(emptyBulkUploadPreview)
  const [mappedPreviewColumns, setMappedPreviewColumns] = useState<Record<string, BulkUploadPreviewColumnMeta>>({})
  const [previewConfirm, setPreviewConfirm] = useState<null | ((draft: BulkUploadPreviewState) => Promise<void>)>(null)
  const [previewImporting, setPreviewImporting] = useState(false)
  const [employeePreviewSource, setEmployeePreviewSource] = useState<{ sheets: ImportPreviewSheet[]; clientId: number; fileName: string; operation: BulkImportOperation } | null>(null)
  const [importReview, setImportReview] = useState<{ file: File; clientId: number; operation: BulkImportOperation; review: EmployeeImportPreflight } | null>(null)
  const [importReviewBusy, setImportReviewBusy] = useState(false)
  const clientStructure = templatesForClient(setup.salaryStructures, employee.clientId)[0]
  const chosenStructure = setup.salaryStructures.find(item => String(item.id) === employee.salaryStructureId) ?? clientStructure
  const rawEmployeeSalary = salaryRecord(employee)
  const structureLineIds = chosenStructure?.lines.map(line => line.componentId) ?? []
  const employeeSalary = chosenStructure && employee.annualCtc ? safeJsonRecord(calculateSalaryJson(employee.annualCtc, setup.salaryComponents, chosenStructure, salaryOverrides)) : rawEmployeeSalary
  const structureComponents = setup.salaryComponents.filter(component => component.active && structureLineIds.includes(String(component.id))).sort((a, b) => structureLineIds.indexOf(String(a.id)) - structureLineIds.indexOf(String(b.id)) || Number(a.priority) - Number(b.priority))
  const deps = drops.filter(item => item.type === 'Department' && item.isActive).map(item => item.value), desigs = drops.filter(item => item.type === 'Designation' && item.isActive).map(item => item.value)
  const grades = drops.filter(item => item.type === 'Employee Grade' && item.isActive && (!item.clientId || item.clientId === employee.clientId)).map(item => item.value)

  const load = async () => {
    const [clientRows, locationRows, dropdownRows, employeeRows, rawSetup, managerRows] = await Promise.all([getClients(), getWorkLocations(), getDropdowns(), getEmployees(), getSetup(setup0), getEmployeeManagerUsers()])
    const activeClientIds = new Set(clientRows.map(client => client.id))
    const activeLocations = locationRows.filter(location => location.isActive && activeClientIds.has(location.clientId))
    setClients(clientRows); setLocations(activeLocations); setDrops(dropdownRows); setEmployees(employeeRows.filter(employee => activeClientIds.has(employee.clientId)).map(normalizeEmployeeDetails))
    setManagerUsers(managerRows)
    setSetup({ ...setup0, ...rawSetup, salaryComponents: rawSetup.salaryComponents ?? [], salaryStructures: rawSetup.salaryStructures ?? [] })
  }

  useEffect(() => { void load() }, [])
  useEffect(() => { setTemplateDownloaded(false) }, [clientFilter])
  const changeClientFilter = (id: number) => { setClientFilter(id); setLocationFilter(0) }
  const calcSalary = (ctc: number, salaryStructure = chosenStructure, overrides: Record<string, string | number> = {}) => calculateSalaryJson(ctc, setup.salaryComponents, salaryStructure, overrides)
  const withSalary = (row: Employee, salaryJson: string): Employee => ({ ...row, salaryJson, salaryComponents: numberRecord(salaryJson) })
  const inferSalaryOverrides = (row: Employee, salaryStructure?: Structure) => {
    if (!salaryStructure || !row.annualCtc) return {}
    const stored = salaryRecord(row)
    const baseline = safeJsonRecord(calcSalary(row.annualCtc, salaryStructure))
    const componentById = new Map(setup.salaryComponents.map(component => [String(component.id), component]))
    return Object.fromEntries(Object.entries(stored).filter(([componentId, value]) => {
      const component = componentById.get(componentId)
      if (!component || !canOverrideSalaryComponent(component)) return false
      return Math.abs(Number(value || 0) - Number(baseline[componentId] || 0)) > 0.009
    }))
  }
  const normalizeEmployeeSalary = (row: Employee, overrides?: Record<string, string | number>) => {
    row = normalizeEmployeeDetails(row)
    const salaryStructure = setup.salaryStructures.find(item => String(item.id) === row.salaryStructureId) ?? templatesForClient(setup.salaryStructures, row.clientId)[0]
    if (!salaryStructure || !row.annualCtc) return row
    const normalized = String(row.salaryStructureId) === String(salaryStructure.id) ? row : { ...row, salaryStructureId: String(salaryStructure.id) }
    return withSalary(normalized, calcSalary(row.annualCtc, salaryStructure, overrides ?? inferSalaryOverrides(row, salaryStructure)))
  }
  const empLine = (componentId: string, value: string) => {
    const nextOverrides = { ...salaryOverrides, [componentId]: value }
    setSalaryOverrides(nextOverrides)
    setEmployee(withSalary(employee, calcSalary(employee.annualCtc, chosenStructure, nextOverrides)))
  }
  const empMonthly = (component: Component) => Number(employeeSalary[String(component.id)] || 0)
  const applyStructure = (id: string) => { const selectedId = id.split(':')[0]; const selectedStructure = setup.salaryStructures.find(item => String(item.id) === selectedId); const ctc = Number(selectedStructure?.annualCtc || employee.annualCtc || 0); setSalaryOverrides({}); setEmployee(withSalary({ ...employee, salaryStructureId: selectedId, annualCtc: ctc }, calcSalary(ctc, selectedStructure))) }
  const applyCtc = (ctc: number) => setEmployee(withSalary({ ...employee, salaryStructureId: chosenStructure ? String(chosenStructure.id) : employee.salaryStructureId, annualCtc: ctc }, calcSalary(ctc, chosenStructure, salaryOverrides)))
  const applyClient = (value: string) => { const clientId = Number(value.split(':')[0] || 0); const selectedStructure = templatesForClient(setup.salaryStructures, clientId)[0]; const ctc = Number(selectedStructure?.annualCtc || employee.annualCtc || 0); setSalaryOverrides({}); setEmployee(withSalary({ ...employee, clientId, salaryStructureId: selectedStructure ? String(selectedStructure.id) : '', annualCtc: ctc }, selectedStructure ? calcSalary(ctc, selectedStructure) : '{}')) }
  const newEmployee = () => {
    const selectedStructure = templatesForClient(setup.salaryStructures, clientFilter)[0]
    const ctc = Number(selectedStructure?.annualCtc || 0)
    setSalaryOverrides({})
    setEmployee(clientFilter ? withSalary({ ...employee0, clientId: clientFilter, salaryStructureId: selectedStructure ? String(selectedStructure.id) : '', annualCtc: ctc }, selectedStructure ? calcSalary(ctc, selectedStructure) : '{}') : employee0)
    setEmployeeInfotype('0001'); setChangeReason(''); setModalOpen(true)
  }
  const loadEmployeeHistory = async (id: number) => {
    setInfotypes(await getEmployeeInfotypes(id))
  }
  const editEmployee = (row: Employee) => { const normalized = normalizeEmployeeDetails(row); const salaryStructure = setup.salaryStructures.find(item => String(item.id) === normalized.salaryStructureId) ?? templatesForClient(setup.salaryStructures, normalized.clientId)[0]; const overrides = inferSalaryOverrides(normalized, salaryStructure); setSalaryOverrides(overrides); setEmployee(normalizeEmployeeSalary(normalized, overrides)); setEmployeeInfotype('0001'); setChangeReason(''); setModalOpen(true); void loadEmployeeHistory(row.id) }
  const closeModal = () => { setModalOpen(false); setEmployee(employee0); setSalaryOverrides({}); setEmployeeInfotype('0001'); setChangeReason(''); setInfotypes([]) }
  const saveEmployee = async () => {
    const isNew = !employee.id
    const response = await persistEmployee(toEmployeePayload(normalizeEmployeeSalary(employee, salaryOverrides)), employeeInfotype, changeReason)
    if (!response.ok) { notify(response.error || 'Unable to save employee.', 'error'); return }
    if (isNew) {
      const saved = { ...employee, id: response.data.id }
      setEmployee(saved)
      setEmployeeInfotype('DOCS')
      await load()
      await loadEmployeeHistory(saved.id)
      notify('Employee created. Add the configured documents now.', 'success')
      return
    }
    closeModal(); await load(); notify('Employee saved successfully.', 'success')
  }
  const runEmployeeAction = async (request: EmployeeActionRequest) => {
    const response = await processEmployeeAction(request)
    if (!response.ok || !response.data) { notify(response.error || 'Employee action could not be processed.', 'error'); return }
    const normalizedDetails = normalizeEmployeeDetails(response.data)
    const salaryStructure = setup.salaryStructures.find(item => String(item.id) === normalizedDetails.salaryStructureId) ?? templatesForClient(setup.salaryStructures, normalizedDetails.clientId)[0]
    const overrides = inferSalaryOverrides(normalizedDetails, salaryStructure)
    const normalized = normalizeEmployeeSalary(normalizedDetails, overrides)
    setSalaryOverrides(overrides); setEmployee(normalized); await loadEmployeeHistory(normalized.id); await load(); notify(`${request.actionType} saved with history.`, 'success')
  }
  const deleteEmployee = async (row: Employee) => {
    const preview = await getEmployeeDeletePreview(row.id)
    if (preview.links.length) { notify(`Cannot delete ${preview.employeeName || row.employeeCode}. Linked records: ${preview.links.join(' | ')}`, 'warning'); return }
    if (!window.confirm(`Delete employee ${row.employeeCode}?`)) return
    const response = await removeEmployee(row.id)
    notify(response.ok ? 'Employee deleted.' : response.error || 'Unable to delete employee.', response.ok ? 'success' : 'error')
    if (response.ok) await load()
  }
  const downloadTemplate = async (operation?: BulkImportOperation, selectedFieldCodes?: string[]) => {
    if (!clientFilter) { setUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors: ['Select a client before downloading employee template.'] } }); return }
    if (operation && selectedFieldCodes?.length) {
      const selected = employeeBulkImportDefinition.fields.filter(field => selectedFieldCodes.includes(field.code) || field.required)
      const headers = [...(operation === 'update' ? ['Employee ID'] : []), ...selected.map(field => field.header)]
      const rows = operation === 'update'
        ? employees.filter(row => row.clientId === clientFilter).map(row => [String(row.id), ...selected.map(field => employeeImportFieldValue(row, field.code, locations, managerUsers, setup.salaryStructures))])
        : []
      const instructions = [
        ['Mode', operation],
        ['Rule', operation === 'update' ? 'Only existing Employee IDs are updated. Blank cells preserve current values.' : operation === 'insert' ? 'Only new Employee Codes are accepted.' : 'Existing Employee Codes update; new codes insert.'],
        ['Selected fields', selected.map(field => field.header).join(', ')]
      ]
      const blob = buildXlsxBlob([{ name: 'Employees', rows: [headers, ...rows] }, { name: 'Instructions', rows: instructions }])
      saveBlob(blob, `employee-${operation}-selected-fields.xlsx`)
      setTemplateDownloaded(true)
      notify(`${operation === 'update' ? 'Update' : 'Insert'} template downloaded with ${selected.length} selected field(s).`, 'info')
      return
    }
    const response = await downloadEmployeeImportTemplate(clientFilter)
    if (!response.ok || !response.data) { setUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors: [response.error || 'Unable to download employee template.'] } }); return }
    saveBlob(response.data, 'employee-import-template.xlsx')
    setTemplateDownloaded(true)
    notify('Employee import template downloaded.', 'info')
  }
  const runEmployeeImport = async (file: File, importClientId = clientFilter, operation: BulkImportOperation = 'upsert', reviewToken = '', decisions: EmployeeImportDecision[] = []) => {
    setUpload({ open: true, state: 'uploading', percent: 1, summary: { totalRows: 0 } })
    const start = await startEmployeeImport(importClientId, file, operation, reviewToken, decisions)
    if (!start.ok || !start.data.jobId) { setUpload({ open: true, state: 'error', percent: 100, summary: { ...start.data, errors: start.data.errors?.length ? start.data.errors : [start.error || 'Upload failed.'] } }); return }
    let job = start.data
    while (job.state === 'Queued' || job.state === 'Processing') {
      const percent = job.totalRows && job.completedRows >= job.totalRows ? 99 : job.totalRows ? Math.min(98, Math.round((job.completedRows / job.totalRows) * 100)) : 5
      setUpload({ open: true, state: 'uploading', percent, summary: job })
      await wait(700)
      job = await getEmployeeImportJob(job.jobId)
    }
    const percent = job.totalRows ? Math.round((job.completedRows / job.totalRows) * 100) : 100
    if (job.state === 'Completed') { setUpload({ open: true, state: 'success', percent: 100, summary: job }); await load(); return }
    setUpload({ open: true, state: 'error', percent, summary: { ...job, errors: job.errors?.length ? job.errors : ['Import failed. No rows were saved.'] } })
  }
  const reviewEmployeeImportIdentity = async (file: File, importClientId: number, operation: BulkImportOperation) => {
    const result = await preflightEmployeeImport(importClientId, file, operation)
    if (!result.ok) {
      setUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors: [result.error || 'Employee identity preflight failed. Import was not started.'] } })
      return
    }
    const review = normalizeEmployeeImportPreflight(result.data)
    if (!review.reviewToken) {
      setUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: review.totalRows, errors: ['Employee identity preflight did not return a review token. Import was blocked.'] } })
      return
    }
    if (employeePreflightNeedsReview(review)) {
      setImportReview({ file, clientId: importClientId, operation, review })
      return
    }
    if (!review.canImport) {
      setUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: review.totalRows, errors: review.errors?.length ? review.errors : ['Employee identity preflight blocked this import.'] } })
      return
    }
    await runEmployeeImport(file, importClientId, operation, review.reviewToken, [])
  }
  const confirmEmployeeImportReview = async (decisions: EmployeeImportDecision[]) => {
    if (!importReview) return
    const context = importReview
    setImportReviewBusy(true)
    setImportReview(null)
    try { await runEmployeeImport(context.file, context.clientId, context.operation, context.review.reviewToken, decisions) }
    finally { setImportReviewBusy(false) }
  }
  const previewEmployeeUpload = async (file: File, columnMeta: Record<string, BulkUploadPreviewColumnMeta> = {}, operation: BulkImportOperation = 'upsert') => {
    try {
      const data = await parseEmployeePreviewData(file)
      const importClientId = clientFilter || data.clientId || 0
      if (!clientFilter && data.clientId) setClientFilter(data.clientId)
      const clientIssues: ImportPreviewIssue[] = importClientId ? [] : [{ rowNumber: 1, column: 'Client', message: 'Select a client before import or upload a template with Client in References sheet.' }]
      setMappedPreviewColumns(columnMeta)
      setPreview({ open: true, title: 'Employee bulk upload preview', fileName: file.name, headers: data.headers, rows: data.rows, issues: [...clientIssues, ...data.issues], sheets: data.sheets, columnMeta })
      setPreviewConfirm(() => async (draft: BulkUploadPreviewState) => reviewEmployeeImportIdentity(employeePreviewFile(draft, data.sourceSheets, file.name), importClientId, operation))
      setEmployeePreviewSource({ sheets: data.sourceSheets, clientId: importClientId, fileName: file.name, operation })
    } catch (error) {
      setUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors: [error instanceof Error ? error.message : 'Unable to preview employee import file.'] } })
    }
  }
  const confirmEmployeePreview = async (draft: BulkUploadPreviewState) => {
    if (!previewConfirm) return
    const action = previewConfirm
    setPreviewImporting(true)
    setPreview(emptyBulkUploadPreview)
    setPreviewConfirm(null)
    try { await action(draft) } finally { setPreviewImporting(false) }
  }
  const uploadEmployees = async (file: File | null) => {
    if (!file) return
    await previewEmployeeUpload(file)
  }
  const openEmployeeBulkUpload = () => {
    if (!clientFilter) { notify('Select a client before starting employee bulk upload.', 'warning'); return }
    setBulkMapperOpen(true)
  }
  const reviewMappedEmployeeUpload = async (result: PreparedBulkImport) => {
    setBulkMapperOpen(false)
    notify(`${result.mappedFields} fields mapped. ${result.skippedColumns} unused source column(s) will be skipped.`, 'info')
    const columnMeta = Object.fromEntries(result.columns.map(column => [column.targetHeader, { sourceHeader: column.sourceHeader, color: column.color, kind: column.kind } satisfies BulkUploadPreviewColumnMeta] as const))
    await previewEmployeeUpload(result.file, columnMeta, result.operation)
  }
  const reviewTemplateEmployeeUpload = async (file: File, operation: BulkImportOperation) => {
    setBulkMapperOpen(false)
    setMappedPreviewColumns({})
    await previewEmployeeUpload(file, {}, operation)
  }
  const resolveEmployeeDuplicates = async (mode: 'skip' | 'replace' | 'replaceAll', sheetName: string) => {
    if (!employeePreviewSource) return
    const resolvedSheets = resolveDuplicateEmployeeSheets(employeePreviewSource.sheets, mode, sheetName)
    const fileName = employeePreviewSource.fileName.replace(/\.xlsx$/i, '-resolved.xlsx')
    const resolvedFile = new File([buildXlsxBlob(resolvedSheets.map(sheet => ({ name: sheet.name, rows: [sheet.headers, ...sheet.rows] })))], fileName, { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
    const data = await parseEmployeePreviewData(resolvedFile)
    const clientIssues: ImportPreviewIssue[] = employeePreviewSource.clientId ? [] : [{ rowNumber: 1, column: 'Client', message: 'Select a client before import or upload a template with Client in References sheet.' }]
    setPreview({ open: true, title: 'Employee bulk upload preview', fileName: resolvedFile.name, headers: data.headers, rows: data.rows, issues: [...clientIssues, ...data.issues], sheets: data.sheets, columnMeta: mappedPreviewColumns })
    setPreviewConfirm(() => async (draft: BulkUploadPreviewState) => reviewEmployeeImportIdentity(employeePreviewFile(draft, data.sourceSheets, resolvedFile.name), employeePreviewSource.clientId, employeePreviewSource.operation))
    setEmployeePreviewSource({ sheets: data.sourceSheets, clientId: employeePreviewSource.clientId, fileName: resolvedFile.name, operation: employeePreviewSource.operation })
  }
  const visibleEmployees = employees.filter(row => row.isActive && (!clientFilter || row.clientId === clientFilter) && (!locationFilter || row.workLocationId === locationFilter) && `${row.employeeCode} ${row.firstName} ${row.lastName} ${row.department} ${row.designation} ${row.workEmail} ${workLocationName(locations, row.workLocationId)}`.toLowerCase().includes(query.toLowerCase()))

  return <section className="employee-master">
    {view === 'master' ? <EmployeeDirectory clients={clients} locations={locations} employees={visibleEmployees} allCount={employees.length} clientFilter={clientFilter} setClientFilter={changeClientFilter} locationFilter={locationFilter} setLocationFilter={setLocationFilter} query={query} setQuery={setQuery} templateDownloaded={templateDownloaded} onNew={newEmployee} onEdit={editEmployee} onDelete={deleteEmployee} onDownloadTemplate={downloadTemplate} onBulkUpload={openEmployeeBulkUpload} /> : <EmployeeOrgStructure clients={clients} locations={locations} employees={employees.filter(row => row.isActive)} clientFilter={clientFilter} setClientFilter={changeClientFilter} />}
    {modalOpen && <div className="employee-modal-backdrop" onClick={closeModal}>
      <section className="employee-modal" role="dialog" aria-modal="true" aria-label="Employee details" onClick={event => event.stopPropagation()}>
        <EmployeePanel employee={employee} setEmployee={row => setEmployee(normalizeEmployeeSalary(row, salaryOverrides))} employeeInfotype={employeeInfotype} setEmployeeInfotype={value => { setEmployeeInfotype(value as EmployeeInfotypeCode); setChangeReason('') }} changeReason={changeReason} setChangeReason={setChangeReason} clients={clients} locations={locations} managerUsers={managerUsers} templates={setup.salaryStructures} salaryComponents={setup.salaryComponents} deps={deps} desigs={desigs} grades={grades} applyClient={applyClient} applyStructure={applyStructure} applyCtc={applyCtc} structureComponents={structureComponents} employeeSalary={employeeSalary} salaryOverrides={salaryOverrides} empLine={empLine} empMonthly={empMonthly} saveEmployee={saveEmployee} closeModal={closeModal} infotypes={infotypes} runEmployeeAction={runEmployeeAction} />
      </section>
    </div>}
    <SmartBulkUploadMapper open={bulkMapperOpen} definition={employeeBulkImportDefinition} clientCode={clients.find(client => client.id === clientFilter)?.code ?? ''} existingEmployeeCodes={employees.filter(row => row.clientId === clientFilter).map(row => row.employeeCode)} onCancel={() => setBulkMapperOpen(false)} onPrepared={reviewMappedEmployeeUpload} onTemplateFile={reviewTemplateEmployeeUpload} onDownloadTemplate={downloadTemplate} />
    <BulkUploadProgressModal open={upload.open} title="Employee bulk upload" state={upload.state} percent={upload.percent} summary={upload.summary} onClose={() => setUpload(current => ({ ...current, open: false }))} />
    <BulkUploadPreviewModal preview={preview} importing={previewImporting} uniqueFields={[["Employee Code"]]} onCancel={() => { setPreview(emptyBulkUploadPreview); setMappedPreviewColumns({}); setPreviewConfirm(null); setEmployeePreviewSource(null) }} onConfirm={draft => void confirmEmployeePreview(draft)} onResolveDuplicates={(mode, sheetName) => void resolveEmployeeDuplicates(mode, sheetName)} />
    <EmployeeImportReviewModal key={importReview?.review.reviewToken ?? 'closed'} open={Boolean(importReview)} fileName={importReview?.file.name ?? ''} operation={importReview?.operation ?? 'upsert'} review={importReview?.review ?? null} busy={importReviewBusy} onCancel={() => setImportReview(null)} onConfirm={decisions => void confirmEmployeeImportReview(decisions)} />
  </section>
}

function normalizeEmployeeImportPreflight(value: EmployeeImportPreflight): EmployeeImportPreflight {
  const rows = Array.isArray(value?.rows) ? value.rows.map((row, index) => ({
    ...row,
    rowNumber: Number(row?.rowNumber || index + 2),
    sheet: String(row?.sheet || 'Employees'),
    proposedEmployeeCode: String(row?.proposedEmployeeCode || ''),
    matchStatus: String(row?.matchStatus || 'New'),
    matchedEmployeeId: Number(row?.matchedEmployeeId || 0) || null,
    matchedEmployeeCode: row?.matchedEmployeeCode ? String(row.matchedEmployeeCode) : null,
    matchedEmployeeName: row?.matchedEmployeeName ? String(row.matchedEmployeeName) : null,
    matchReasons: Array.isArray(row?.matchReasons) ? row.matchReasons.map(String).filter(Boolean) : [],
    blockingReasons: Array.isArray(row?.blockingReasons) ? row.blockingReasons.map(String).filter(Boolean) : [],
    changes: Array.isArray(row?.changes) ? row.changes.map(change => ({
      ...change,
      field: String(change?.field || ''),
      label: String(change?.label || change?.field || 'Field'),
      oldValue: change?.oldValue === null || change?.oldValue === undefined ? '' : String(change.oldValue),
      newValue: change?.newValue === null || change?.newValue === undefined ? '' : String(change.newValue),
      sensitive: Boolean(change?.sensitive),
      payrollImpact: Boolean(change?.payrollImpact)
    })) : [],
    candidateEmployees: Array.isArray(row?.candidateEmployees) ? row.candidateEmployees.map(candidate => ({
      employeeId: Number(candidate?.employeeId || 0),
      employeeCode: String(candidate?.employeeCode || ''),
      employeeName: String(candidate?.employeeName || ''),
      matchReasons: Array.isArray(candidate?.matchReasons) ? candidate.matchReasons.map(String).filter(Boolean) : [],
      changes: Array.isArray(candidate?.changes) ? candidate.changes.map(change => ({
        ...change,
        field: String(change?.field || ''),
        label: String(change?.label || change?.field || 'Field'),
        oldValue: change?.oldValue === null || change?.oldValue === undefined ? '' : String(change.oldValue),
        newValue: change?.newValue === null || change?.newValue === undefined ? '' : String(change.newValue),
        sensitive: Boolean(change?.sensitive),
        payrollImpact: Boolean(change?.payrollImpact)
      })) : []
    })).filter(candidate => candidate.employeeId > 0) : [],
    identityEvidence: Array.isArray(row?.identityEvidence) ? row.identityEvidence.map(evidence => ({
      field: String(evidence?.field || ''),
      label: String(evidence?.label || evidence?.field || 'Identifier'),
      uploadedValue: String(evidence?.uploadedValue || ''),
      sensitive: Boolean(evidence?.sensitive),
      candidates: Array.isArray(evidence?.candidates) ? evidence.candidates.map(candidate => ({
        employeeId: Number(candidate?.employeeId || 0),
        employeeCode: String(candidate?.employeeCode || ''),
        employeeName: String(candidate?.employeeName || ''),
        existingValue: String(candidate?.existingValue || '')
      })).filter(candidate => candidate.employeeId > 0) : []
    })) : [],
    canResolveConflict: Boolean(row?.canResolveConflict)
  })) : []
  return {
    reviewToken: String(value?.reviewToken || ''),
    totalRows: Number(value?.totalRows || rows.length),
    canImport: value?.canImport === true,
    requiresConfirmation: Boolean(value?.requiresConfirmation),
    rows,
    errors: Array.isArray(value?.errors) ? value.errors.map(String).filter(Boolean) : []
  }
}

function employeePreflightNeedsReview(review: EmployeeImportPreflight) {
  return review.rows.some(row => {
    const status = String(row.matchStatus || '').toLowerCase().replace(/[^a-z0-9]/g, '')
    const reasons = row.matchReasons.map(reason => String(reason || '').toLowerCase().replace(/[^a-z0-9]/g, ''))
    const identifierMatch = ['matchedbyidentifier', 'identifiermatch', 'secondaryidentifier', 'identitymatch'].some(value => status.includes(value))
      || reasons.some(reason => ['mobile', 'phone', 'aadhaar', 'aadhar', 'pan', 'bankaccount', 'nameaddress'].some(value => reason.includes(value)))
    const conflict = ['conflict', 'blocked', 'ambiguous', 'multiple'].some(value => status.includes(value))
    const probable = ['probable', 'possible', 'similar', 'nameaddress'].some(value => status.includes(value))
    return conflict
      || probable
      || identifierMatch
      || Boolean(row.canResolveConflict)
      || (row.candidateEmployees?.length ?? 0) > 1
      || row.blockingReasons.length > 0
      || row.changes.some(change => change.sensitive || change.payrollImpact || sensitiveEmployeeImportField(change.field || change.label))
  })
}

function sensitiveEmployeeImportField(value: string) {
  const key = String(value || '').toLowerCase().replace(/[^a-z0-9]/g, '')
  return ['mobile', 'phone', 'aadhaar', 'aadhar', 'pan', 'bank', 'ifsc', 'salary', 'ctc', 'payment', 'portalaccess', 'active', 'workemail'].some(field => key.includes(field))
}

function EmployeeOrgStructure(p: { clients: Client[]; locations: WorkLocation[]; employees: Employee[]; clientFilter: number; setClientFilter: (id: number) => void }) {
  const [orientation, setOrientation] = useState<'vertical' | 'horizontal'>('vertical')
  const [zoom, setZoom] = useState(1)
  const [selectedEmployee, setSelectedEmployee] = useState<Employee | null>(null)
  const clientRows = p.clients.filter(client => !p.clientFilter || client.id === p.clientFilter)
  const employeeCount = clientRows.reduce((sum, client) => sum + p.employees.filter(employee => employee.clientId === client.id).length, 0)
  const changeZoom = (delta: number) => setZoom(current => Math.min(1.6, Math.max(.55, Number((current + delta).toFixed(2)))))
  const resetZoom = () => setZoom(1)
  return <section className="card employee-org-page">
    <header><i className="blue">O</i><div><h3>Client-wise org structure</h3><p>Visual reporting hierarchy by client. Each tile uses active employee master data.</p></div></header>
    <div className="employee-org-toolbar">
      <label><span>Client</span><SearchSelect value={p.clientFilter} onChange={value => p.setClientFilter(Number(value))} options={selectOptions(p.clients.map(client => ({ value: client.id, label: client.name })), 'All clients', 0)} /></label>
      <div><span>Total employees</span><b>{employeeCount}</b></div>
      <div className="employee-org-view-toggle"><span>View</span><div><button type="button" className={orientation === 'vertical' ? 'active' : ''} onClick={() => setOrientation('vertical')}>Vertical</button><button type="button" className={orientation === 'horizontal' ? 'active' : ''} onClick={() => setOrientation('horizontal')}>Horizontal</button></div></div>
      <div className="employee-org-zoom"><span>Zoom</span><div><button type="button" onClick={() => changeZoom(-.1)}>-</button><b>{Math.round(zoom * 100)}%</b><button type="button" onClick={() => changeZoom(.1)}>+</button><button type="button" onClick={resetZoom}>Reset</button></div></div>
    </div>
    <div className="employee-org-client-list">
      {clientRows.map(client => <ClientOrgChart key={client.id} client={client} employees={p.employees.filter(employee => employee.clientId === client.id)} locations={p.locations} orientation={orientation} zoom={zoom} onZoom={changeZoom} onSelectEmployee={setSelectedEmployee} />)}
      {!clientRows.length && <p className="empty">No client found for org structure.</p>}
    </div>
    {selectedEmployee && <EmployeeOrgDetails employee={selectedEmployee} clients={p.clients} locations={p.locations} employees={p.employees} onClose={() => setSelectedEmployee(null)} />}
  </section>
}

function ClientOrgChart(p: { client: Client; employees: Employee[]; locations: WorkLocation[]; orientation: 'vertical' | 'horizontal'; zoom: number; onZoom: (delta: number) => void; onSelectEmployee: (employee: Employee) => void }) {
  const active = p.employees.filter(employee => employee.isActive)
  const employeeIds = new Set(active.map(employee => employee.id))
  const childrenByManager = new Map<number, Employee[]>()
  active.forEach(employee => {
    const managerId = employee.reportingManagerId || 0
    if (managerId && employeeIds.has(managerId)) childrenByManager.set(managerId, [...(childrenByManager.get(managerId) ?? []), employee])
  })
  childrenByManager.forEach(rows => rows.sort(employeeSort))
  const roots = active.filter(employee => !employee.reportingManagerId || !employeeIds.has(employee.reportingManagerId)).sort(employeeSort)
  return <section className="employee-org-client">
    <div className="employee-org-client-title">
      <div><span>Client</span><h3>{p.client.name}</h3></div>
      <b>{active.length} employee{active.length === 1 ? '' : 's'}</b>
    </div>
    {roots.length ? <div className="employee-org-canvas" onWheel={event => { event.preventDefault(); p.onZoom(event.deltaY > 0 ? -.05 : .05) }}>
      <div className={`employee-org-tree ${p.orientation}`} style={{ transform: `scale(${p.zoom})` }}>
        {roots.map(employee => <OrgNode key={employee.id} employee={employee} childrenByManager={childrenByManager} locations={p.locations} level={0} onSelectEmployee={p.onSelectEmployee} />)}
      </div>
    </div> : <p className="empty">No active employees available for this client.</p>}
  </section>
}

function OrgNode(p: { employee: Employee; childrenByManager: Map<number, Employee[]>; locations: WorkLocation[]; level: number; onSelectEmployee: (employee: Employee) => void }) {
  const children = p.childrenByManager.get(p.employee.id) ?? []
  const initials = `${p.employee.firstName?.[0] ?? ''}${p.employee.lastName?.[0] ?? ''}`.trim() || p.employee.employeeCode.slice(0, 2)
  return <div className={`employee-org-node level-${Math.min(p.level, 4)}`}>
    <EmployeeOrgTile employee={p.employee} initials={initials.toUpperCase()} locationName={workLocationName(p.locations, p.employee.workLocationId)} childCount={children.length} onClick={() => p.onSelectEmployee(p.employee)} />
    {children.length > 0 && <div className="employee-org-children">{children.map(child => <OrgNode key={child.id} employee={child} childrenByManager={p.childrenByManager} locations={p.locations} level={p.level + 1} onSelectEmployee={p.onSelectEmployee} />)}</div>}
  </div>
}

function EmployeeOrgTile(p: { employee: Employee; initials: string; locationName: string; childCount: number; onClick: () => void }) {
  const name = `${p.employee.firstName} ${p.employee.lastName}`.trim() || p.employee.employeeCode
  return <button type="button" className="employee-org-tile" onClick={p.onClick}>
    <div className="employee-org-avatar">{p.initials}</div>
    <div className="employee-org-info">
      <strong>{name}</strong>
      <span>{p.employee.employeeCode}</span>
      <small>{p.employee.designation || 'Designation not assigned'}</small>
      <em>{[p.employee.department, p.locationName].filter(Boolean).join(' / ') || 'Org assignment pending'}</em>
    </div>
    {p.childCount > 0 && <b>{p.childCount}</b>}
  </button>
}

function EmployeeOrgDetails(p: { employee: Employee; clients: Client[]; locations: WorkLocation[]; employees: Employee[]; onClose: () => void }) {
  const name = `${p.employee.firstName} ${p.employee.lastName}`.trim() || p.employee.employeeCode
  const manager = p.employees.find(employee => employee.id === p.employee.reportingManagerId)
  const managerName = manager ? `${manager.firstName} ${manager.lastName}`.trim() || manager.employeeCode : 'Not assigned'
  const rows = [
    ['Employee code', p.employee.employeeCode],
    ['Client', p.clients.find(client => client.id === p.employee.clientId)?.name || '-'],
    ['Work location', workLocationName(p.locations, p.employee.workLocationId) || '-'],
    ['Department', p.employee.department || '-'],
    ['Designation', p.employee.designation || '-'],
    ['Grade', p.employee.grade || '-'],
    ['Reporting manager', managerName],
    ['Joining date', p.employee.dateOfJoining || '-'],
    ['Work email', p.employee.workEmail || '-'],
    ['Portal access', p.employee.portalAccess ? 'Enabled' : 'Disabled']
  ]
  return <div className="employee-org-detail-backdrop" onClick={p.onClose}>
    <section className="employee-org-detail" role="dialog" aria-modal="true" aria-label="Employee org details" onClick={event => event.stopPropagation()}>
      <header><div><span>Employee details</span><h3>{name}</h3><p>{p.employee.employeeCode} / {p.employee.designation || 'Designation not assigned'}</p></div><button type="button" onClick={p.onClose}>Close</button></header>
      <div className="employee-org-detail-grid">{rows.map(([label, value]) => <div key={label}><span>{label}</span><b>{value}</b></div>)}</div>
    </section>
  </div>
}

function employeeSort(a: Employee, b: Employee) {
  return `${a.firstName} ${a.lastName} ${a.employeeCode}`.localeCompare(`${b.firstName} ${b.lastName} ${b.employeeCode}`)
}

type EmployeePreviewData = ImportPreviewData & { issues: ImportPreviewIssue[]; sheets: BulkUploadPreviewSheet[]; sourceSheets: ImportPreviewSheet[]; clientId?: number }

async function parseEmployeePreviewData(file: File): Promise<EmployeePreviewData> {
  const sheets = await parseImportPreviewSheets(file)
  const clientId = employeeClientIdFromSheets(sheets)
  const importSheets = sheets.filter(sheet => !['references', 'reference', 'masters', 'instructions'].includes(sheet.name.trim().toLowerCase()) && sheet.rows.length)
  const previewSheets = importSheets.map(sheet => {
    const headers = sheet.headers
    const rows = sheet.rows.map(row => headers.map(header => previewCell(headers, row, header)))
    const issues = validateImportPreview({ headers, rows }, employeeSheetPreviewRules(headers))
    return { name: sheet.name, headers, rows, issues }
  })
  const rows = previewSheets.flatMap(sheet => sheet.rows)
  const issues = previewSheets.flatMap(sheet => (sheet.issues ?? []).map(issue => ({ ...issue, message: `${sheet.name}: ${issue.message}` })))
  if (!rows.length) throw new Error('No employee import rows found.')
  return { headers: previewSheets[0]?.headers ?? [], rows, issues, sheets: previewSheets, sourceSheets: sheets, clientId }
}

function previewCell(headers: string[], row: string[], name: string) {
  const index = headers.findIndex(header => importNorm(header) === importNorm(name))
  const value = index >= 0 ? row[index] ?? '' : ''
  return ['dateofjoining', 'dateofbirth'].includes(importNorm(name)) ? previewDate(value) : value
}

function importNorm(value: string) {
  return value.replace(/[\s_-]/g, '').toLowerCase()
}

function employeeClientIdFromSheets(sheets: ImportPreviewSheet[]) {
  for (const sheet of sheets) {
    if (!['references', 'reference', 'masters', 'instructions'].includes(sheet.name.trim().toLowerCase())) continue
    for (const row of sheet.rows) {
      const type = previewCell(sheet.headers, row, 'Reference Type') || previewCell(sheet.headers, row, 'Master Type')
      if (importNorm(type) !== 'client') continue
      const id = Number(previewCell(sheet.headers, row, 'Id') || previewCell(sheet.headers, row, 'Client Id'))
      if (Number.isFinite(id) && id > 0) return id
    }
  }
  return 0
}

function employeeSheetPreviewRules(headers: string[]): ImportPreviewRules {
  const has = (name: string) => headers.some(header => importNorm(header) === importNorm(name))
  return {
    required: ['Employee Code'],
    unique: [['Employee Code']],
    booleans: ['Portal Access', 'Active'].filter(has),
    numbers: ['Work Location Id', 'Annual CTC'].filter(has),
    dates: ['Date Of Joining', 'Date Of Birth'].filter(has),
    enums: {
      ...(has('Gender') ? { Gender: ['Male', 'Female', 'Other'] } : {}),
      ...(has('Payment Mode') ? { 'Payment Mode': ['Bank Transfer', 'Cheque', 'Cash'] } : {})
    },
    custom: (row, rowNumber) => {
      const issues: ImportPreviewIssue[] = []
      const salaryJson = row['Salary Json']?.trim()
      if (salaryJson) {
        try { const parsed = JSON.parse(salaryJson); if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) issues.push({ rowNumber, column: 'Salary Json', message: 'Salary Json must be a JSON object.' }) }
        catch { issues.push({ rowNumber, column: 'Salary Json', message: 'Salary Json must be valid JSON.' }) }
      }
      const email = row['Work Email']?.trim()
      if (email && !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)) issues.push({ rowNumber, column: 'Work Email', message: 'Work Email must be a valid email address.' })
      const managerEmail = row['Reporting Manager Email']?.trim()
      if (managerEmail && !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(managerEmail)) issues.push({ rowNumber, column: 'Reporting Manager Email', message: 'Reporting Manager Email must be a valid email address.' })
      return issues
    }
  }
}

function resolveDuplicateEmployeeSheets(sheets: ImportPreviewSheet[], mode: 'skip' | 'replace' | 'replaceAll', sheetName: string) {
  return sheets.map(sheet => {
    if (['references', 'reference', 'masters', 'instructions'].includes(sheet.name.trim().toLowerCase())) return sheet
    if (mode === 'replace' && sheet.name !== sheetName) return sheet
    const index = sheet.headers.findIndex(header => importNorm(header) === 'employeecode')
    if (index < 0) return sheet
    const rows = mode === 'skip' ? keepFirstEmployeeRows(sheet.rows, index) : keepLastEmployeeRows(sheet.rows, index)
    return { ...sheet, rows }
  })
}

function employeePreviewFile(draft: BulkUploadPreviewState, sourceSheets: ImportPreviewSheet[], fileName: string) {
  const isInfotype = (name: string) => /^000[12689]\b/i.test(name.trim())
  const cleanSheetName = (name: string) => isInfotype(name) ? name : 'Employees'
  const importNames = new Set((draft.sheets?.map(sheet => sheet.name.toLowerCase()) ?? []))
  const importSheets = draft.sheets?.length
    ? draft.sheets.map(sheet => ({ name: cleanSheetName(sheet.name), rows: [sheet.headers.map(header => header.trim()), ...sheet.rows] }))
    : [{ name: 'Employees', rows: [draft.headers.map(header => header.trim()), ...draft.rows] }]
  const referenceSheets = sourceSheets
    .filter(sheet => !importNames.has(sheet.name.toLowerCase()))
    .map(sheet => ({ name: sheet.name, rows: [sheet.headers, ...sheet.rows] }))
  const name = fileName.replace(/\.[^.]+$/i, '') + '-preview.xlsx'
  return new File([buildXlsxBlob([...importSheets, ...referenceSheets])], name, { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
}

function keepFirstEmployeeRows(rows: string[][], codeIndex: number) {
  const seen = new Set<string>()
  return rows.filter(row => {
    const code = (row[codeIndex] ?? '').trim().toLowerCase()
    if (!code) return true
    if (seen.has(code)) return false
    seen.add(code)
    return true
  })
}

function keepLastEmployeeRows(rows: string[][], codeIndex: number) {
  const order: string[] = []
  const byCode = new Map<string, string[]>()
  const blanks: string[][] = []
  for (const row of rows) {
    const code = (row[codeIndex] ?? '').trim().toLowerCase()
    if (!code) { blanks.push(row); continue }
    if (!byCode.has(code)) order.push(code)
    byCode.set(code, row)
  }
  return [...order.map(code => byCode.get(code)!).filter(Boolean), ...blanks]
}

function saveBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.click()
  URL.revokeObjectURL(url)
}

function previewDate(value: string) {
  const text = value.trim()
  const serial = Number(text)
  if (Number.isFinite(serial) && serial >= 20000 && serial <= 80000) {
    const date = new Date(Date.UTC(1899, 11, 30) + serial * 86400000)
    return date.toISOString().slice(0, 10)
  }
  return value
}

function EmployeeDirectory(p: { clients: Client[]; locations: WorkLocation[]; employees: Employee[]; allCount: number; clientFilter: number; setClientFilter: (id: number) => void; locationFilter: number; setLocationFilter: (id: number) => void; query: string; setQuery: (value: string) => void; templateDownloaded: boolean; onNew: () => void; onEdit: (employee: Employee) => void; onDelete: (employee: Employee) => void; onDownloadTemplate: () => void; onBulkUpload: () => void }) {
  const clientName = (id: number) => p.clients.find(client => client.id === id)?.name ?? `Client #${id || '-'}`
  const locationName = (id: number) => workLocationName(p.locations, id)
  const locationOptions = p.locations.filter(location => !p.clientFilter || location.clientId === p.clientFilter).map(location => ({ value: location.id, label: p.clientFilter ? location.name : `${location.name} - ${clientName(location.clientId)}` }))
  return <section className="card employee-directory"><header><i className="blue">E</i><div><h3>Employee master</h3><p>Search client-wise employees. Create or edit details in a focused popup.</p></div><div className="employee-directory-actions"><button type="button" disabled={!p.clientFilter} title={p.clientFilter ? 'Download Excel template' : 'Select a client first'} onClick={p.onDownloadTemplate}>Download Excel template</button><button type="button" data-testid="employee-bulk-upload-open" className="employee-upload-action" disabled={!p.clientFilter} title={!p.clientFilter ? 'Select a client first' : 'Use a template or map any Excel/CSV file'} onClick={p.onBulkUpload}>Bulk upload</button><button type="button" onClick={p.onNew}>New employee</button></div></header>
    <div className="employee-directory-tools"><label><span>Client</span><SearchSelect testId="employee-client-filter" value={p.clientFilter} onChange={value => p.setClientFilter(Number(value))} options={selectOptions(p.clients.map(client => ({ value: client.id, label: client.name })), 'All clients', 0)} /></label><label><span>Work Location</span><SearchSelect value={p.locationFilter} onChange={value => p.setLocationFilter(Number(value))} options={selectOptions(locationOptions, 'All locations', 0)} /></label><label><span>Search</span><input value={p.query} onChange={event => p.setQuery(event.target.value)} placeholder="Code, name, location, department, email..." /></label><div className="employee-directory-count"><span>Showing</span><b>{p.employees.length} / {p.allCount}</b></div></div>
    <DataTable rows={p.employees} emptyText="No employees found for the selected filters." exportFileName="employees" columns={[
      { key: 'employeeName', label: 'Employee', value: row => `${row.firstName} ${row.lastName}`.trim(), render: row => <strong>{row.firstName} {row.lastName}</strong> },
      { key: 'employeeCode', label: 'Code' },
      { key: 'clientName', label: 'Client', value: row => clientName(row.clientId) },
      { key: 'workLocationName', label: 'Work Location', value: row => locationName(row.workLocationId) },
      { key: 'department', label: 'Department' },
      { key: 'designation', label: 'Designation' },
      { key: 'grade', label: 'Grade' },
      { key: 'workEmail', label: 'Work email' },
      { key: 'status', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
    ]} actions={row => <span className="row-actions"><button type="button" onClick={() => p.onEdit(row)}>Edit</button><button type="button" className="danger" onClick={() => p.onDelete(row)}>Delete</button></span>} />
  </section>
}

function EmployeePanel(p: { employee: Employee; setEmployee: (employee: Employee) => void; employeeInfotype: EmployeeInfotypeCode; setEmployeeInfotype: (code: string) => void; changeReason: string; setChangeReason: (value: string) => void; clients: Client[]; locations: WorkLocation[]; managerUsers: WorkflowApprover[]; templates: Structure[]; salaryComponents: Component[]; deps: string[]; desigs: string[]; grades: string[]; applyClient: (value: string) => void; applyStructure: (value: string) => void; applyCtc: (value: number) => void; structureComponents: Component[]; employeeSalary: Record<string, string>; salaryOverrides: Record<string, string>; empLine: (id: string, value: string) => void; empMonthly: (component: Component) => number; saveEmployee: () => void; closeModal: () => void; infotypes: EmployeeInfotypeRecord[]; runEmployeeAction: (request: EmployeeActionRequest) => Promise<void> }) {
  const personal = p.employee.personalDetails, payment = p.employee.paymentDetails
  const salaryRows = p.structureComponents.map(component => ({ component, monthly: p.empMonthly(component), annual: p.empMonthly(component) * 12 }))
  const totals = calculateSalaryTotals(salaryRows.map(row => ({ line: { componentId: String(row.component.id), value: '' }, ...row })))
  const badgeClass = (category: string) => category.toLowerCase().replace(/\s+/g, '-')
  const setPersonal = <K extends keyof EmployeePersonalDetails>(key: K, value: EmployeePersonalDetails[K]) => p.setEmployee({ ...p.employee, personalDetails: { ...personal, [key]: value } })
  const copyCorrespondence = (checked: boolean) => p.setEmployee({ ...p.employee, personalDetails: { ...personal, permanentAddress: checked ? personal.correspondenceAddress : personal.permanentAddress } })
  const setPayment = <K extends keyof EmployeePaymentDetails>(key: K, value: EmployeePaymentDetails[K]) => p.setEmployee({ ...p.employee, paymentDetails: { ...payment, [key]: value } })
  const selectedInfotype = employeeInfotypes.find(item => item.code === p.employeeInfotype) ?? employeeInfotypes[1]
  const managerOptions = [{ value: '', label: 'No reporting manager' }, ...p.managerUsers.filter(user => !p.employee.clientId || !user.clientId || user.clientId === p.employee.clientId).map(user => ({ value: user.id, label: `${user.displayName || user.email} / ${user.email}${user.clientName ? ` / ${user.clientName}` : ''}` }))]
  return <section className="employee-card"><header><div><span className="eyebrow purple">{p.employee.id ? 'Edit employee' : 'New employee'}</span><h3>{p.employee.id ? `${p.employee.firstName} ${p.employee.lastName}`.trim() || p.employee.employeeCode : 'Employee details'}</h3><p>{p.employee.employeeCode || 'New code'} / {clientNameFor(p.clients, p.employee.clientId)} / {p.employee.isActive ? 'Active' : 'Inactive'}</p></div><button type="button" className="employee-modal-close" onClick={p.closeModal}>x</button></header>
    <section className="employee-basic-strip"><article><span>Code</span><b>{p.employee.employeeCode || '-'}</b></article><article><span>DOJ</span><b>{dateText(p.employee.dateOfJoining) || '-'}</b></article><article><span>Grade</span><b>{p.employee.grade || '-'}</b></article><article><span>Location</span><b>{workLocationName(p.locations, p.employee.workLocationId)}</b></article><article><span>Annual CTC</span><b>{money(p.employee.annualCtc || 0)}</b></article></section>
    <label className="employee-infotype-picker"><span>Infotype</span><SearchSelect value={p.employeeInfotype} onChange={value => p.setEmployeeInfotype(String(value))} options={employeeInfotypes.map(item => ({ value: item.code, label: `${item.code} - ${item.name}` }))} /></label>
    <section className="employee-infotype-editor"><h4>{selectedInfotype.code} - {selectedInfotype.name}</h4>
    {p.employeeInfotype === '0000' && <EmployeeActionEditor employee={p.employee} locations={p.locations} deps={p.deps} desigs={p.desigs} grades={p.grades} runEmployeeAction={p.runEmployeeAction} />}
    {p.employeeInfotype === '0001' && <div className="grid"><F l="Client"><Sel v={String(p.employee.clientId || '')} set={p.applyClient} a={p.clients.map(item => `${item.id}:${item.name}`)} /></F><F l="Employee code"><input value={p.employee.employeeCode} onChange={event => p.setEmployee({ ...p.employee, employeeCode: event.target.value })} /></F><F l="Work email"><input value={p.employee.workEmail} onChange={event => p.setEmployee({ ...p.employee, workEmail: event.target.value })} /></F><F l="Department"><Sel v={p.employee.department} set={value => p.setEmployee({ ...p.employee, department: value })} a={p.deps} /></F><F l="Designation"><Sel v={p.employee.designation} set={value => p.setEmployee({ ...p.employee, designation: value })} a={p.desigs} /></F><F l="Employee Grade"><Sel v={p.employee.grade} set={value => p.setEmployee({ ...p.employee, grade: value })} a={p.grades} /></F><F l="Work location"><Sel v={String(p.employee.workLocationId || '')} set={value => p.setEmployee({ ...p.employee, workLocationId: Number(value.split(':')[0] || 0) })} a={p.locations.filter(item => item.clientId === p.employee.clientId).map(item => `${item.id}:${item.name}`)} /></F><F l="Reporting manager"><SearchSelect value={p.employee.reportingManagerUserId ?? ''} onChange={value => p.setEmployee({ ...p.employee, reportingManagerUserId: value ? Number(value) : null })} options={managerOptions} /></F><Chk l="Portal access" v={p.employee.portalAccess} set={value => p.setEmployee({ ...p.employee, portalAccess: value })} /><Chk l="Active" v={p.employee.isActive} set={value => p.setEmployee({ ...p.employee, isActive: value })} /></div>}
    {p.employeeInfotype === '0002' && <div className="grid"><F l="First name"><input value={p.employee.firstName} onChange={event => p.setEmployee({ ...p.employee, firstName: event.target.value })} /></F><F l="Last name"><input value={p.employee.lastName} onChange={event => p.setEmployee({ ...p.employee, lastName: event.target.value })} /></F><F l="Gender"><Sel v={p.employee.gender} set={value => p.setEmployee({ ...p.employee, gender: value })} a={['Male', 'Female', 'Other']} /></F><F l="Date of joining"><input type="date" value={p.employee.dateOfJoining} onChange={event => p.setEmployee({ ...p.employee, dateOfJoining: event.target.value })} /></F><F l="Date of birth"><input type="date" value={personal.dateOfBirth || ''} onChange={event => setPersonal('dateOfBirth', event.target.value)} /></F><F l="PAN"><input value={personal.panNumber || ''} onChange={event => setPersonal('panNumber', event.target.value)} /></F><F l="Aadhaar"><input value={personal.aadhaarNumber || ''} onChange={event => setPersonal('aadhaarNumber', event.target.value)} /></F><F l="UAN Number"><input value={personal.uanNumber || ''} onChange={event => setPersonal('uanNumber', event.target.value)} /></F><F l="Mobile"><input value={personal.mobile || ''} onChange={event => setPersonal('mobile', event.target.value)} /></F></div>}
    {p.employeeInfotype === '0006' && <div className="grid"><F l="Address" w><input value={personal.address || ''} onChange={event => setPersonal('address', event.target.value)} /></F><F l="Correspondence Address" w><input value={personal.correspondenceAddress || ''} onChange={event => setPersonal('correspondenceAddress', event.target.value)} /></F><label className="employee-same-address"><input type="checkbox" checked={!!personal.correspondenceAddress && personal.permanentAddress === personal.correspondenceAddress} onChange={event => copyCorrespondence(event.target.checked)} />Same as correspondence address</label><F l="Permanent Address" w><input value={personal.permanentAddress || ''} onChange={event => setPersonal('permanentAddress', event.target.value)} /></F></div>}
    {p.employeeInfotype === '0008' && <div className="employee-salary-panel">
      <div className="employee-salary-controls"><F l="Salary template"><Sel v={p.employee.salaryStructureId} set={p.applyStructure} a={templatesForClient(p.templates, p.employee.clientId).map(item => `${item.id}:${item.name}`)} /></F><F l="Annual CTC"><input value={p.employee.annualCtc} onChange={event => p.applyCtc(Number(event.target.value.replace(/\D/g, '')))} /></F></div>
      <div className="employee-salary-summary"><article><span>Monthly gross</span><b>{money(totals.gross)}</b></article><article><span>Deductions</span><b>{money(totals.deductions)}</b></article><article><span>Monthly net</span><b>{money(totals.net)}</b></article><article><span>Annual CTC</span><b>{money(p.employee.annualCtc)}</b></article></div>
      <div className="employee-salary-table">
        <div className="employee-salary-row employee-salary-head"><span>Component</span><span>Name</span><span>Monthly</span><span>Annual</span><span>Override</span></div>
        {salaryRows.length ? salaryRows.map(({ component, monthly, annual }) => <div className="employee-salary-row" key={component.id}>
          <div className="employee-salary-code"><span className={`salary-badge ${badgeClass(component.category)}`}>{component.category}</span><b title={component.code}>{component.code}</b></div>
          <strong title={component.name}>{component.name}</strong>
          <output>{money(monthly)}</output>
          <output>{money(annual)}</output>
          <input inputMode="decimal" value={p.salaryOverrides[String(component.id)] ?? p.employeeSalary[String(component.id)] ?? ''} readOnly={!canOverrideSalaryComponent(component)} title={canOverrideSalaryComponent(component) ? 'Employee-specific override' : 'Calculated from salary component master and template'} onChange={event => p.empLine(String(component.id), event.target.value.replace(/[^\d.-]/g, ''))} aria-label={`${component.name} override`} />
        </div>) : <p className="employee-salary-empty">Select a client and salary template, then enter Annual CTC to calculate the salary breakup.</p>}
      </div>
    </div>}
    {p.employeeInfotype === '0009' && <div className="grid"><F l="Bank"><input value={payment.bankName || ''} onChange={event => setPayment('bankName', event.target.value)} /></F><F l="Account no"><input value={payment.bankAccountNo || ''} onChange={event => setPayment('bankAccountNo', event.target.value)} /></F><F l="IFSC"><input value={payment.ifscCode || ''} onChange={event => setPayment('ifscCode', event.target.value)} /></F><F l="Payment mode"><Sel v={payment.paymentMode || ''} set={value => setPayment('paymentMode', value)} a={['Bank Transfer', 'Cheque', 'Cash']} /></F></div>}
    {p.employeeInfotype === 'DOCS' && <EmployeeAttachmentPanel employeeId={p.employee.id} clientId={p.employee.clientId} />}
    {p.employeeInfotype === '360' && <EmployeeActivity360 employeeId={p.employee.id} clientId={p.employee.clientId} />}
    {p.employeeInfotype !== '0000' && p.employeeInfotype !== 'DOCS' && p.employeeInfotype !== '360' && <div className="grid"><F l="Change reason" w><input value={p.changeReason} onChange={event => p.setChangeReason(event.target.value)} placeholder="Reason for this infotype change" /></F></div>}
    </section>
    {p.employee.id > 0 && p.employee.clientId > 0 && !['0000', 'DOCS', '360'].includes(p.employeeInfotype) && <EmployeeDynamicFields employeeId={p.employee.id} clientId={p.employee.clientId} infotypeCode={p.employeeInfotype} changeReason={p.changeReason} />}
    <InfotypeHistory employee={p.employee} infotypeCode={p.employeeInfotype} infotypes={p.infotypes} clients={p.clients} locations={p.locations} managerUsers={p.managerUsers} templates={p.templates} />
    <div className="actions">
      <button type="button" className="secondary" onClick={p.closeModal}>{p.employeeInfotype === 'DOCS' || p.employeeInfotype === '360' ? 'Close' : 'Cancel'}</button>
      {p.employeeInfotype === 'DOCS' && !p.employee.id && <button type="button" onClick={() => p.setEmployeeInfotype('0001')}>Enter employee details</button>}
      {p.employeeInfotype !== 'DOCS' && p.employeeInfotype !== '360' && <button type="button" disabled={p.employeeInfotype === '0000'} onClick={p.saveEmployee}>Save infotype</button>}
    </div></section>
}

function EmployeeActivity360({ employeeId, clientId }: { employeeId: number; clientId: number }) {
  const [events, setEvents] = useState<PersonActivityEvent[]>([])
  const [module, setModule] = useState('')
  useEffect(() => { if (employeeId) void getEmployeeActivity360(employeeId).then(setEvents) }, [employeeId])
  if (!employeeId) return <p className="employee-salary-empty">Save the employee first to view the consolidated activity timeline.</p>
  const modules = Array.from(new Set(events.map(row => row.moduleCode).filter(Boolean)))
  const visible = module ? events.filter(row => row.moduleCode === module) : events
  const candidateId = events.find(row => Number(row.candidateId) > 0)?.candidateId || 0
  return <section className="employee-360-panel">
    <header><div><h4>Person-centric activity timeline</h4><p>Recruitment history, infotype changes and secure document actions remain connected after candidate-to-employee conversion.</p></div><SearchSelect value={module} onChange={value => setModule(String(value))} options={selectOptions(modules, 'All modules')} /></header>
    <div className="employee-360-timeline">{visible.map(row => <article key={`${row.moduleCode}-${row.eventType}-${row.id}`}><i /><div><b>{row.eventTitle}</b><p>{row.eventSummary || row.eventType}</p><small>{new Date(row.occurredAt).toLocaleString('en-IN')} · {row.actorName || 'System'} · {row.moduleCode}</small></div></article>)}{!visible.length && <p>No consolidated activity is available yet.</p>}</div>
    {candidateId > 0 && <EntityAttachmentPanel entityType="CANDIDATE" entityId={candidateId} clientId={clientId} moduleCode="RECRUITMENT" formCodes={['EMPLOYEE_REFERRAL', 'CANDIDATE_APPLICATION', 'PRE_ONBOARDING']} title="Recruitment documents" description="Read-only candidate documents retained in the global document system; files are not duplicated during employee conversion." readOnly />}
  </section>
}

function EmployeeActionEditor(p: { employee: Employee; locations: WorkLocation[]; deps: string[]; desigs: string[]; grades: string[]; runEmployeeAction: (request: EmployeeActionRequest) => Promise<void> }) {
  const [action, setAction] = useState<EmployeeActionRequest>(() => actionFromEmployee(p.employee))
  useEffect(() => { setAction(actionFromEmployee(p.employee)) }, [p.employee.id])
  const set = <K extends keyof EmployeeActionRequest>(key: K, value: EmployeeActionRequest[K]) => setAction(current => ({ ...current, [key]: value }))
  const submit = () => {
    void p.runEmployeeAction({ ...action, employeeId: p.employee.id, salaryStructureId: '', annualCtc: 0, salaryJson: '{}' })
  }
  if (!p.employee.id) return <p className="employee-salary-empty">Save the employee first to enable infotype actions.</p>
  return <section className="employee-action-box"><div className="grid"><F l="Action"><Sel v={action.actionType} set={value => set('actionType', value)} a={['Promotion', 'Demotion', 'Transfer', 'Retire', 'Terminate', 'Resign', 'Rehire']} /></F><F l="Effective date"><input type="date" value={action.effectiveDate} onChange={event => set('effectiveDate', event.target.value)} /></F><F l="Reason"><input value={action.reason} onChange={event => set('reason', event.target.value)} /></F><F l="Department"><Sel v={action.department} set={value => set('department', value)} a={p.deps} /></F><F l="Designation"><Sel v={action.designation} set={value => set('designation', value)} a={p.desigs} /></F><F l="Grade"><Sel v={action.grade} set={value => set('grade', value)} a={p.grades} /></F><F l="Work location"><Sel v={String(action.workLocationId || '')} set={value => set('workLocationId', Number(value.split(':')[0] || 0))} a={p.locations.filter(item => item.clientId === p.employee.clientId).map(item => `${item.id}:${item.name}`)} /></F></div><button type="button" onClick={submit}>Save action</button></section>
}

function InfotypeHistory(p: { employee: Employee; infotypeCode: EmployeeInfotypeCode; infotypes: EmployeeInfotypeRecord[]; clients: Client[]; locations: WorkLocation[]; managerUsers: WorkflowApprover[]; templates: Structure[] }) {
  if (!p.employee.id || p.infotypeCode === 'DOCS' || p.infotypeCode === '360') return null
  const rows = p.infotypes.filter(row => row.infotypeCode === p.infotypeCode).sort((a, b) => statusRank(a.status) - statusRank(b.status) || String(b.effectiveFrom).localeCompare(String(a.effectiveFrom)))
  const infotypeName = employeeInfotypes.find(item => item.code === p.infotypeCode)?.name ?? 'Infotype'
  return <section className="employee-history-grid single"><div><h4>{p.infotypeCode} - {infotypeName} historical records</h4><DataTable rows={rows} getRowId={row => row.id} emptyText="No records for this infotype yet." exportFileName={`employee-infotypes-${p.employee.employeeCode}-${p.infotypeCode}`} columns={infotypeHistoryColumns(p.infotypeCode, p)} /></div></section>
}

function infotypeHistoryColumns(code: EmployeeInfotypeCode, refs: { clients: Client[]; locations: WorkLocation[]; managerUsers: WorkflowApprover[]; templates: Structure[] }): Column<EmployeeInfotypeRecord>[] {
  const common: Column<EmployeeInfotypeRecord>[] = [
    { key: 'status', label: 'Status', width: '110px', render: row => <span className={`employee-status-pill ${row.status.toLowerCase()}`}>{row.status}</span>, value: row => row.status },
    { key: 'actionType', label: 'Action', width: '130px' },
    { key: 'effectiveFrom', label: 'From', width: '120px', value: row => dateText(row.effectiveFrom) },
    { key: 'effectiveTo', label: 'To', width: '120px', value: row => dateText(row.effectiveTo) }
  ]
  const trail: Column<EmployeeInfotypeRecord>[] = [
    { key: 'changeReason', label: 'Reason', width: '180px' },
    { key: 'createdBy', label: 'By', width: '140px' }
  ]
  const valueColumn = (key: string, label: string, path: string[], width = '150px', map?: (value: unknown) => string): Column<EmployeeInfotypeRecord> => ({ key, label, width, value: row => infotypeValue(row, path, map) })
  const clientName = (value: unknown) => refs.clients.find(client => client.id === Number(value))?.name || String(value || '')
  const locationName = (value: unknown) => refs.locations.find(location => location.id === Number(value))?.name || String(value || '')
  const managerName = (value: unknown) => refs.managerUsers.find(user => user.id === Number(value))?.displayName || String(value || '')
  const templateName = (value: unknown) => refs.templates.find(template => String(template.id) === String(value))?.name || String(value || '')
  const byCode: Record<EmployeeInfotypeCode, Column<EmployeeInfotypeRecord>[]> = {
    '0000': [valueColumn('activeValue', 'Active', ['IsActive', 'isActive']), valueColumn('dojValue', 'Joining date', ['DateOfJoining', 'dateOfJoining'])],
    '0001': [valueColumn('clientValue', 'Client', ['ClientId', 'clientId'], '170px', clientName), valueColumn('deptValue', 'Department', ['Department', 'department']), valueColumn('desigValue', 'Designation', ['Designation', 'designation']), valueColumn('gradeValue', 'Grade', ['Grade', 'grade']), valueColumn('locationValue', 'Location', ['WorkLocationId', 'workLocationId'], '170px', locationName), valueColumn('managerUserValue', 'Manager user', ['ReportingManagerUser', 'reportingManagerUser', 'ReportingManagerUserId', 'reportingManagerUserId'], '190px', managerName), valueColumn('emailValue', 'Work email', ['WorkEmail', 'workEmail'], '190px')],
    '0002': [valueColumn('firstNameValue', 'First name', ['FirstName', 'firstName']), valueColumn('lastNameValue', 'Last name', ['LastName', 'lastName']), valueColumn('genderValue', 'Gender', ['Gender', 'gender']), valueColumn('dobValue', 'DOB', ['PersonalDetails.DateOfBirth', 'personalDetails.dateOfBirth', 'PersonalDetails.dateOfBirth']), valueColumn('panValue', 'PAN', ['PersonalDetails.PanNumber', 'personalDetails.panNumber']), valueColumn('uanValue', 'UAN', ['PersonalDetails.UanNumber', 'personalDetails.uanNumber']), valueColumn('mobileValue', 'Mobile', ['PersonalDetails.Mobile', 'personalDetails.mobile'])],
    '0006': [valueColumn('addressValue', 'Address', ['Address', 'address'], '220px'), valueColumn('correspondenceValue', 'Correspondence', ['CorrespondenceAddress', 'correspondenceAddress'], '220px'), valueColumn('permanentValue', 'Permanent', ['PermanentAddress', 'permanentAddress'], '220px')],
    '0008': [valueColumn('templateValue', 'Template', ['SalaryStructureId', 'salaryStructureId'], '180px', templateName), valueColumn('ctcValue', 'Annual CTC', ['AnnualCtc', 'annualCtc']), valueColumn('componentsValue', 'Components', ['SalaryComponents', 'salaryComponents'], '260px')],
    '0009': [valueColumn('bankValue', 'Bank', ['BankName', 'bankName']), valueColumn('accountValue', 'Account no', ['BankAccountNo', 'bankAccountNo'], '180px'), valueColumn('ifscValue', 'IFSC', ['IfscCode', 'ifscCode']), valueColumn('modeValue', 'Mode', ['PaymentMode', 'paymentMode'])],
    'DOCS': [],
    '360': []
  }
  return [...common, ...byCode[code], ...trail]
}

function infotypeValue(row: EmployeeInfotypeRecord, paths: string[], map?: (value: unknown) => string) {
  const data = safeJsonRecord(row.dataJson) as Record<string, unknown>
  const values = paths.map(path => nestedValue(data, path)).filter(value => value !== undefined && value !== null && value !== '')
  const value = values[0]
  if (value === undefined || value === null || value === '') return ''
  if (map) return map(value)
  if (typeof value === 'boolean') return value ? 'Yes' : 'No'
  if (typeof value === 'number') return value.toLocaleString('en-IN')
  if (typeof value === 'object') return objectSummary(value as Record<string, unknown>)
  return String(value)
}

function objectSummary(value: Record<string, unknown>) {
  const entries = Object.entries(value).filter(([, item]) => item !== undefined && item !== null && item !== '')
  if (!entries.length) return ''
  return entries.slice(0, 4).map(([key, item]) => `${key}: ${typeof item === 'number' ? item.toLocaleString('en-IN') : String(item)}`).join(', ') + (entries.length > 4 ? '...' : '')
}

function nestedValue(source: Record<string, unknown>, path: string) {
  return path.split('.').reduce<unknown>((current, key) => current && typeof current === 'object' ? (current as Record<string, unknown>)[key] : undefined, source)
}

function statusRank(status: string) {
  return status.toLowerCase() === 'active' ? 0 : 1
}

function actionFromEmployee(employee: Employee): EmployeeActionRequest {
  return { employeeId: employee.id, actionType: 'Promotion', effectiveDate: new Date().toISOString().slice(0, 10), reason: '', department: employee.department, designation: employee.designation, grade: employee.grade, workLocationId: employee.workLocationId, salaryStructureId: employee.salaryStructureId, annualCtc: employee.annualCtc, salaryJson: employee.salaryJson || '{}' }
}

function dateText(value?: string | null) {
  return value ? String(value).slice(0, 10) : ''
}

function clientNameFor(clients: Client[], id: number) {
  return clients.find(client => client.id === id)?.name || 'No client'
}

function workLocationName(locations: WorkLocation[], id: number) {
  return locations.find(location => location.id === id)?.name || '-'
}

function employeeImportFieldValue(row: Employee, code: string, locations: WorkLocation[], users: WorkflowApprover[], templates: Structure[]) {
  const personal = row.personalDetails ?? personal0
  const payment = row.paymentDetails ?? payment0
  switch (code) {
    case 'EmployeeCode': return row.employeeCode
    case 'FirstName': return row.firstName
    case 'LastName': return row.lastName
    case 'Gender': return row.gender
    case 'DateOfJoining': return row.dateOfJoining
    case 'DateOfBirth': return personal.dateOfBirth || ''
    case 'WorkEmail': return row.workEmail
    case 'Mobile': return personal.mobile || ''
    case 'Department': return row.department
    case 'Designation': return row.designation
    case 'Grade': return row.grade
    case 'WorkLocation': return locations.find(location => location.id === row.workLocationId)?.name || ''
    case 'ReportingManagerEmail': return users.find(user => user.id === row.reportingManagerUserId)?.email || ''
    case 'PortalAccess': return row.portalAccess ? 'TRUE' : 'FALSE'
    case 'Active': return row.isActive ? 'TRUE' : 'FALSE'
    case 'SalaryTemplate': return templates.find(template => String(template.id) === String(row.salaryStructureId))?.name || ''
    case 'AnnualCtc': return String(row.annualCtc || '')
    case 'Pan': return personal.panNumber || ''
    case 'Aadhaar': return personal.aadhaarNumber || ''
    case 'UanNumber': return personal.uanNumber || ''
    case 'EsicNumber': return personal.esicNumber || ''
    case 'Address': return personal.address || ''
    case 'CorrespondenceAddress': return personal.correspondenceAddress || ''
    case 'PermanentAddress': return personal.permanentAddress || ''
    case 'BankName': return payment.bankName || ''
    case 'BankAccountNo': return payment.bankAccountNo || ''
    case 'Ifsc': return payment.ifscCode || ''
    case 'PaymentMode': return payment.paymentMode || ''
    case 'ChangeReason': return ''
    default: return ''
  }
}

function normalizeEmployeeDetails(row: Employee): Employee {
  const personalJson = safeJsonRecord(row.personalJson)
  const paymentJson = safeJsonRecord(row.paymentJson)
  const salaryComponents = Object.keys(row.salaryComponents || {}).length ? row.salaryComponents : numberRecord(row.salaryJson)
  return {
    ...row,
    grade: row.grade || '',
    reportingManagerUserId: row.reportingManagerUserId ? Number(row.reportingManagerUserId) : null,
    salaryComponents,
    salaryJson: JSON.stringify(salaryComponents),
    personalDetails: {
      ...personal0,
      ...row.personalDetails,
      dateOfBirth: row.personalDetails?.dateOfBirth || personalJson.dateOfBirth || personalJson.dob || '',
      panNumber: row.personalDetails?.panNumber || personalJson.panNumber || personalJson.pan || '',
      aadhaarNumber: row.personalDetails?.aadhaarNumber || personalJson.aadhaarNumber || personalJson.aadhaar || '',
      uanNumber: row.personalDetails?.uanNumber || personalJson.uanNumber || personalJson.uan || '',
      esicNumber: row.personalDetails?.esicNumber || personalJson.esicNumber || personalJson.esic || '',
      mobile: row.personalDetails?.mobile || personalJson.mobile || '',
      address: row.personalDetails?.address || personalJson.address || '',
      correspondenceAddress: row.personalDetails?.correspondenceAddress || personalJson.correspondenceAddress || '',
      permanentAddress: row.personalDetails?.permanentAddress || personalJson.permanentAddress || ''
    },
    paymentDetails: {
      ...payment0,
      ...row.paymentDetails,
      bankName: row.paymentDetails?.bankName || paymentJson.bankName || paymentJson.bank || '',
      bankAccountNo: row.paymentDetails?.bankAccountNo || paymentJson.bankAccountNo || paymentJson.account || paymentJson.accountNumber || '',
      ifscCode: row.paymentDetails?.ifscCode || paymentJson.ifscCode || paymentJson.ifsc || '',
      paymentMode: row.paymentDetails?.paymentMode || paymentJson.paymentMode || paymentJson.mode || ''
    }
  }
}

function toEmployeePayload(row: Employee): Employee {
  const salaryComponents = Object.fromEntries(Object.entries(row.salaryComponents || {}).map(([key, value]) => [key, Number(value) || 0]))
  const personalJson = {
    dob: row.personalDetails.dateOfBirth,
    dateOfBirth: row.personalDetails.dateOfBirth,
    mobile: row.personalDetails.mobile,
    pan: row.personalDetails.panNumber,
    panNumber: row.personalDetails.panNumber,
    aadhaar: row.personalDetails.aadhaarNumber,
    aadhaarNumber: row.personalDetails.aadhaarNumber,
    uan: row.personalDetails.uanNumber,
    uanNumber: row.personalDetails.uanNumber,
    esic: row.personalDetails.esicNumber,
    esicNumber: row.personalDetails.esicNumber,
    address: row.personalDetails.address,
    correspondenceAddress: row.personalDetails.correspondenceAddress,
    permanentAddress: row.personalDetails.permanentAddress,
    source: row.personalDetails.source,
    sourceLocation: row.personalDetails.sourceLocation,
    city: row.personalDetails.city,
    district: row.personalDetails.district,
    state: row.personalDetails.state,
    rawDesignation: row.personalDetails.rawDesignation,
    originalEmployeeCode: row.personalDetails.originalEmployeeCode,
    duplicateResolution: row.personalDetails.duplicateResolution,
    excelRow: row.personalDetails.excelRow,
    esicEmployee: row.personalDetails.esicEmployee,
    ptLwfWorkmenComp: row.personalDetails.ptLwfWorkmenComp,
    tds: row.personalDetails.tds,
    recovery: row.personalDetails.recovery
  }
  const paymentJson = {
    bank: row.paymentDetails.bankName,
    bankName: row.paymentDetails.bankName,
    account: row.paymentDetails.bankAccountNo,
    bankAccountNo: row.paymentDetails.bankAccountNo,
    ifsc: row.paymentDetails.ifscCode,
    ifscCode: row.paymentDetails.ifscCode,
    mode: row.paymentDetails.paymentMode,
    paymentMode: row.paymentDetails.paymentMode
  }
  return { ...row, reportingManagerUserId: row.reportingManagerUserId ? Number(row.reportingManagerUserId) : null, salaryComponents, salaryJson: JSON.stringify(salaryComponents), personalJson: JSON.stringify(personalJson), paymentJson: JSON.stringify(paymentJson) }
}

function salaryRecord(row: Employee) {
  return Object.keys(row.salaryComponents || {}).length ? Object.fromEntries(Object.entries(row.salaryComponents).map(([key, value]) => [key, String(value)])) : safeJsonRecord(row.salaryJson)
}

function numberRecord(json: string) {
  return Object.fromEntries(Object.entries(safeJsonRecord(json)).map(([key, value]) => [key, Number(value) || 0]))
}

function refId(value: string | number | null | undefined) {
  return String(value ?? '').split(':')[0]
}

function templatesForClient(templates: Structure[], clientId: number | string) {
  const active = templates.filter(template => template.active !== false)
  const client = refId(clientId)
  if (!client) return active
  const scoped = active.filter(template => refId(template.clientId) === client)
  return scoped.length ? scoped : active
}
