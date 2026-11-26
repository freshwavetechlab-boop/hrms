import { clientChoice } from '../components/ClientOption'
import EmployeeResignationAction from '../components/EmployeeResignationAction'
import EmployeeLifecycleDialog from '../components/EmployeeLifecycleDialog'
import EmptySalarySetupCleanup from '../components/EmptySalarySetupCleanup'
import TalentAcquisitionEmployeeImport from '../components/TalentAcquisitionEmployeeImport'
import '../components/EmployeeResignation.css'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useEmployeeProfileCompletion } from '../components/EmployeeProfileCompletionControls'
import EmployeeMissingInformation, { createEmployeeDocumentCheck } from '../components/EmployeeMissingInformation'
import { useLocation, useSearchParams } from 'react-router-dom'
import { Alert, Button, Checkbox, Drawer, Segmented, Select, Space, Tabs, Tag } from 'antd'
import type { TableRowSelection } from 'antd/es/table/interface'
import RecruitmentRecordList from '../components/RecruitmentRecordList'
import { PageHeaderPortal } from '../components/layout/AppPageHeader'
import { RecruitmentViewContext, useSessionPreference, type RecruitmentView } from '../hooks/useRecruitmentPreferences'
import { matchesWorkforce, workforceFields, workforceValue } from '../utils/employeeWorkforce'
import './EmployeeWorkspace.css'
import { AimOutlined, ApartmentOutlined, NodeIndexOutlined, ZoomInOutlined, ZoomOutOutlined } from '@ant-design/icons'
import { Chk, F, Sel } from '../components/FormPrimitives'
import BulkUploadPreviewModal, { emptyBulkUploadPreview, type BulkUploadPreviewColumnMeta, type BulkUploadPreviewSheet, type BulkUploadPreviewState } from '../components/BulkUploadPreviewModal'
import BulkUploadProgressModal, { type BulkUploadState, type BulkUploadSummary } from '../components/BulkUploadProgressModal'
import DataTable, { type Column } from '../components/DataTable'
import SearchSelect, { selectOptions } from '../components/SearchSelect'
import PayrollScopeSelect, { type PayrollScopeOption } from '../components/PayrollScopeSelect'
import { employeeClientOptions } from '../utils/employeeClientOptions'
import { useToast } from '../components/ToastProvider'
import { employee0, setup0 } from '../data/payrollDefaults'
import { getEmployeeDirectoryClients, getEmployees } from '../services/payrollService'
import { notifyEmployeeMasterSaved } from '../services/employeeMasterChanges'
import { getDropdowns, getEmployeeImportJob, getEmployeeInfotypes, getEmployeeManagerUsers, getSetup, getWorkLocations, preflightEmployeeImport, processEmployeeAction, saveEmployee as persistEmployee, updateEmployeeCode, startEmployeeImport, type EmployeeImportDecision, type EmployeeImportPreflight } from '../services/settingsService'
import type { Client, Component, Drop, Employee, EmployeeActionRequest, EmployeeInfotypeRecord, EmployeePaymentDetails, EmployeePersonalDetails, Setup, Structure, WorkLocation, WorkflowApprover } from '../types/payroll'
import { calculateSalaryDetails, calculateSalaryJson, calculateSalaryTotals, canOverrideSalaryComponent, hasDailyRateComponents, money } from '../utils/salary'
import SalaryFieldHelp from '../components/SalaryFieldHelp'
import { parseImportPreviewSheets, validateImportPreview, type ImportPreviewData, type ImportPreviewIssue, type ImportPreviewRules, type ImportPreviewSheet } from '../utils/importPreview'
import { normalizeEmployeeImportDate } from '../utils/employeeImportDate'
import { buildXlsxBlob } from '../utils/xlsx'
import { safeJsonRecord } from '../shared/json'
import EmployeeAttachmentPanel from '../components/EmployeeAttachmentPanel'
import EmployeeDynamicFields from '../components/EmployeeDynamicFields'
import EntityAttachmentPanel from '../components/EntityAttachmentPanel'
import SmartBulkUploadMapper from '../components/SmartBulkUploadMapper'
import EmployeeImportReviewModal from '../components/EmployeeImportReviewModal'
import RecruitmentFormBuilder from '../components/RecruitmentFormBuilder'
import { emptyEmployeeFields, getEmployeeFields, employeeImportDefinition, employeeFieldValue as employeeImportFieldValue, exportCompleteEmployees, type EmployeeFieldExchange } from '../utils/employeeFields'
import type { BulkImportOperation, PreparedBulkImport } from '../utils/smartBulkImport'
import { getEmployeeActivity360 } from '../services/recruitmentTalentService'
import type { PersonActivityEvent } from '../types/payroll'
import '../components/Employee360.css'
import '../TemplateDesigner.css'
import './EmployeeWorkspace.css'
import { useAuthSession } from '../components/AuthGate'
import { createEmployeePageLoader } from '../utils/employeePageLoading'
import { assignedEmployeeSalaryTemplate, employeeSalaryTemplates } from '../utils/employeeSalaryTemplate'

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
type EmployeeEditorReferences = { setup: Setup; dropdowns: Drop[]; managers: WorkflowApprover[]; locations: WorkLocation[] }
type EmployeeDirectoryReads = { clients: Client[]; employees: Employee[]; locations: WorkLocation[] }
const personal0 = employee0.personalDetails
const payment0 = employee0.paymentDetails
const defaultSkillCategories = ['Highly skilled', 'Skilled', 'Semi-skilled', 'Unskilled']
const wait = (ms: number) => new Promise(resolve => window.setTimeout(resolve, ms))

export type EmployeePageView = 'master' | 'org'

export default function EmployeePage({ view = 'master' }: { view?: EmployeePageView }) {
  const notify = useToast()
  const session = useAuthSession()
  const scopedClientId = Number(session?.user.clientId || 0)
  const clientScoped = scopedClientId > 0 && !session?.user.permissions.includes('security.manage')
  const dataScope = `${session?.user.id || 0}:${session?.user.clientAccessMode || ''}:${scopedClientId}:${clientScoped}:${session?.user.assignedClientIds?.join(',') || ''}`
  const currentDataScope = useRef(dataScope); currentDataScope.current = dataScope
  const pageReads = useRef<{ scope: string; loader: ReturnType<typeof createEmployeePageLoader<EmployeeDirectoryReads, EmployeeEditorReferences>> } | null>(null)
  const locationRead = useRef<{ scope: string; promise: Promise<WorkLocation[]> } | null>(null)
  const editorGeneration = useRef(0)
  const [clients, setClients] = useState<Client[]>([]), [locations, setLocations] = useState<WorkLocation[]>([]), [drops, setDrops] = useState<Drop[]>([]), [setup, setSetup] = useState<Setup>(setup0), [managerUsers, setManagerUsers] = useState<WorkflowApprover[]>([])
  const [employees, setEmployees] = useState<Employee[]>([]), [employee, setEmployee] = useState(employee0), [employeeInfotype, setEmployeeInfotype] = useState<EmployeeInfotypeCode>('0001')
  const [directoryLoaded, setDirectoryLoaded] = useState(false)
  const [savedEmployee, setSavedEmployee] = useState<Employee | null>(null)
  const [savingEmployee, setSavingEmployee] = useState(false)
  const [saveError, setSaveError] = useState('')
  const saveInFlight = useRef(false)
  const editedCode = !!savedEmployee && savedEmployee.employeeCode !== employee.employeeCode
  const editKind = useMemo(() => {
    if (!savedEmployee || savedEmployee === employee) return 'none'
    if (JSON.stringify({ ...employee, employeeCode: savedEmployee.employeeCode }) !== JSON.stringify(savedEmployee)) return 'details'
    return editedCode ? 'code' : 'none'
  }, [employee, savedEmployee, editedCode])
  const [salaryOverrides, setSalaryOverrides] = useState<Record<string, string>>({})
  const [infotypes, setInfotypes] = useState<EmployeeInfotypeRecord[]>([])
  const [changeReason, setChangeReason] = useState('')
  const [modalOpen, setModalOpen] = useState(false)
  const [deleteTarget, setDeleteTarget] = useState<Employee | null>(null)
  const [resignTarget, setResignTarget] = useState<Employee | null>(null)
  const [documentRevision, setDocumentRevision] = useState(0)
  const [fieldClient, setFieldClient] = useState(0)
  const [fieldExchange, setFieldExchange] = useState<EmployeeFieldExchange>(emptyEmployeeFields)
  const [fieldError, setFieldError] = useState('')
  const [searchParams, setSearchParams] = useSearchParams()
  const route = useLocation()
  const fromDashboard = searchParams.get('source') === 'workforce'
  const linkedEmployeeId = Number(searchParams.get('employeeId')) > 0 ? Number(searchParams.get('employeeId')) : 0
  const fromEmployeeLinks = linkedEmployeeId > 0 && !fromDashboard
  const linkedRelation = searchParams.get('employeeLink') || ''
  const linkedManagerUserIds = new Set((searchParams.get('managerUserIds') || '').split(',').map(Number).filter(id => id > 0))
  const [savedClient, setClientFilter] = useSessionPreference('employees.ui:' + session?.user.id + ':client', 0)
  useEffect(() => { const id = Number(searchParams.get('clientId')); if (!fromDashboard && clients.some(client => client.id === id) && (!clientScoped || id === scopedClientId)) setClientFilter(id) }, [searchParams, clients, clientScoped, scopedClientId, fromDashboard])
  const clientFilter = clientScoped ? scopedClientId : fromDashboard || fromEmployeeLinks ? Number(searchParams.get('clientId') || 0) : savedClient
  const currentClientFilter = useRef(clientFilter); currentClientFilter.current = clientFilter
  useEffect(() => { setDeleteTarget(null); setResignTarget(null) }, [dataScope, clientFilter])
  useEffect(() => {
    let active = true; setFieldExchange(emptyEmployeeFields); setFieldError('')
    if (clientFilter) void getEmployeeFields(clientFilter).then(data => { if (active) setFieldExchange(data) }).catch(error => { if (active) setFieldError(error.message) })
    return () => { active = false }
  }, [clientFilter, documentRevision])
  const bulkDefinition = useMemo(() => employeeImportDefinition(fieldExchange), [fieldExchange])
  const exportEmployees = async (rows: Employee[]) => {
    if (!clientFilter) { notify('Select a client before exporting complete employee data.', 'warning'); return }
    try {
      const refs = await editorReferences()
      if (!refs || currentClientFilter.current !== clientFilter) return
      const exchange = await getEmployeeFields(clientFilter)
      if (currentClientFilter.current !== clientFilter || currentDataScope.current !== dataScope) return
      exportCompleteEmployees(rows, exchange, refs.locations, refs.managers, refs.setup.salaryStructures)
    }
    catch (error) { notify(error instanceof Error ? error.message : 'Employee export failed.', 'error') }
  }
  const [upload, setUpload] = useState<{ open: boolean; state: BulkUploadState; percent: number; summary: BulkUploadSummary }>({ open: false, state: 'uploading', percent: 0, summary: { totalRows: 0 } })
  const [bulkMapperOpen, setBulkMapperOpen] = useState(false)
  const [preview, setPreview] = useState<BulkUploadPreviewState>(emptyBulkUploadPreview)
  const [mappedPreviewColumns, setMappedPreviewColumns] = useState<Record<string, BulkUploadPreviewColumnMeta>>({})
  const [previewConfirm, setPreviewConfirm] = useState<null | ((draft: BulkUploadPreviewState) => Promise<void>)>(null)
  const [previewImporting, setPreviewImporting] = useState(false)
  const [employeePreviewSource, setEmployeePreviewSource] = useState<{ sheets: ImportPreviewSheet[]; clientId: number; fileName: string; operation: BulkImportOperation } | null>(null)
  const [importReview, setImportReview] = useState<{ file: File; clientId: number; operation: BulkImportOperation; review: EmployeeImportPreflight } | null>(null)
  const [importReviewBusy, setImportReviewBusy] = useState(false)
  const chosenStructure = assignedEmployeeSalaryTemplate(setup.salaryStructures, employee.clientId, employee.salaryStructureId)
  const rawEmployeeSalary = salaryRecord(employee)
  const structureLineIds = chosenStructure?.lines.map(line => line.componentId) ?? []
  const dailyRateSalary = hasDailyRateComponents(setup.salaryComponents, chosenStructure)
  const employeeSalary = chosenStructure && (employee.annualCtc || dailyRateSalary) ? safeJsonRecord(calculateSalaryJson(employee.annualCtc, setup.salaryComponents, chosenStructure, salaryOverrides)) : rawEmployeeSalary
  const structureComponents = setup.salaryComponents.filter(component => component.active && (chosenStructure ? structureLineIds.includes(String(component.id)) : Object.hasOwn(rawEmployeeSalary, String(component.id)))).sort((a, b) => structureLineIds.indexOf(String(a.id)) - structureLineIds.indexOf(String(b.id)) || Number(a.priority) - Number(b.priority))
  const masterValues = (type: string) => [...new Set(drops.filter(item => item.type === type && item.isActive && (!item.clientId || item.clientId === employee.clientId)).map(item => item.value))]
  const deps = masterValues('Department'), desigs = masterValues('Designation')
  const employmentTypes = [...new Set([...masterValues('Employment Type'), ...(employee.personalDetails.employmentType ? [employee.personalDetails.employmentType] : [])])]
  const grades = drops.filter(item => item.type === 'Employee Grade' && item.isActive && (!item.clientId || item.clientId === employee.clientId)).map(item => item.value)
  const skillCategories = Array.from(new Set([...defaultSkillCategories, ...drops.filter(item => item.type === 'Employee Category' && item.isActive && (!item.clientId || item.clientId === employee.clientId)).map(item => item.value)]))

  const readers = () => {
    if (currentDataScope.current !== dataScope) return null
    if (pageReads.current?.scope === dataScope) return pageReads.current.loader
    pageReads.current?.loader.dispose()
    const loader = createEmployeePageLoader<EmployeeDirectoryReads, EmployeeEditorReferences>({
      clients: getEmployeeDirectoryClients, employees: getEmployees,
      locations: () => {
        const promise = getWorkLocations(); locationRead.current = { scope: dataScope, promise }; return promise
      },
    }, {
      clients: rows => { setClients(rows); if (clientScoped) setClientFilter(scopedClientId) },
      employees: rows => { setEmployees(rows.map(normalizeEmployeeDetails)); setDirectoryLoaded(true) }, locations: setLocations,
    }, async () => {
      const [dropdowns, rawSetup, managers, editorLocations] = await Promise.all([
        getDropdowns(), getSetup(setup0), getEmployeeManagerUsers(),
        locationRead.current?.scope === dataScope ? locationRead.current.promise : getWorkLocations(),
      ])
      return { dropdowns, managers, locations: editorLocations, setup: { ...setup0, ...rawSetup, salaryComponents: rawSetup.salaryComponents ?? [], salaryStructures: rawSetup.salaryStructures ?? [] } }
    }, refs => { setDrops(refs.dropdowns); setSetup(refs.setup); setManagerUsers(refs.managers); setLocations(refs.locations) })
    pageReads.current = { scope: dataScope, loader }
    return loader
  }
  // Employee mutations do not change client identities or work locations.
  // Refresh only the roster and retain the already available selector options.
  const load = () => readers()?.load(['employees']) ?? Promise.resolve()
  const editorReferences = async () => {
    try { const refs = await readers()?.editor(); return currentDataScope.current === dataScope ? refs : undefined }
    catch (error) { if (currentDataScope.current === dataScope) notify(error instanceof Error ? error.message : 'Unable to load employee settings.', 'error'); return undefined }
  }
  useEffect(() => {
    setClients([]); setEmployees([]); setDirectoryLoaded(false); setLocations([]); setDrops([]); setManagerUsers([]); setSetup(setup0)
    const current = readers(); void current?.load()
    return () => { current?.dispose(); if (pageReads.current?.loader === current) pageReads.current = null }
  }, [dataScope])
  const changeClientFilter = (id: number) => { setClientFilter(id); if (fromDashboard || fromEmployeeLinks) setSearchParams(id ? { clientId: String(id) } : {}) }
  const calcSalary = (ctc: number, salaryStructure = chosenStructure, overrides: Record<string, string | number> = {}, sourceSetup = setup) => calculateSalaryJson(ctc, sourceSetup.salaryComponents, salaryStructure, overrides)
  const withSalary = (row: Employee, salaryJson: string): Employee => ({ ...row, salaryJson, salaryComponents: numberRecord(salaryJson) })
  const inferSalaryOverrides = (row: Employee, salaryStructure?: Structure, sourceSetup = setup) => {
    if (!salaryStructure || (!row.annualCtc && !hasDailyRateComponents(sourceSetup.salaryComponents, salaryStructure))) return {}
    const stored = salaryRecord(row)
    const dailyRates = Object.fromEntries(Object.entries(stored).filter(([id]) => sourceSetup.salaryComponents.some(component => String(component.id) === id && component.calculationType === 'Daily Rate')))
    const baseline = safeJsonRecord(calcSalary(row.annualCtc, salaryStructure, dailyRates, sourceSetup))
    const componentById = new Map(sourceSetup.salaryComponents.map(component => [String(component.id), component]))
    return Object.fromEntries(Object.entries(stored).filter(([componentId, value]) => {
      const component = componentById.get(componentId)
      if (!component || !canOverrideSalaryComponent(component)) return false
      return component.calculationType === 'Daily Rate' || Math.abs(Number(value || 0) - Number(baseline[componentId] || 0)) > 0.009
    }))
  }
  const normalizeEmployeeSalary = (row: Employee, overrides?: Record<string, string | number>, sourceSetup = setup) => {
    row = normalizeEmployeeDetails(row)
    const salaryStructure = assignedEmployeeSalaryTemplate(sourceSetup.salaryStructures, row.clientId, row.salaryStructureId)
    if (!salaryStructure || (!row.annualCtc && !hasDailyRateComponents(sourceSetup.salaryComponents, salaryStructure))) return row
    return withSalary(row, calcSalary(row.annualCtc, salaryStructure, overrides ?? inferSalaryOverrides(row, salaryStructure, sourceSetup), sourceSetup))
  }
  const empLine = (componentId: string, value: string) => {
    if (!chosenStructure) {
      const values = { ...salaryRecord(employee), [componentId]: value }
      setEmployee(withSalary(employee, JSON.stringify(values)))
      return
    }
    const nextOverrides = { ...salaryOverrides, [componentId]: value }
    setSalaryOverrides(nextOverrides)
    setEmployee(withSalary(employee, calcSalary(employee.annualCtc, chosenStructure, nextOverrides)))
  }
  const empMonthly = (component: Component) => Number(employeeSalary[String(component.id)] || 0) * (component.calculationType === 'Daily Rate' ? 30 : 1)
  const applyStructure = (id: string) => {
    const selectedId = id.split(':')[0]
    if (!selectedId || selectedId === '0') { setSalaryOverrides({}); setEmployee({ ...employee, salaryStructureId: '' }); return }
    const selectedStructure = assignedEmployeeSalaryTemplate(setup.salaryStructures, employee.clientId, selectedId)
    if (!selectedStructure) { notify('Choose an active salary template available for this client.', 'warning'); return }
    const templateCtc = Number(selectedStructure.annualCtc)
    const ctc = Number.isFinite(templateCtc) && templateCtc > 0 ? templateCtc : Number(employee.annualCtc || 0)
    setSalaryOverrides({}); setEmployee(withSalary({ ...employee, salaryStructureId: String(selectedStructure.id), annualCtc: ctc }, calcSalary(ctc, selectedStructure)))
  }
  const applyCtc = (ctc: number) => setEmployee(chosenStructure
    ? withSalary({ ...employee, annualCtc: ctc }, calcSalary(ctc, chosenStructure, salaryOverrides))
    : { ...employee, annualCtc: ctc })
  const applyClient = (value: string) => {
    const clientId = Number(value.split(':')[0] || 0)
    if (clientId === Number(employee.clientId)) return
    setSalaryOverrides({}); setEmployee({ ...employee, clientId, salaryStructureId: '' })
  }
  const newEmployee = async () => {
    const token = ++editorGeneration.current
    const refs = await editorReferences()
    if (!refs || token !== editorGeneration.current || currentClientFilter.current !== clientFilter) return
    setSalaryOverrides({})
    setSavedEmployee(null); setSaveError('')
    setEmployee({ ...employee0, clientId: clientFilter || 0, salaryStructureId: '' })
    setEmployeeInfotype('0001'); setChangeReason(''); setModalOpen(true)
  }
  const loadEmployeeHistory = async (id: number) => {
    const token = editorGeneration.current
    const rows = await getEmployeeInfotypes(id)
    if (token === editorGeneration.current && currentDataScope.current === dataScope) setInfotypes(rows)
  }
  const editEmployee = async (row: Employee, infotype: EmployeeInfotypeCode = '0001') => {
    const token = ++editorGeneration.current
    const refs = await editorReferences()
    if (!refs || token !== editorGeneration.current || currentClientFilter.current !== clientFilter) return
    const normalized = normalizeEmployeeDetails(row)
    const salaryStructure = assignedEmployeeSalaryTemplate(refs.setup.salaryStructures, normalized.clientId, normalized.salaryStructureId)
    const overrides = inferSalaryOverrides(normalized, salaryStructure, refs.setup)
    const draft = normalizeEmployeeSalary(normalized, overrides, refs.setup)
    setSalaryOverrides(overrides); setEmployee(draft); setSavedEmployee(draft); setSaveError(''); setEmployeeInfotype(infotype); setChangeReason(''); setModalOpen(true); void loadEmployeeHistory(row.id)
  }
  const closeModal = () => { editorGeneration.current++; setDocumentRevision(value => value + 1); setModalOpen(false); setEmployee(employee0); setSalaryOverrides({}); setEmployeeInfotype('0001'); setChangeReason(''); setInfotypes([]) }
  const saveEmployee = async () => {
    if (saveInFlight.current) return
    setSaveError('')
    if (editedCode && employeeInfotype !== '0001') {
      setSaveError('Open 0001 - Organizational Assignment to save the employee code change.')
      return
    }
    if (editKind !== 'code' && chosenStructure && (employee.annualCtc || dailyRateSalary)) {
      const invalid = calculateSalaryDetails(employee.annualCtc, setup.salaryComponents, chosenStructure, salaryOverrides).find(row => row.error || !Number.isFinite(row.monthly))
      if (invalid) { setSaveError(`Salary calculation needs correction: ${invalid.component.code}: ${invalid.error || 'Invalid amount'}`); return }
    }
    const isNew = !employee.id
    const token = editorGeneration.current
    const code = employee.employeeCode.trim()
    saveInFlight.current = true
    setSavingEmployee(true)
    try {
      const response = editKind === 'code' && savedEmployee
        ? await updateEmployeeCode(employee.id, { clientId: employee.clientId, expectedEmployeeCode: savedEmployee.employeeCode, employeeCode: code, changeReason })
        : await persistEmployee(toEmployeePayload(normalizeEmployeeSalary(editedCode ? { ...employee, employeeCode: code } : employee, salaryOverrides)), employeeInfotype, changeReason)
      if (currentDataScope.current !== dataScope || token !== editorGeneration.current) return
      if (!response.ok) { setSaveError(response.error || 'Unable to save employee.'); return }
      if (!response.data.id || (!isNew && response.data.id !== employee.id) || (editedCode && response.data.employeeCode !== code)) {
        setSaveError('The API did not confirm the change. Reopen this employee to check the saved code before retrying.')
        void load()
        return
      }
      notifyEmployeeMasterSaved(employee.clientId)
      if (isNew) {
        const saved = { ...employee, id: response.data.id, employeeCode: response.data.employeeCode ?? code }
        setEmployee(saved); setSavedEmployee(saved); setChangeReason(''); setEmployeeInfotype('DOCS')
        notify('Employee created. Add the configured documents now.', 'success')
        void loadEmployeeHistory(saved.id)
      } else {
        if (editedCode) setEmployees(rows => rows.map(row => row.id === employee.id && row.clientId === employee.clientId ? { ...row, employeeCode: code } : row))
        closeModal()
        notify(editedCode ? `Employee code updated to ${code}.` : 'Employee updated successfully.', 'success')
      }
      void load()
    } catch (error) {
      if (currentDataScope.current === dataScope && token === editorGeneration.current) setSaveError(error instanceof Error ? error.message : 'Unable to confirm the employee update.')
    } finally {
      saveInFlight.current = false
      setSavingEmployee(false)
    }
  }
  const runEmployeeAction = async (request: EmployeeActionRequest) => {
    const response = await processEmployeeAction(request)
    if (!response.ok || !response.data) { notify(response.error || 'Employee action could not be processed.', 'error'); return }
    const normalizedDetails = normalizeEmployeeDetails(response.data)
    const salaryStructure = assignedEmployeeSalaryTemplate(setup.salaryStructures, normalizedDetails.clientId, normalizedDetails.salaryStructureId)
    const overrides = inferSalaryOverrides(normalizedDetails, salaryStructure)
    const normalized = normalizeEmployeeSalary(normalizedDetails, overrides)
    setSalaryOverrides(overrides); setEmployee(normalized); setSavedEmployee(normalized); setSaveError(''); await loadEmployeeHistory(normalized.id); await load(); notify(`${request.actionType} saved with history.`, 'success')
  }
  const deleteEmployee = (row: Employee) => setDeleteTarget(row)
  const downloadTemplate = async (operation?: BulkImportOperation, selectedFieldCodes?: string[]) => {
    if (!clientFilter) { setUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors: ['Select a client before downloading employee template.'] } }); return }
    const refs = await editorReferences()
    if (!refs || currentClientFilter.current !== clientFilter) return
    operation ??= 'insert'
    if (operation) {
      const exchange = await getEmployeeFields(clientFilter).catch(error => { notify(error.message, 'error'); return null })
      if (!exchange || currentClientFilter.current !== clientFilter || currentDataScope.current !== dataScope) return
      const definition = employeeImportDefinition(exchange)
      selectedFieldCodes ??= definition.fields.map(field => field.code)
      const selectedCodes = selectedFieldCodes
      const selected = definition.fields.filter(field => selectedCodes.includes(field.code) || field.required && !(field.requiredForNew && operation === 'update'))
      const headers = selected.map(field => field.header)
      const rows = operation === 'update'
        ? employees.filter(row => row.clientId === clientFilter).map(row => selected.map(field => exchange.values[row.id]?.[field.code] ?? employeeImportFieldValue(row, field.code, refs.locations, refs.managers, refs.setup.salaryStructures)))
        : []
      const instructions = [
        ['Mode', operation],
        ['Rule', operation === 'update' ? 'Only existing Employee IDs are updated. Blank cells preserve current values.' : operation === 'insert' ? 'Only new Employee Codes are accepted.' : 'Existing Employee Codes update; new codes insert.'],
        ['Selected fields', selected.map(field => field.header).join(', ')]
      ]
      const blob = buildXlsxBlob([{ name: 'Employees', rows: [headers, ...rows] }, { name: 'Instructions', rows: instructions }])
      saveBlob(blob, `employee-${operation}-selected-fields.xlsx`)
      notify(`${operation === 'update' ? 'Update' : 'Insert'} template downloaded with ${selected.length} selected field(s).`, 'info')
      return
    }

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
  const visibleEmployees = useMemo(() => employees.filter(row => (fromEmployeeLinks ? Number(row.id) === linkedEmployeeId : row.isActive || !fromDashboard && !!row.lastWorkingDate) && (!clientFilter || row.clientId === clientFilter) && (!fromDashboard || matchesWorkforce(row, searchParams, locations))), [employees, clientFilter, fromDashboard, fromEmployeeLinks, linkedEmployeeId, searchParams, locations])
  const linkedEmployee = employees.find(row => Number(row.id) === linkedEmployeeId)
  const linkedEmployeeLabel = linkedEmployee ? `${linkedEmployee.firstName} ${linkedEmployee.lastName} / ${linkedEmployee.employeeCode}` : `Employee #${linkedEmployeeId}`
  const linkedOrgEmployees = fromEmployeeLinks ? employees.filter(row => row.isActive && (!clientFilter || row.clientId === clientFilter)
    && (linkedRelation === 'manager-users' ? linkedManagerUserIds.has(Number(row.reportingManagerUserId)) : Number(row.reportingManagerId) === linkedEmployeeId)) : employees.filter(row => row.isActive)
  const clientScopeOptions = useMemo(() => employeeClientOptions(clients, employees, !directoryLoaded), [clients, employees, directoryLoaded])

  return <section className={`employee-master${view === 'master' ? ' employee-record-page' : ''}`}>
    {fieldError && <Alert type="error" showIcon message={fieldError} action={<Button onClick={() => setDocumentRevision(value => value + 1)}>Retry</Button>} />}
    {fromEmployeeLinks && view === 'org' && <div className="employee-dashboard-scope"><Tag color="blue">Linked reporting records: {linkedEmployeeLabel}</Tag><Button size="small" onClick={() => changeClientFilter(clientFilter)}>Show full organization</Button></div>}
    {view === 'master' ? <EmployeeDirectory onEmployeesChanged={load} fieldExchange={fieldExchange} configureFields={setFieldClient} onExcelExport={exportEmployees} documentRevision={documentRevision} clientScoped={clientScoped} clients={clients} clientScopeOptions={clientScopeOptions} locations={locations} employees={visibleEmployees} clientFilter={clientFilter} setClientFilter={changeClientFilter} dashboardKey={fromDashboard || fromEmployeeLinks ? route.key : ''} referenceContext={fromEmployeeLinks} dashboardFilters={fromEmployeeLinks ? linkedEmployeeLabel : fromDashboard ? workforceFields.filter(key => searchParams.has(key)).map(key => searchParams.get(key)!).join(' / ') || 'All active employees' : ''} clearDashboard={() => { setClientFilter(clientFilter); setSearchParams({}) }} onNew={newEmployee} onEdit={editEmployee} onDelete={deleteEmployee} onResign={setResignTarget} onDownloadTemplate={downloadTemplate} onBulkUpload={openEmployeeBulkUpload} /> : <EmployeeOrgStructure clientScoped={clientScoped} clients={clients} locations={locations} employees={linkedOrgEmployees} lookupEmployees={employees} reference={fromEmployeeLinks ? linkedEmployeeLabel : undefined} clientFilter={clientFilter} setClientFilter={changeClientFilter} />}
    {deleteTarget && <EmployeeLifecycleDialog key={deleteTarget.id} employee={deleteTarget} onClose={() => setDeleteTarget(null)} onChanged={load} onResign={row => { setDeleteTarget(null); setResignTarget(row) }} />}
    {resignTarget && <EmployeeResignationAction key={resignTarget.id} clientId={resignTarget.clientId} employees={[{ id: resignTarget.id, employeeCode: resignTarget.employeeCode, name: `${resignTarget.firstName} ${resignTarget.lastName}`.trim() }]} open hideTrigger onOpenChange={open => { if (!open) setResignTarget(null) }} onSaved={async () => { await load(); notify('Resignation saved. Employment and linked ESS access follow the last working date.', 'success') }} />}
    {modalOpen && <div className="employee-modal-backdrop" onClick={() => { if (!saveInFlight.current) closeModal() }}>
      <section className="employee-modal" role="dialog" aria-modal="true" aria-label="Employee details" aria-busy={savingEmployee} inert={savingEmployee} onClick={event => event.stopPropagation()}>
        <EmployeePanel dirty={editKind !== 'none'} saving={savingEmployee} saveError={saveError} clientScoped={clientScoped} employee={employee} setEmployeeCode={code => setEmployee(current => ({ ...current, employeeCode: code }))} setEmployee={row => setEmployee(normalizeEmployeeSalary(row, salaryOverrides))} employeeInfotype={employeeInfotype} setEmployeeInfotype={value => { setEmployeeInfotype(value as EmployeeInfotypeCode); setChangeReason('') }} changeReason={changeReason} setChangeReason={setChangeReason} clients={clients} locations={locations} managerUsers={managerUsers} templates={setup.salaryStructures} salaryComponents={setup.salaryComponents} deps={deps} desigs={desigs} grades={grades} skillCategories={skillCategories} employmentTypes={employmentTypes} applyClient={applyClient} applyStructure={applyStructure} applyCtc={applyCtc} structureComponents={structureComponents} employeeSalary={employeeSalary} salaryOverrides={salaryOverrides} empLine={empLine} empMonthly={empMonthly} saveEmployee={saveEmployee} closeModal={closeModal} infotypes={infotypes} runEmployeeAction={runEmployeeAction} />
      </section>
    </div>}
    <SmartBulkUploadMapper open={bulkMapperOpen} definition={bulkDefinition} clientCode={clients.find(client => client.id === clientFilter)?.code ?? ''} existingEmployeeCodes={employees.filter(row => row.clientId === clientFilter).map(row => row.employeeCode)} onCancel={() => setBulkMapperOpen(false)} onPrepared={reviewMappedEmployeeUpload} onTemplateFile={reviewTemplateEmployeeUpload} onDownloadTemplate={downloadTemplate} />
    <Drawer rootClassName="employee-field-configuration-drawer" title="Configure employee fields" open={fieldClient > 0} width="min(1200px, 98vw)" onClose={() => { setFieldClient(0); setDocumentRevision(value => value + 1) }} destroyOnClose><RecruitmentFormBuilder employeeMode initialClientId={fieldClient} clientScopeManaged onSaved={() => setDocumentRevision(value => value + 1)} /></Drawer>
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

function EmployeeOrgStructure(p: { clientScoped: boolean; clients: Client[]; locations: WorkLocation[]; employees: Employee[]; clientFilter: number; setClientFilter: (id: number) => void; reference?: string; lookupEmployees?: Employee[] }) {
  const [orientation, setOrientation] = useState<'vertical' | 'horizontal'>('vertical')
  const [zoom, setZoom] = useState(1)
  const [selectedEmployee, setSelectedEmployee] = useState<Employee | null>(null)
  const clientRows = p.clients.filter(client => !p.clientFilter || client.id === p.clientFilter)
  const employeeCount = clientRows.reduce((sum, client) => sum + p.employees.filter(employee => employee.clientId === client.id).length, 0)
  const clientSummaries = p.clients.map(client => ({ client, count: p.employees.filter(employee => employee.clientId === client.id).length }))
  const changeZoom = (delta: number) => setZoom(current => Math.min(1.6, Math.max(.55, Number((current + delta).toFixed(2)))))
  const resetZoom = () => setZoom(1)
  return <section className="card employee-org-page">
    <header><i className="blue"><ApartmentOutlined /></i><div><h3>{p.reference ? 'Linked reporting employees' : 'Organization chart'}</h3><p>{p.reference || 'Teams-style reporting hierarchy built from each active employee\'s reporting manager.'}</p></div></header>
    <div className="employee-org-toolbar">
      {!p.clientScoped && <label><span>Client</span><PayrollScopeSelect ariaLabel="Organization chart client" value={p.clientFilter} onChange={value => p.setClientFilter(Number(value))} options={employeeClientOptions(p.clients, p.employees)} /></label>}
      <div><span>Total employees</span><b>{employeeCount}</b></div>
      {!p.reference && <><div className="employee-org-view-toggle"><span>Layout</span><div><button type="button" aria-pressed={orientation === 'vertical'} className={orientation === 'vertical' ? 'active' : ''} onClick={() => setOrientation('vertical')}><ApartmentOutlined /> Vertical</button><button type="button" aria-pressed={orientation === 'horizontal'} className={orientation === 'horizontal' ? 'active' : ''} onClick={() => setOrientation('horizontal')}><NodeIndexOutlined /> Horizontal</button></div></div>
      <div className="employee-org-zoom"><span>Canvas</span><div><button type="button" title="Zoom out" aria-label="Zoom out" onClick={() => changeZoom(-.1)}><ZoomOutOutlined /></button><b>{Math.round(zoom * 100)}%</b><button type="button" title="Zoom in" aria-label="Zoom in" onClick={() => changeZoom(.1)}><ZoomInOutlined /></button><button type="button" title="Fit to default scale" onClick={resetZoom}><AimOutlined /> Fit</button></div></div></>}
    </div>
    <div className="employee-org-client-list">
      {p.reference ? <DataTable rows={p.employees} getRowId={row => row.id} title="Reporting links" emptyText="No active reporting employees remain for this reference." exportFileName="employee-reporting-links" actions={row => <Button size="small" onClick={() => setSelectedEmployee(row)}>View details</Button>} columns={[
        { key: 'employeeCode', label: 'Employee code' },
        { key: 'name', label: 'Employee', value: row => `${row.firstName} ${row.lastName}`.trim() },
        { key: 'department', label: 'Department' }, { key: 'designation', label: 'Designation' },
        { key: 'location', label: 'Work location', value: row => workLocationName(p.locations, row.workLocationId) },
        { key: 'reportingManagerUserId', label: 'Manager login reference' }
      ]} /> : !p.clientFilter ? <div className="employee-org-overview" aria-label="Client organization overview">
        <div className="employee-org-overview-copy"><b>Choose a client hierarchy</b><span>All-client mode stays lightweight. Open one client at a time to explore every reporting root without a multi-thousand-card canvas.</span></div>
        <div className="employee-org-overview-grid">
          {clientSummaries.map(({ client, count }) => <button type="button" key={client.id} data-employee-count={count} aria-label={`Open ${client.name} hierarchy, ${count} employees`} onClick={() => p.setClientFilter(client.id)}>
            <span>{client.code || 'Client'}</span><strong>{client.name}</strong><b>{count} employee{count === 1 ? '' : 's'}</b><small>Open hierarchy</small>
          </button>)}
        </div>
      </div> : clientRows.map(client => <ClientOrgChart key={client.id} client={client} employees={p.employees.filter(employee => employee.clientId === client.id)} locations={p.locations} orientation={orientation} zoom={zoom} onZoom={changeZoom} onSelectEmployee={setSelectedEmployee} />)}
      {!clientRows.length && <p className="empty">No client found for org structure.</p>}
    </div>
    {selectedEmployee && <EmployeeOrgDetails employee={selectedEmployee} clients={p.clients} locations={p.locations} employees={p.lookupEmployees || p.employees} onClose={() => setSelectedEmployee(null)} />}
  </section>
}

function ClientOrgChart(p: { client: Client; employees: Employee[]; locations: WorkLocation[]; orientation: 'vertical' | 'horizontal'; zoom: number; onZoom: (delta: number) => void; onSelectEmployee: (employee: Employee) => void }) {
  const [selectedRootId, setSelectedRootId] = useState(0)
  const active = p.employees.filter(employee => employee.isActive)
  const employeeIds = new Set(active.map(employee => employee.id))
  const childrenByManager = new Map<number, Employee[]>()
  active.forEach(employee => {
    const managerId = employee.reportingManagerId || 0
    if (managerId && employeeIds.has(managerId)) childrenByManager.set(managerId, [...(childrenByManager.get(managerId) ?? []), employee])
  })
  childrenByManager.forEach(rows => rows.sort(employeeSort))
  const roots = active.filter(employee => !employee.reportingManagerId || !employeeIds.has(employee.reportingManagerId)).sort(employeeSort)
  const selectedRoot = roots.find(employee => employee.id === selectedRootId) ?? roots[0]
  return <section className="employee-org-client">
    <div className="employee-org-client-title">
      <div><span>Client</span><h3>{p.client.name}</h3></div>
      <b>{active.length} employee{active.length === 1 ? '' : 's'}</b>
    </div>
    {roots.length > 1 && <div className="employee-org-root-control">
      <label><span>Reporting root</span><SearchSelect value={selectedRoot?.id ?? 0} onChange={value => setSelectedRootId(Number(value))} options={roots.map(employee => ({ value: employee.id, label: `${`${employee.firstName} ${employee.lastName}`.trim() || employee.employeeCode} - ${employee.employeeCode}${employee.designation ? ` / ${employee.designation}` : ''}` }))} /></label>
      <p><b>{roots.length}</b> independent reporting roots found. Search and switch roots instead of loading them into one oversized canvas.</p>
    </div>}
    {roots.length ? <><div className="employee-org-canvas-hint"><ZoomInOutlined /> Hold Ctrl and use the mouse wheel to zoom. Normal wheel movement scrolls the page.</div><div className={`employee-org-canvas${active.length <= 3 ? ' compact' : ''}`} onWheel={event => { if (!event.ctrlKey && !event.metaKey) return; event.preventDefault(); p.onZoom(event.deltaY > 0 ? -.05 : .05) }}>
      <div className={`employee-org-tree ${p.orientation}`} style={{ zoom: p.zoom }}>
        {selectedRoot && <OrgNode key={selectedRoot.id} employee={selectedRoot} childrenByManager={childrenByManager} locations={p.locations} level={0} onSelectEmployee={p.onSelectEmployee} />}
      </div>
    </div></> : <p className="empty">No active employees available for this client.</p>}
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
  const managerName = manager ? `${manager.firstName} ${manager.lastName}`.trim() || manager.employeeCode : p.employee.reportingManagerUserId ? `Login user #${p.employee.reportingManagerUserId}` : 'Not assigned'
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
    normalizeDate: normalizeEmployeeImportDate,
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
  return normalizeEmployeeImportDate(value) ?? value
}

function EmployeeDirectory(p: { onEmployeesChanged: () => Promise<void>; fieldExchange: EmployeeFieldExchange; configureFields: (clientId: number) => void; onExcelExport: (rows: Employee[]) => void; documentRevision: number; clientScoped: boolean; clients: Client[]; clientScopeOptions: PayrollScopeOption[]; locations: WorkLocation[]; employees: Employee[]; clientFilter: number; setClientFilter: (id: number) => void; dashboardKey: string; referenceContext?: boolean; dashboardFilters: string; clearDashboard: () => void; onNew: () => void; onEdit: (employee: Employee, infotype?: EmployeeInfotypeCode) => void; onDelete: (employee: Employee) => void; onResign: (employee: Employee) => void; onDownloadTemplate: () => void; onBulkUpload: () => void }) {
  const session = useAuthSession()
  const completion = useEmployeeProfileCompletion(p.employees, p.clients, p.clientFilter, p.documentRevision, p.configureFields, p.clientScopeOptions)
  const scope = session?.user.id + ':' + p.clientFilter
  const [selectForExit, setSelectForExit] = useState(false)
  const [exitIds, setExitIds] = useState<number[]>([])
  useEffect(() => { setExitIds([]); setSelectForExit(false) }, [p.clientFilter])
  const canSelectExit = (row: Employee) => row.isActive && !row.lastWorkingDate && row.clientId === p.clientFilter
  const selectedExitIds = new Set(exitIds)
  const exitEmployees = p.employees.filter(row => selectedExitIds.has(Number(row.id)) && canSelectExit(row))
  const toggleExitEmployee = (row: Employee, checked: boolean) => {
    if (!canSelectExit(row)) return
    const id = Number(row.id)
    setExitIds(current => checked ? [...new Set([...current, id])] : current.filter(value => value !== id))
  }
  const exitSelection: TableRowSelection<Employee> = {
    selectedRowKeys: exitEmployees.map(row => Number(row.id)),
    preserveSelectedRowKeys: true,
    // A single checkbox changes only that employee, even after selecting all
    // pages. Do not replace the whole selection with a table callback snapshot.
    onSelect: (row, checked) => toggleExitEmployee(row, checked),
    onChange: (keys, _, info) => {
      if (info.type === 'single') return
      const eligibleIds = new Set(p.employees.filter(canSelectExit).map(row => Number(row.id)))
      setExitIds([...new Set(keys.map(Number))].filter(id => eligibleIds.has(id)))
    },
    getCheckboxProps: row => ({ disabled: !canSelectExit(row) }),
  }
  const [view, setView] = useSessionPreference<RecruitmentView>('employees.ui:' + scope + ':view:v2', 'Table')
  const [savedSearch, setSearch] = useSessionPreference('employees.ui:' + scope + ':search', '')
  const [dashboardSearch, setDashboardSearch] = useState('')
  useEffect(() => { if (p.dashboardKey) { setView('Table'); setDashboardSearch('') } }, [p.dashboardKey])
  const checkDocuments = useMemo(() => createEmployeeDocumentCheck(), [p.documentRevision, session?.user.id])
  const clientName = (id: number) => p.clients.find(client => client.id === id)?.name ?? 'Client #' + id
  const locationName = (id: number) => workforceValue({ workLocationId: id } as Employee, 'location', p.locations)
  const canManage = session?.user.permissions.includes('employees.manage')
  const columns: Column<Employee>[] = [
      { key: 'employeeName', label: 'Employee', value: row => `${row.firstName} ${row.lastName}`.trim(), render: row => <strong>{row.firstName} {row.lastName}</strong> },
      { key: 'employeeCode', label: 'Code' },
      { key: 'clientName', label: 'Client', value: row => clientName(row.clientId) },
      { key: 'workLocationName', label: 'Work Location', value: row => locationName(row.workLocationId) },
      { key: 'department', label: 'Department' },
      { key: 'designation', label: 'Designation' },
      { key: 'grade', label: 'Grade' },
      { key: 'skillCategory', label: 'Employee category', value: row => row.personalDetails.skillCategory || 'Not categorized' },
      { key: 'workEmail', label: 'Work email' },
      { key: 'status', label: 'Status', value: row => row.employmentStatus || (row.isActive ? 'Active' : 'Inactive'), render: row => <><Tag color={row.lastWorkingDate ? 'red' : row.isActive ? 'green' : 'default'}>{row.employmentStatus || (row.isActive ? 'Active' : 'Inactive')}</Tag>{row.lastWorkingDate && <small>Last working: {row.lastWorkingDate}</small>}</> }
    ]
  columns.push(...p.fieldExchange.fields.map(field => ({ key: field.code, label: field.field.label, value: (row: Employee) => p.fieldExchange.values[row.id]?.[field.code] || '' })))
  columns.splice(7, 0, { key: 'employmentType', label: 'Employee type', value: row => workforceValue(row, 'employmentType') })
  return <RecruitmentViewContext.Provider value={{ view, scope, namespace: 'employees.ui' }}>
    <PageHeaderPortal slot="employees-client-controls">{!p.clientScoped && <div data-testid="employee-client-filter"><PayrollScopeSelect ariaLabel="Employee Master client" value={p.clientFilter} onChange={value => p.setClientFilter(Number(value))} options={p.clientScopeOptions} /></div>}</PageHeaderPortal>
    <PageHeaderPortal slot="employees-view-controls"><Space wrap><Segmented options={[{ label: 'Card view', value: 'Cards' }, { label: 'Table view', value: 'Table' }]} value={view} onChange={value => setView(value as RecruitmentView)} />{completion.manageButton}</Space></PageHeaderPortal>
    <PageHeaderPortal slot="employees-page-controls">{completion.bulkAction}{canManage && <><Button disabled={!p.clientFilter} onClick={() => { setSelectForExit(value => !value); setExitIds([]) }}>{selectForExit ? 'Finish selection' : 'Select employees'}</Button>{selectForExit && <EmployeeResignationAction clientId={p.clientFilter} employees={exitEmployees.map(row => ({ id: row.id, employeeCode: row.employeeCode, name: (row.firstName + ' ' + row.lastName).trim() }))} onSaved={async () => { setExitIds([]); await p.onEmployeesChanged() }} />}</>}<Button disabled={!p.clientFilter} onClick={p.onDownloadTemplate}>Excel template</Button>{canManage && <><TalentAcquisitionEmployeeImport clientId={p.clientFilter} clientName={clientName(p.clientFilter)} onImported={p.onEmployeesChanged} /><Button data-testid="employee-bulk-upload-open" disabled={!p.clientFilter} onClick={p.onBulkUpload}>Bulk upload</Button><Button type="primary" onClick={p.onNew}>New employee</Button></>}</PageHeaderPortal>
    <section className="recruitment-monitor-page recruitment-experience recruitment-viewport employee-record-workspace">
      {completion.statusNotice}
      {p.dashboardFilters && <div className="employee-dashboard-scope"><Tag color="blue">{p.referenceContext ? 'Employee' : 'Dashboard'}: {p.dashboardFilters}</Tag><Button size="small" onClick={p.clearDashboard}>{p.referenceContext ? 'Show all employees' : 'Clear dashboard filter'}</Button></div>}
      <div className="recruitment-workspace-surface"><div className="recruitment-workspace-content">
        <RecruitmentRecordList rowSelection={selectForExit ? exitSelection : completion.rowSelection} selection={selectForExit ? row => <Checkbox checked={selectedExitIds.has(Number(row.id))} disabled={!canSelectExit(row)} onChange={event => toggleExitEmployee(row, event.target.checked)}>Select</Checkbox> : completion.selection} rowClassName={row => row.lastWorkingDate ? 'employee-exit-record' : ''} selectAllDescription={selectForExit ? "Select eligible employees for a dated resignation" : "Select all matching employees with missing information, email and ESS access"} cardExtra={row => row.lastWorkingDate ? <Space><Tag color="red">{row.employmentStatus}</Tag><span>Last working: {row.lastWorkingDate}</span></Space> : <EmployeeMissingInformation additionalIssues={[...completion.additionalMissing(row), ...p.fieldExchange.fields.filter(field => field.field.isRequired && !p.fieldExchange.values[row.id]?.[field.code]).map(field => ({ infotype: field.infotypeCode, label: field.field.label }))]} employee={row} checkDocuments={checkDocuments} onOpen={infotype => p.onEdit(row, infotype)} />} key={scope + ':' + p.dashboardKey} rows={p.employees} columns={columns} title={row => (row.firstName + ' ' + row.lastName).trim() || row.employeeCode} subtitle={row => row.employeeCode + ' / ' + (row.designation || 'Designation not mapped')} exportFileName="employees" onExcelExport={p.onExcelExport} searchPlaceholder="Search by employee name, code, department or email" searchValue={p.dashboardKey ? dashboardSearch : savedSearch} onSearchChange={p.dashboardKey ? setDashboardSearch : setSearch} hiddenCardColumns={['employeeName', 'employeeCode', 'designation']} cardSummaryColumns={['workLocationName', 'department', 'employmentType', 'skillCategory']}
          filters={['location', 'department', 'designation', 'employmentType', 'skillCategory'].map(key => ({ key, label: ({ location: 'Work location', department: 'Department', designation: 'Designation', employmentType: 'Employee type', skillCategory: 'Employee category' } as Record<string, string>)[key], value: (row: Employee) => workforceValue(row, key as typeof workforceFields[number], p.locations) }))}
          actionsWidth={canManage ? 240 : 110} actions={row => <span className="row-actions">{completion.mailAction(row, view === 'Table')}<Button size="small" onClick={() => p.onEdit(row)}>{canManage ? 'Edit' : 'View'}</Button>{canManage && <>{row.isActive && !row.lastWorkingDate && <Button size="small" onClick={() => p.onResign(row)}>Resign</Button>}<Button size="small" danger onClick={() => p.onDelete(row)}>Delete</Button></>}</span>} />
      </div></div>
    </section>
    {completion.dialogs}
  </RecruitmentViewContext.Provider>
}

function AnnualCtcInput(p: { value: number; onChange: (value: number) => void }) {
  const [text, setText] = useState(String(p.value))
  useEffect(() => { setText(String(p.value)) }, [p.value])
  return <input inputMode="decimal" value={text} onChange={event => {
    const [whole, ...fraction] = event.target.value.replace(/[^\d.]/g, '').split('.')
    const next = whole + (fraction.length ? '.' + fraction.join('') : '')
    setText(next)
    const amount = Number(next)
    p.onChange(Number.isFinite(amount) ? amount : 0)
  }} />
}

function EmployeePanel(p: { dirty: boolean; saving: boolean; saveError: string; clientScoped: boolean; employee: Employee; setEmployeeCode: (code: string) => void; setEmployee: (employee: Employee) => void; employeeInfotype: EmployeeInfotypeCode; setEmployeeInfotype: (code: string) => void; changeReason: string; setChangeReason: (value: string) => void; clients: Client[]; locations: WorkLocation[]; managerUsers: WorkflowApprover[]; templates: Structure[]; salaryComponents: Component[]; deps: string[]; desigs: string[]; grades: string[]; skillCategories: string[]; employmentTypes: string[]; applyClient: (value: string) => void; applyStructure: (value: string) => void; applyCtc: (value: number) => void; structureComponents: Component[]; employeeSalary: Record<string, string>; salaryOverrides: Record<string, string>; empLine: (id: string, value: string) => void; empMonthly: (component: Component) => number; saveEmployee: () => void; closeModal: () => void; infotypes: EmployeeInfotypeRecord[]; runEmployeeAction: (request: EmployeeActionRequest) => Promise<void> }) {
  const salaryTemplates = templatesForClient(p.templates, p.employee.clientId)
  const salaryAssignment = refId(p.employee.salaryStructureId).trim()
  const assignedTemplate = assignedEmployeeSalaryTemplate(p.templates, p.employee.clientId, salaryAssignment)
  const unavailableAssignment = !!salaryAssignment && salaryAssignment !== '0' && !assignedTemplate
  const hasDailyRates = p.structureComponents.some(component => component.calculationType === 'Daily Rate')
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
    <Tabs className="employee-infotype-tabs" activeKey={p.employeeInfotype} onChange={p.setEmployeeInfotype} items={employeeInfotypes.map(item => ({ key: item.code, label: `${item.code} - ${item.name}` }))} />
    <section className="employee-infotype-editor"><h4>{selectedInfotype.code} - {selectedInfotype.name}</h4>
    {p.employeeInfotype === '0000' && <EmployeeActionEditor employee={p.employee} locations={p.locations} deps={p.deps} desigs={p.desigs} grades={p.grades} runEmployeeAction={p.runEmployeeAction} />}
    {p.employeeInfotype === '0001' && <div className="grid">{!p.clientScoped && <F l="Client"><Sel v={String(p.employee.clientId || '')} set={p.applyClient} a={p.clients.map(clientChoice)} /></F>}<F l="Employee code"><input value={p.employee.employeeCode} onChange={event => p.setEmployeeCode(event.target.value)} /></F><F l="Work email"><input value={p.employee.workEmail} onChange={event => p.setEmployee({ ...p.employee, workEmail: event.target.value })} /></F><F l="Department"><Sel v={p.employee.department} set={value => p.setEmployee({ ...p.employee, department: value })} a={p.deps} /></F><F l="Designation"><Sel v={p.employee.designation} set={value => p.setEmployee({ ...p.employee, designation: value })} a={p.desigs} /></F><F l="Employee Grade"><Sel v={p.employee.grade} set={value => p.setEmployee({ ...p.employee, grade: value })} a={p.grades} /></F><F l="Employee type"><Sel v={personal.employmentType || ''} set={value => setPersonal('employmentType', value)} a={p.employmentTypes} /></F><F l="Employee category"><Sel v={personal.skillCategory || ''} set={value => setPersonal('skillCategory', value)} a={p.skillCategories} /></F><F l="Work location"><Sel v={String(p.employee.workLocationId || '')} set={value => p.setEmployee({ ...p.employee, workLocationId: Number(value.split(':')[0] || 0) })} a={p.locations.filter(item => item.isActive && item.clientId === p.employee.clientId).map(item => `${item.id}:${item.name}`)} /></F><F l="Reporting manager"><SearchSelect value={p.employee.reportingManagerUserId ?? ''} onChange={value => p.setEmployee({ ...p.employee, reportingManagerUserId: value ? Number(value) : null })} options={managerOptions} /></F><Chk l="Portal access" v={p.employee.portalAccess} set={value => p.setEmployee({ ...p.employee, portalAccess: value })} /><Chk l="Active" v={p.employee.isActive} set={value => p.setEmployee({ ...p.employee, isActive: value })} /></div>}
    {p.employeeInfotype === '0002' && <div className="grid"><F l="First name"><input value={p.employee.firstName} onChange={event => p.setEmployee({ ...p.employee, firstName: event.target.value })} /></F><F l="Last name"><input value={p.employee.lastName} onChange={event => p.setEmployee({ ...p.employee, lastName: event.target.value })} /></F><F l="Gender"><Sel v={p.employee.gender} set={value => p.setEmployee({ ...p.employee, gender: value })} a={['Male', 'Female', 'Other']} /></F><F l="Date of joining"><input type="date" value={p.employee.dateOfJoining} onChange={event => p.setEmployee({ ...p.employee, dateOfJoining: event.target.value })} /></F><F l="Date of birth"><input type="date" value={personal.dateOfBirth || ''} onChange={event => setPersonal('dateOfBirth', event.target.value)} /></F><F l="PAN"><input value={personal.panNumber || ''} onChange={event => setPersonal('panNumber', event.target.value)} /></F><F l="Aadhaar"><input value={personal.aadhaarNumber || ''} onChange={event => setPersonal('aadhaarNumber', event.target.value)} /></F><F l="UAN Number"><input value={personal.uanNumber || ''} onChange={event => setPersonal('uanNumber', event.target.value)} /></F><F l="Mobile"><input value={personal.mobile || ''} onChange={event => setPersonal('mobile', event.target.value)} /></F></div>}
    {p.employeeInfotype === '0006' && <div className="grid"><F l="Address" w><input value={personal.address || ''} onChange={event => setPersonal('address', event.target.value)} /></F><F l="Correspondence Address" w><input value={personal.correspondenceAddress || ''} onChange={event => setPersonal('correspondenceAddress', event.target.value)} /></F><label className="employee-same-address"><input type="checkbox" checked={!!personal.correspondenceAddress && personal.permanentAddress === personal.correspondenceAddress} onChange={event => copyCorrespondence(event.target.checked)} />Same as correspondence address</label><F l="Permanent Address" w><input value={personal.permanentAddress || ''} onChange={event => setPersonal('permanentAddress', event.target.value)} /></F></div>}
    {p.employeeInfotype === '0008' && <div className="employee-salary-panel">
      {!assignedTemplate && <Alert showIcon type="warning" message={unavailableAssignment ? `Assigned salary template #${salaryAssignment} is unavailable for this client` : 'Salary template not assigned'} description={unavailableAssignment ? 'Existing salary data is retained. Choose a template available for this client, or select No template assigned to clear the link.' : salaryTemplates.length ? 'Existing Basic Pay and Annual CTC are retained. Select a salary template when you are ready to bind it.' : 'Existing Basic Pay and Annual CTC are retained. Create a salary template for this client, then select it here.'} />}
      <div className="employee-salary-controls"><F l="Salary template"><Select className="app-search-select" aria-label="Salary template" aria-invalid={unavailableAssignment} showSearch optionFilterProp="label" value={salaryAssignment && salaryAssignment !== '0' ? salaryAssignment : ''} onChange={p.applyStructure} options={[{ value: '', label: 'No template assigned' }, ...(unavailableAssignment ? [{ value: salaryAssignment, label: `Unavailable assigned template (#${salaryAssignment})`, disabled: true }] : []), ...salaryTemplates.map(item => ({ value: String(item.id), label: item.name }))]} /></F><F l="Annual CTC"><AnnualCtcInput value={p.employee.annualCtc} onChange={p.applyCtc} /></F></div>
      <div className="employee-salary-summary"><article><span>{hasDailyRates ? '30-day gross equivalent' : 'Monthly gross'}</span><b>{money(totals.gross)}</b></article><article><span>{hasDailyRates ? '30-day deduction equivalent' : 'Deductions'}</span><b>{money(totals.deductions)}</b></article><article><span>{hasDailyRates ? '30-day net equivalent' : 'Monthly net'}</span><b>{money(totals.net)}</b></article><article><span>Annual CTC</span><b>{money(p.employee.annualCtc)}</b></article></div>
      <div className="employee-salary-table">
        <div className="employee-salary-row employee-salary-head"><span>Component</span><span>Name</span><span>{hasDailyRates ? '30-day equivalent' : 'Monthly'}</span><span>{hasDailyRates ? 'Annual equivalent' : 'Annual'}</span><span>Employee amount / rate<SalaryFieldHelp label="Employee amount / rate" text="Monthly components store a monthly amount. Daily Rate components store the amount per day, not a monthly salary. Equivalents shown assume 30 days; actual pay uses attendance payable days. Annual CTC is optional for daily rates." example="For daily wages enter 700. Payroll with 22 payable days pays Rs 15,400; do not enter 21,000 as the daily rate." /></span></div>
        {salaryRows.length ? salaryRows.map(({ component, monthly, annual }) => <div className="employee-salary-row" key={component.id}>
          <div className="employee-salary-code"><span className={`salary-badge ${badgeClass(component.category)}`}>{component.category}</span><b title={component.code}>{component.code}</b></div>
          <strong title={component.name}>{component.name}</strong>
          <output>{money(monthly)}</output>
          <output>{money(annual)}</output>
          <div><input inputMode="decimal" value={p.salaryOverrides[String(component.id)] ?? p.employeeSalary[String(component.id)] ?? ''} readOnly={!canOverrideSalaryComponent(component)} title={component.calculationType === 'Daily Rate' ? 'Rate per payable day' : canOverrideSalaryComponent(component) ? 'Employee-specific monthly override' : 'Calculated from salary component master and template'} onChange={event => p.empLine(String(component.id), event.target.value.replace(/[^\d.-]/g, ''))} aria-label={`${component.name} override`} />{component.calculationType === 'Daily Rate' && <small>Rs / payable day</small>}</div>
        </div>) : <p className="employee-salary-empty">Select a client and salary template, then enter Annual CTC to calculate the salary breakup.</p>}
      </div>
    </div>}
    {p.employeeInfotype === '0009' && <div className="grid"><F l="Bank"><input value={payment.bankName || ''} onChange={event => setPayment('bankName', event.target.value)} /></F><F l="Account no"><input value={payment.bankAccountNo || ''} onChange={event => setPayment('bankAccountNo', event.target.value)} /></F><F l="IFSC"><input value={payment.ifscCode || ''} onChange={event => setPayment('ifscCode', event.target.value)} /></F><F l="Payment mode"><Sel v={payment.paymentMode || ''} set={value => setPayment('paymentMode', value)} a={['Bank Transfer', 'Cheque', 'Cash']} /></F></div>}
    {p.employeeInfotype === 'DOCS' && <EmployeeAttachmentPanel employeeId={p.employee.id} clientId={p.employee.clientId} />}
    {p.employeeInfotype === '360' && <EmployeeActivity360 employeeId={p.employee.id} clientId={p.employee.clientId} />}
    {p.employeeInfotype !== '0000' && p.employeeInfotype !== 'DOCS' && p.employeeInfotype !== '360' && <div className="grid"><F l="Change reason" w><input value={p.changeReason} onChange={event => p.setChangeReason(event.target.value)} placeholder="Reason for this infotype change" /></F></div>}
    </section>
    {p.employee.id > 0 && p.employee.clientId > 0 && !['0000', 'DOCS', '360'].includes(p.employeeInfotype) && <EmployeeDynamicFields employeeId={p.employee.id} clientId={p.employee.clientId} infotypeCode={p.employeeInfotype} changeReason={p.changeReason} />}
    {p.employeeInfotype === '0008' && p.employee.id > 0 && <EmptySalarySetupCleanup employeeId={p.employee.id} clientId={p.employee.clientId} employeeCode={p.employee.employeeCode} onApplied={p.closeModal} />}
    <InfotypeHistory employee={p.employee} infotypeCode={p.employeeInfotype} infotypes={p.infotypes} clients={p.clients} locations={p.locations} managerUsers={p.managerUsers} templates={p.templates} />
    {p.saveError && <Alert type="error" showIcon role="alert" message={p.saveError} />}
    <div className="actions">
      <button type="button" className="secondary" onClick={p.closeModal}>{p.employeeInfotype === 'DOCS' || p.employeeInfotype === '360' ? 'Close' : 'Cancel'}</button>
      {p.employeeInfotype === 'DOCS' && !p.employee.id && <button type="button" onClick={() => p.setEmployeeInfotype('0001')}>Enter employee details</button>}
      {p.employeeInfotype !== 'DOCS' && p.employeeInfotype !== '360' && <button id="employee-infotype-save" type="button" className={p.dirty ? 'employee-update' : undefined} disabled={p.saving || p.employeeInfotype === '0000'} onClick={p.saveEmployee}>{p.saving ? (p.employee.id ? 'Updating…' : 'Saving…') : p.dirty ? 'Update' : 'Save infotype'}</button>}
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
  return <section className="employee-action-box"><div className="grid"><F l="Action"><Sel v={action.actionType} set={value => set('actionType', value)} a={['Promotion', 'Demotion', 'Transfer', 'Retire', 'Terminate', 'Resign', 'Rehire']} /></F><F l="Effective date"><input type="date" value={action.effectiveDate} onChange={event => set('effectiveDate', event.target.value)} /></F><F l="Reason"><input value={action.reason} onChange={event => set('reason', event.target.value)} /></F><F l="Department"><Sel v={action.department} set={value => set('department', value)} a={p.deps} /></F><F l="Designation"><Sel v={action.designation} set={value => set('designation', value)} a={p.desigs} /></F><F l="Grade"><Sel v={action.grade} set={value => set('grade', value)} a={p.grades} /></F><F l="Work location"><Sel v={String(action.workLocationId || '')} set={value => set('workLocationId', Number(value.split(':')[0] || 0))} a={p.locations.filter(item => item.isActive && item.clientId === p.employee.clientId).map(item => `${item.id}:${item.name}`)} /></F></div><button type="button" onClick={submit}>Save action</button></section>
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
      employmentType: row.personalDetails?.employmentType || String(personalJson.employmentType || ''),
      skillCategory: row.personalDetails?.skillCategory || String(personalJson.skillCategory || ''),
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
    employmentType: row.personalDetails.employmentType,
    skillCategory: row.personalDetails.skillCategory,
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
  return employeeSalaryTemplates(templates, clientId)
}
