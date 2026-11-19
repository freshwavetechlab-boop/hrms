import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { MoreOutlined, RocketOutlined, WarningOutlined, ReloadOutlined } from '@ant-design/icons'
import { Button, Checkbox, Dropdown, Select } from 'antd'
import { Link } from 'react-router-dom'
import { getAttendanceGroups, getMonthlyAttendance } from '../services/leaveAttendanceService'
import { cancelPayrollAdjustment, createPayRun, createSelectedPayRunDraft, deletePayRun, exportPayRunUrl, getClients, getPayrollEmployeeOptions, getPayRun, getPayRunDiagnostics, getPayrollAdjustments, getPayRuns, runPayRunAction, savePayrollAdjustment } from '../services/payrollService'
import { getSetup, getWorkLocations } from '../services/settingsService'
import type { AttendanceGroup, Client, Component, Employee, EmployeeMonthlyAttendance, PayRun, PayRunDiagnostics, PayrollAdjustment, PayRunSalaryLine, RunEmployee, Setup, WorkLocation } from '../types/payroll'
import { savedPayrollDays } from '../utils/savedPayrollAttendance'
import { setup0 } from '../data/payrollDefaults'
import { canCreateSelectedPayRunDraft, matchesSelectedPayRunDraftResponse, selectedPayRunDraftRequest, selectedDraftApplicableAdjustments, selectedDraftAdjustmentReviewKey, selectedDraftSourceReviewKey, withSelectedDraftSourceReview, type SelectedDraftSourceReviewChoice } from '../utils/selectedPayRunDraft'
import { presentDayText, presentDayTotal } from '../utils/payrollAttendanceDisplay'
import { matchesPayRunReviewScope } from '../utils/payRunReviewScope'
import { money } from '../utils/salary'
import { monthlyAttendanceHasIssue } from '../utils/reviewedExtraPaidUnits'
import PageTabs from './PageTabs'
import DataTable, { type Column } from './DataTable'
import FileDropZone from './FileDropZone'
import SearchSelect from './SearchSelect'
import { componentToAdjustmentType, prepareAdjustmentImports, type AdjustmentImportMode } from '../features/payroll/adjustmentImport'
import { useAuthSession } from './AuthGate'
import SelectedPayRunProcessing from './SelectedPayRunProcessing'
import RegularEmployeeCodeSelection from './RegularEmployeeCodeSelection'
import SelectedDraftAdjustmentSelection from './SelectedDraftAdjustmentSelection'
import DraftPayrollAdjustments from './DraftPayrollAdjustments'
import EmployeePayrollRecalculation from './EmployeePayrollRecalculation'
import DraftEmployerCosts from './DraftEmployerCosts'
import type { EmployeeCodeScope } from '../utils/regularEmployeeCodeSelection'
import { getPayrollFormulaWorkbook } from '../services/payrollCalculationService'
import { buildPayrollFormulaWorkbook, preparePayrollFormulaWorkbookRequest } from '../utils/payrollFormulaWorkbook'
import { downloadXlsx } from '../utils/xlsx'
import { getPayrollLocationAttendance, type LocationAttendanceContext } from '../services/payrollLocationService'
import { payrollLocationOptions } from '../utils/payrollLocationAttendance'
import { useSessionPreference } from '../hooks/useRecruitmentPreferences'
import PayrollLocationPayslips from './PayrollLocationPayslips'
import { PageHeaderPortal } from './layout/AppPageHeader'
import RegularRunJourney from './RegularRunJourney'
import PayrollScopeSelect from './PayrollScopeSelect'
import { buildRegularRunJourney } from '../utils/regularRunJourney'
import { regularPayrollLocations } from '../utils/regularPayrollLocations'
import { payrollClientIndicators } from '../utils/payrollScopeIndicators'
import './PayrollLocationFlow.css'
import './RegularRunWorkspace.css'

const currentPeriod = (() => {
  const date = new Date()
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`
})()
const daysInPeriod = (period: string) => {
  const [year, month] = period.split('-').map(Number)
  return year && month ? new Date(year, month, 0).getDate() : 30
}
type PayrollTab = 'Regular Run' | 'Adjustments' | 'Off-cycle Run'
type PayrollStage = 'Setup' | 'Employees' | 'Review'
type AdjustmentStage = 'Entry' | 'Import'
type AttendancePolicyBatch = { id: string; name: string; groups: AttendanceGroup[]; groupIds: number[]; employeeIds: number[]; employeeCount: number; comboCount: number }
const stageItems: readonly PayrollStage[] = ['Employees', 'Setup', 'Review']
const adjustmentStages: readonly AdjustmentStage[] = ['Entry', 'Import']
const processingStatuses = new Set(['Queued', 'Processing'])
const regularRunCodeFor = (policyBatchId: string, ids: number[]) => {
  const batchId = policyBatchId.trim()
  if (batchId) return `REG-B-${stableRunHash(batchId)}`
  const selected = Array.from(new Set(ids.filter(id => id > 0))).sort((a, b) => a - b)
  if (!selected.length) return 'REGULAR'
  if (selected.length === 1) return `REG-G${selected[0]}`
  return `REG-M-${stableRunHash(selected.join(','))}`
}
const stableRunHash = (value: string) => {
  let hash = 2166136261
  for (const ch of value) {
    hash ^= ch.charCodeAt(0)
    hash = Math.imul(hash, 16777619) >>> 0
  }
  return hash.toString(16).toUpperCase().padStart(8, '0')
}
const policyBatchKey = (group: AttendanceGroup) => group.policyBatchId?.trim() || `group:${group.id}`
const policyBaseName = (group: AttendanceGroup) => {
  const suffix = ` - ${group.workLocationName || 'Location'} - ${group.department} - ${group.designation}`
  return group.name.endsWith(suffix) ? group.name.slice(0, -suffix.length).trim() : group.name
}
const buildPolicyBatches = (groups: AttendanceGroup[]): AttendancePolicyBatch[] => {
  const map = new Map<string, AttendanceGroup[]>()
  groups.forEach(group => {
    const key = policyBatchKey(group)
    map.set(key, [...(map.get(key) ?? []), group])
  })
  return Array.from(map.entries()).map(([id, batchGroups]) => {
    const names = Array.from(new Set(batchGroups.map(policyBaseName).filter(Boolean)))
    const employeeIds = Array.from(new Set(batchGroups.flatMap(group => group.employeeIds ?? [])))
    return {
      id,
      name: names.length === 1 ? names[0] : batchGroups[0]?.name || 'Attendance policy',
      groups: batchGroups,
      groupIds: batchGroups.map(group => group.id),
      employeeIds,
      employeeCount: employeeIds.length,
      comboCount: batchGroups.length
    }
  }).sort((a, b) => a.name.localeCompare(b.name))
}

const adjustment0: PayrollAdjustment = { id: 0, clientId: 0, employeeId: 0, employeeName: '', employeeCode: '', componentId: 0, componentCode: '', componentName: '', adjustmentType: 'Earning', amount: 0, payPeriod: currentPeriod, payRunType: 'Regular', reasonCode: 'Overtime', notes: '', taxable: true, status: 'Approved', payRunId: null }

export default function PayRunsPanel({ mode = 'payrun', initialRunType = 'Regular Run' }: { mode?: 'payrun' | 'adjustments'; initialRunType?: Exclude<PayrollTab, 'Adjustments'> }) {
  const [formulaBusy, setFormulaBusy] = useState(false)
  const formulaScope = useRef('')
  const session = useAuthSession()
  const scopedClientId = Number(session?.user.clientId || 0)
  const clientScoped = scopedClientId > 0 && !session?.user.permissions.includes('security.manage')
  const [clients, setClients] = useState<Client[]>([]), [employees, setEmployees] = useState<Employee[]>([]), [runs, setRuns] = useState<PayRun[]>([]), [attendanceGroups, setAttendanceGroups] = useState<AttendanceGroup[]>([])
  const [workLocations, setWorkLocations] = useState<WorkLocation[]>([])
  const [setup, setSetup] = useState<Setup>(setup0), [selected, setSelected] = useState<PayRun | null>(null)
  const [diagnostics, setDiagnostics] = useState<PayRunDiagnostics | null>(null)
  const [clientId, setClientId] = useSessionPreference(`payroll.context.client:${session?.user.id}:${mode}:${initialRunType}`, scopedClientId)
  const [period, setPeriod] = useSessionPreference(`payroll.context.period:${session?.user.id}:${mode}:${initialRunType}`, currentPeriod)
  const [workingDays] = useState(30)
  const [includedIds, setIncludedIds] = useState<number[]>([]), [attendancePolicyBatchId, setAttendancePolicyBatchId] = useState(''), [offcycleEmployeeIds, setOffcycleEmployeeIds] = useState<number[]>([]), [offcycleAdjustmentIds, setOffcycleAdjustmentIds] = useState<number[]>([])
  const scopeChoice = useRef({ clientId: clientScoped ? scopedClientId : clientId, policyBatchId: '' })
  const loadSequence = useRef(0)
  const [adjustments, setAdjustments] = useState<PayrollAdjustment[]>([]), [adjustment, setAdjustment] = useState<PayrollAdjustment>(adjustment0)
  const [tab] = useState<PayrollTab>(mode === 'adjustments' ? 'Adjustments' : initialRunType), [busy, setBusy] = useState(false), [message, setMessage] = useState('Select client, verify employees, then run payroll.')
  const [stage, setStage] = useState<PayrollStage>('Employees')
  const [regularView, setRegularView] = useSessionPreference<'Run payroll' | 'Employees' | 'Locations' | 'Review & correct' | 'Run history'>(`payroll.regular.view:${session?.user.id}:${clientId}`, 'Run payroll')
  const selectedDraftPending = useRef(false)
  const explicitlyStartedRuns = useRef(new Set<number>())
  const [selectedDraftUncertain, setSelectedDraftUncertain] = useState(false)
  const [selectedAdjustmentReview, setSelectedAdjustmentReview] = useState<{ key: string; ids: number[]; sourceReview?: SelectedDraftSourceReviewChoice }>({ key: '', ids: [] })
  const canCreateSelectedDraft = canCreateSelectedPayRunDraft(session?.user)
  const [adjustmentStage, setAdjustmentStage] = useState<AdjustmentStage>('Entry')
  const [attendanceRows, setAttendanceRows] = useState<EmployeeMonthlyAttendance[]>([]), [offcycleName, setOffcycleName] = useState('Off-cycle payment'), [offcycleReason, setOffcycleReason] = useState('Missed employee / reimbursement payment')

  const clientEmployees = useMemo(() => employees.filter(employee => employee.clientId === clientId && employee.isActive), [clientId, employees])
  const clientAttendanceGroups = useMemo(() => attendanceGroups.filter(group => group.clientId === clientId && group.isActive), [attendanceGroups, clientId])
  const attendancePolicyBatches = useMemo(() => buildPolicyBatches(clientAttendanceGroups), [clientAttendanceGroups])
  const selectedPolicyBatch = useMemo(() => attendancePolicyBatches.find(batch => batch.id === attendancePolicyBatchId) || null, [attendancePolicyBatches, attendancePolicyBatchId])
  const selectedAttendanceGroups = selectedPolicyBatch?.groups ?? []
  const selectedAttendanceGroupIds = useMemo(() => selectedAttendanceGroups.map(group => group.id), [selectedAttendanceGroups])
  const selectedRegularRunCode = useMemo(() => regularRunCodeFor(selectedPolicyBatch?.id ?? '', selectedAttendanceGroupIds), [selectedAttendanceGroupIds, selectedPolicyBatch?.id])
  const selectedLegacyRegularRunCode = useMemo(() => regularRunCodeFor('', selectedAttendanceGroupIds), [selectedAttendanceGroupIds])
  const selectedPolicyLabel = selectedPolicyBatch?.name ?? ''
  const [locationContext, setLocationContext] = useState<LocationAttendanceContext | null>(null)
  const [locationLoading, setLocationLoading] = useState(false)
  const [payrollLocationId, setPayrollLocationId] = useSessionPreference(`payroll.location:${session?.user.id}:${clientId}:${attendancePolicyBatchId}`, 0)
  const locationScope = JSON.stringify([clientId, period, attendancePolicyBatchId, selectedAttendanceGroupIds])
  useEffect(() => {
    let active = true
    setLocationContext(null); setLocationLoading(!!clientId && !!selectedPolicyBatch)
    if (clientId && selectedPolicyBatch) void getPayrollLocationAttendance(clientId, period, selectedPolicyBatch.id, selectedPolicyBatch.groupIds).then(result => {
      if (active) { setLocationLoading(false); if (result.ok) setLocationContext(result.data) }
    })
    return () => { active = false }
  }, [locationScope]) // eslint-disable-line react-hooks/exhaustive-deps
  const locationOptions = useMemo(() => {
    const existing = payrollLocationOptions(locationContext)
    const ids = new Set(existing.map(option => option.value))
    return [...existing, ...workLocations.filter(location => location.clientId === clientId && location.isActive && !ids.has(location.id)).map(location => ({ value: location.id, label: location.name }))]
  }, [locationContext, workLocations, clientId])
  const selectedPayrollLocationId = locationOptions.some(option => option.value === payrollLocationId) ? payrollLocationId : 0
  const locationEmployeeIds = useMemo(() => new Set(locationContext?.rows.filter(row => !selectedPayrollLocationId || row.workLocationId === selectedPayrollLocationId).map(row => row.employeeId)), [locationContext, selectedPayrollLocationId])
  const locationDefaultScope = useRef('')
  const groupEmployeeIds = useMemo(() => new Set(selectedAttendanceGroups.flatMap(group => group.employeeIds ?? [])), [selectedAttendanceGroups])
  const regularEmployees = useMemo(() => selectedAttendanceGroups.length ? clientEmployees.filter(employee => groupEmployeeIds.has(employee.id)
    && (!selectedPayrollLocationId || locationEmployeeIds.has(employee.id))) : [], [clientEmployees, groupEmployeeIds, selectedAttendanceGroups.length, selectedPayrollLocationId, locationEmployeeIds])
  useEffect(() => {
    if (!locationContext) return
    const key = JSON.stringify([locationScope, selectedPayrollLocationId])
    if (locationDefaultScope.current === key) return
    locationDefaultScope.current = key
    const native = locationContext.rows.filter(row => row.nativePart)
    const defaults = native.length ? new Set(native.map(row => row.employeeId)) : null
    setIncludedIds(current => current.filter(id => (!selectedPayrollLocationId || locationEmployeeIds.has(id)) && (!defaults || defaults.has(id))))
  }, [locationContext, selectedPayrollLocationId, locationEmployeeIds, locationScope])
  const variableComponents = useMemo(() => setup.salaryComponents.filter(isAdjustmentComponent), [setup.salaryComponents])
  const pendingAdjustments = adjustments.filter(item => item.status === 'Approved' && item.clientId === clientId && item.payPeriod === period)
  const regularAdjustments = pendingAdjustments.filter(item => item.payRunType !== 'Off Cycle' && (!selectedAttendanceGroups.length || groupEmployeeIds.has(item.employeeId)))
  const offcycleAdjustments = pendingAdjustments.filter(item => item.payRunType === 'Off Cycle')
  const includedCount = includedIds.length
  const estimatedMonthlyCost = regularEmployees.filter(employee => includedIds.includes(employee.id)).reduce((sum, employee) => sum + Number(employee.annualCtc || 0) / 12, 0)
  const offcycleEmployeeCost = clientEmployees.filter(employee => offcycleEmployeeIds.includes(employee.id)).reduce((sum, employee) => sum + Number(employee.annualCtc || 0) / 12, 0)
  const offcycleAdjustmentCost = offcycleAdjustments.filter(item => offcycleAdjustmentIds.includes(item.id)).reduce((sum, item) => sum + (isDeductionAdjustment(item) ? -Number(item.amount || 0) : Number(item.amount || 0)), 0)
  const selectedEstimate = tab === 'Off-cycle Run' ? offcycleEmployeeCost + offcycleAdjustmentCost : estimatedMonthlyCost
  const estimateLabel = tab === 'Off-cycle Run' ? 'Off-cycle estimate' : 'Estimated monthly cost'
  const attendanceByEmployee = useMemo(() => new Map(attendanceRows.map(row => [row.employeeId, row])), [attendanceRows])
  const attendanceWorkingDays = includedIds.map(id => Number(attendanceByEmployee.get(id)?.workingDays || 0)).filter(days => days > 0)
  const fallbackWorkingDays = setup.schedule.salaryDays === 'Fixed days' ? Number(setup.schedule.fixedDays || 30) : daysInPeriod(period) || workingDays || 30
  const derivedWorkingDays = Math.max(1, Math.min(31, Math.ceil(attendanceWorkingDays.length ? Math.max(...attendanceWorkingDays) : fallbackWorkingDays)))
  const varianceEmployees = selected?.employees.filter(employee => !employee.isSkipped && Math.abs(employee.netPayVariance || 0) > 0) ?? []
  const materialVarianceCount = varianceEmployees.filter(employee => Math.abs(employee.variancePercent || 0) >= 10 || Math.abs(employee.netPayVariance || 0) >= 5000).length
  const missingAttendance = includedIds.filter(id => !attendanceByEmployee.has(id)).length
  const locationAttendanceIssues = new Set(locationContext?.rows.filter(row => row.nativePart && includedIds.includes(row.employeeId)
    && (!row.recorded || !!row.issue)).map(row => row.employeeId)).size
  const rosterMismatch = includedIds.some(id => !regularEmployees.some(employee => employee.id === id))
  const attendanceIssues = missingAttendance + attendanceRows.filter(row => includedIds.includes(row.employeeId) && monthlyAttendanceHasIssue(row, clientId)).length + locationAttendanceIssues
  const attendanceReady = !locationLoading && !rosterMismatch && includedCount > 0 && attendanceRows.length > 0 && attendanceIssues === 0
  const reviewRuns = useMemo(() => runs.filter(run => matchesPayRunReviewScope(run, {
    clientId,
    offCycle: tab === 'Off-cycle Run',
    policyBatchId: selectedPolicyBatch?.id,
    regularRunCode: selectedRegularRunCode,
    legacyRegularRunCode: selectedLegacyRegularRunCode,
  })), [clientId, runs, selectedLegacyRegularRunCode, selectedRegularRunCode, selectedPolicyBatch?.id, tab])
  const scopedReviewRuns = useMemo(() => {
    if (tab !== 'Regular Run' || !selectedPayrollLocationId) return reviewRuns
    if (locationLoading || !locationContext) return []
    const ids = new Set(regularPayrollLocations(clientId, period, workLocations, employees, locationContext, reviewRuns).find(row => row.id === selectedPayrollLocationId)?.runIds ?? [])
    return reviewRuns.filter(run => ids.has(run.id))
  }, [tab, selectedPayrollLocationId, reviewRuns, locationLoading, locationContext, clientId, period, workLocations, employees])
  const attendanceReviewLink = useMemo(() => {
    const params = new URLSearchParams()
    if (clientId) params.set('clientId', String(clientId))
    if (period) params.set('period', period)
    if (selectedAttendanceGroupIds.length) params.set('groupIds', selectedAttendanceGroupIds.join(','))
    if (selectedPolicyBatch?.id) params.set('policyBatchId', selectedPolicyBatch.id)
    if (includedIds.length) params.set('employeeIds', includedIds.join(','))
    if (selectedPayrollLocationId) params.set('workLocationId', String(selectedPayrollLocationId))
    const query = params.toString()
    return query ? `/attendance?${query}` : '/attendance'
  }, [clientId, period, selectedAttendanceGroupIds, selectedPolicyBatch?.id, includedIds, selectedPayrollLocationId])

  const employeeIdsForGroups = (groups: AttendanceGroup[], sourceEmployees = employees) => {
    const ids = new Set(groups.flatMap(group => group.employeeIds ?? []))
    return ids.size ? sourceEmployees.filter(employee => employee.isActive && ids.has(employee.id)).map(employee => employee.id) : []
  }

  const load = async () => {
    const sequence = ++loadSequence.current
    const [clientRows, employeeRows, runRows, setupRow, adjustmentRows, groupRows, locationRows] = await Promise.all([getClients(), getPayrollEmployeeOptions(), getPayRuns(), getSetup(setup0), getPayrollAdjustments(), getAttendanceGroups(), tab === 'Regular Run' && mode !== 'adjustments' ? getWorkLocations() : Promise.resolve([])])
    if (sequence !== loadSequence.current) return
    const choice = scopeChoice.current
    const nextClientId = clientScoped ? scopedClientId : clientRows.some(row => row.id === choice.clientId) ? choice.clientId : clientRows[0]?.id || 0
    const activeGroups = groupRows.filter(group => group.isActive)
    const nextBatches = buildPolicyBatches(activeGroups.filter(group => group.clientId === nextClientId))
    const nextBatch = nextBatches.find(batch => batch.id === choice.policyBatchId) || null
    const fallbackBatch = nextBatches[0] || null
    setClients(clientRows)
    if (nextClientId !== clientId) setClientId(nextClientId)
    setEmployees(employeeRows)
    setAttendanceGroups(groupRows)
    setWorkLocations(locationRows)
    if (nextClientId && (!choice.policyBatchId || choice.clientId !== nextClientId)) {
      const selectedBatch = nextBatch || fallbackBatch
      scopeChoice.current = { clientId: nextClientId, policyBatchId: selectedBatch?.id || '' }
      setAttendancePolicyBatchId(selectedBatch?.id || '')
      setIncludedIds(employeeIdsForGroups(selectedBatch?.groups ?? [], employeeRows))
    }
    setRuns(runRows)
    setSetup({ ...setup0, ...setupRow, salaryComponents: setupRow.salaryComponents ?? [] })
    setAdjustments(adjustmentRows)
    setSelectedAdjustmentReview({ key: '', ids: [] })
  }

  const open = async (id: number) => {
    const [payRun, diagnosticRows] = await Promise.all([getPayRun(id), getPayRunDiagnostics(id)])
    if (payRun) setSelected(payRun)
    setDiagnostics(diagnosticRows)
  }

  useEffect(() => { void Promise.resolve().then(load) }, [])
  useEffect(() => {
    let cancelled = false
    setAttendanceRows([])
    if (clientId && period) void getMonthlyAttendance(clientId, period).then(rows => { if (!cancelled) setAttendanceRows(rows) })
    return () => { cancelled = true }
  }, [clientId, period])
  useEffect(() => {
    if (mode === 'adjustments' || (stage !== 'Review' && tab !== 'Regular Run') || !clientId || !period) return
    if (selected?.clientId === clientId && selected.payPeriod === period && scopedReviewRuns.some(run => run.id === selected.id)) return
    const latest = scopedReviewRuns.find(run => run.clientId === clientId && run.payPeriod === period)
    if (latest) void open(latest.id)
  }, [mode, stage, clientId, period, selected?.id, selected?.clientId, selected?.payPeriod, scopedReviewRuns, tab])
  useEffect(() => {
    if (!selected || !processingStatuses.has(selected.status)) return
    let active = true
    const timer = window.setInterval(async () => {
      const payRun = await getPayRun(selected.id)
      if (!payRun || !active) return
      const nextDiagnostics = await getPayRunDiagnostics(payRun.id)
      if (!active) return
      setSelected(payRun)
      setDiagnostics(nextDiagnostics)
      if (!processingStatuses.has(payRun.status)) {
        setMessage(payRun.status === 'Failed' ? 'Payroll failed. Review diagnostics below, fix the issue, then run the draft again.' : 'Payroll draft is ready for review.')
        await load()
      }
    }, 5000)
    return () => { active = false; window.clearInterval(timer) }
  }, [selected?.id, selected?.status])

  const changeClient = (id: number) => {
    setMessage('')
    setSelectedAdjustmentReview({ key: '', ids: [] })
    const nextBatch = buildPolicyBatches(attendanceGroups.filter(group => group.clientId === id && group.isActive))[0] || null
    scopeChoice.current = { clientId: id, policyBatchId: nextBatch?.id || '' }
    setClientId(id)
    setAttendancePolicyBatchId(nextBatch?.id || '')
    setIncludedIds(employeeIdsForGroups(nextBatch?.groups ?? []))
    setOffcycleEmployeeIds([])
    setOffcycleAdjustmentIds([])
    setSelected(null)
    setDiagnostics(null)
  }

  const changeAttendancePolicyBatch = (id: string) => {
    setMessage('')
    setSelectedAdjustmentReview({ key: '', ids: [] })
    const nextBatch = attendancePolicyBatches.find(batch => batch.id === id) || null
    scopeChoice.current = { clientId, policyBatchId: nextBatch?.id || '' }
    setAttendancePolicyBatchId(nextBatch?.id || '')
    setIncludedIds(employeeIdsForGroups(nextBatch?.groups ?? []))
    setSelected(null)
    setDiagnostics(null)
  }

  const changePeriod = (value: string) => {
    setMessage('')
    setSelectedAdjustmentReview({ key: '', ids: [] })
    setPeriod(value)
    setSelected(null)
    setDiagnostics(null)
  }

  const createRegular = async () => {
    if (!selectedAttendanceGroups.length) return setMessage('Select at least one attendance policy before running regular payroll.')
    if (locationLoading || rosterMismatch) return setMessage('Wait for the selected location roster, then review the employee selection.')
    if (!attendanceReady) return setMessage(`Attendance is not ready. Resolve ${attendanceIssues || includedCount} attendance issue${(attendanceIssues || includedCount) === 1 ? '' : 's'} first.`)
    setBusy(true)
    const excludedEmployeeIds = clientEmployees.filter(employee => !includedIds.includes(employee.id)).map(employee => employee.id)
    const response = await createPayRun({ clientId, payPeriod: period, payDate: `${period}-28`, totalWorkingDays: derivedWorkingDays, runType: 'Regular', runName: `Regular payroll - ${selectedPolicyLabel}`, reason: selectedPolicyLabel, attendanceGroupId: selectedAttendanceGroupIds[0] || 0, attendanceGroupIds: selectedAttendanceGroupIds, attendancePolicyBatchId: selectedPolicyBatch?.id || '', attendanceGroupName: selectedPolicyLabel, includedEmployeeIds: includedIds, excludedEmployeeIds, adjustmentIds: regularAdjustments.map(item => item.id) })
    await afterCreate(response, 'Draft payroll prepared with approved one-time adjustments.')
  }

  const adjustmentScope = { clientId, period, policyBatchId: selectedPolicyBatch?.id || '', employeeIds: includedIds, adjustments }
  const adjustmentReviewKey = selectedDraftAdjustmentReviewKey(adjustmentScope)
  const selectedDraftAdjustmentIds = selectedAdjustmentReview.key === adjustmentReviewKey ? selectedAdjustmentReview.ids : []
  const applicableSelectedAdjustments = selectedDraftApplicableAdjustments(adjustmentScope)
  const selectedDraftBase = selectedPayRunDraftRequest({ ...adjustmentScope, workingDays: derivedWorkingDays, policyLabel: selectedPolicyLabel, groupIds: selectedAttendanceGroupIds, eligibleEmployeeIds: regularEmployees.map(employee => employee.id), selectedAdjustmentIds: selectedDraftAdjustmentIds })
  const sourceReviewKey = selectedDraftSourceReviewKey(selectedDraftBase.request, adjustmentReviewKey)
  const reviewedSelectedDraft = withSelectedDraftSourceReview(selectedDraftBase.request, sourceReviewKey, selectedAdjustmentReview.sourceReview)
  const selectedDraft = { request: reviewedSelectedDraft.request, error: selectedDraftBase.error || reviewedSelectedDraft.error }
  const createSelectedDraft = async () => {
    if (selectedDraftPending.current || busy || selectedDraftUncertain || !canCreateSelectedDraft || tab !== 'Regular Run' || mode === 'adjustments') return
    if (!selectedDraft.request) { setMessage(selectedDraft.error); return }
    if (!attendanceReady) { setMessage('Attendance payable inputs are not ready. Review the selected employees before creating this draft.'); return }
    const body = selectedDraft.request
    selectedDraftPending.current = true; setBusy(true)
    try {
      const response = await createSelectedPayRunDraft(body)
      if (!response.ok || !matchesSelectedPayRunDraftResponse(response.data, body)) {
        setSelectedAdjustmentReview({ key: '', ids: [] })
        setSelectedDraftUncertain(true)
        setMessage((response.error || 'Selected draft creation could not be confirmed for this exact scope.') + ' No automatic retry was made. Refresh this page and inspect saved payroll runs before creating again.')
        return
      }
      const run = response.data!
      explicitlyStartedRuns.current.add(run.id)
      setSelected(run); setStage('Review'); setMessage('Run ' + run.id + ' saved as AwaitingProcessing. Review selected draft processing to verify its persisted employee scope, then explicitly process it. No calculation, approval or delivery has run.')
      try { setDiagnostics(await getPayRunDiagnostics(run.id)); await load() }
      catch { setMessage('Run ' + run.id + ' was saved as AwaitingProcessing, but its review context could not be refreshed. Do not create it again; refresh and review this exact saved run before processing.'); setSelectedDraftUncertain(true) }
    } catch {
      setSelectedAdjustmentReview({ key: '', ids: [] })
      setSelectedDraftUncertain(true); setMessage('Selected draft outcome is unconfirmed. No automatic retry was made. Refresh this page and inspect saved payroll runs before creating again.')
    } finally { selectedDraftPending.current = false; setBusy(false) }
  }

  const createOffcycle = async () => {
    setBusy(true)
    const response = await createPayRun({ clientId, payPeriod: period, payDate: `${period}-28`, totalWorkingDays: derivedWorkingDays, runType: 'Off Cycle', runName: offcycleName, reason: offcycleReason, includedEmployeeIds: offcycleEmployeeIds, adjustmentIds: offcycleAdjustmentIds })
    await afterCreate(response, 'Off-cycle draft prepared for selected employees/payments.')
  }

  const afterCreate = async (response: { ok: boolean; data: PayRun | null; error?: string }, success: string) => {
    if (response.ok && response.data) {
      explicitlyStartedRuns.current.add(response.data.id)
      setSelected(response.data)
      setDiagnostics(await getPayRunDiagnostics(response.data.id))
      setStage('Review')
      setMessage(processingStatuses.has(response.data.status) ? 'Payroll request is under process. Review will refresh automatically and show pass/fail here.' : response.data.status === 'Failed' ? 'Payroll validation failed. Open diagnostics below to trace and fix blocking issues.' : success)
      await load()
    } else {
      const existing = runs.find(run => run.clientId === clientId && run.payPeriod === period && (run.runType || 'Regular') === 'Regular' && (selectedPolicyBatch?.id ? (run.attendancePolicyBatchId ? run.attendancePolicyBatchId === selectedPolicyBatch.id : [selectedRegularRunCode, selectedLegacyRegularRunCode].includes(run.runCode)) : run.runCode === selectedRegularRunCode))
      if (existing) { await open(existing.id); setMessage('Existing regular draft opened for this attendance policy and period.') }
      else setMessage(response.error || 'Payroll could not be created. Check client, period, selected employees and adjustments.')
    }
    setBusy(false)
  }

  const saveAdjustment = async () => {
    if (!adjustment.employeeId || !adjustment.amount || !adjustment.componentName.trim()) return setMessage('Select employee, component and amount for adjustment.')
    const component = variableComponents.find(item => item.id === adjustment.componentId)
    const selectedEmployee = employees.find(item => item.id === adjustment.employeeId)
    const response = await savePayrollAdjustment({ ...adjustment, clientId, payPeriod: period, employeeName: selectedEmployee ? `${selectedEmployee.firstName} ${selectedEmployee.lastName}`.trim() : '', employeeCode: selectedEmployee?.employeeCode || '', componentCode: component?.code || adjustment.componentCode, componentName: component?.name || adjustment.componentName, adjustmentType: adjustment.adjustmentType, amount: Number(adjustment.amount) })
    if (response.ok) {
      setAdjustment({ ...adjustment0, clientId, payPeriod: period })
      setMessage('Adjustment saved. It will be picked in regular/off-cycle payroll based on Pay Run Type.')
      await load()
    }
  }

  const importAdjustments = async (file: File, mode: AdjustmentImportMode) => {
    if (!clientId) return setMessage('Select client before importing adjustments.')
    if (file.size > 1024 * 1024) return setMessage('Import file is too large. Keep CSV under 1 MB.')
    const prepared = prepareAdjustmentImports({ text: await file.text(), mode, employees: clientEmployees, components: variableComponents, clientId, period, seed: adjustment0 })
    if (!prepared.rows.length && !prepared.skipped) return setMessage('CSV has no import rows.')
    let imported = 0, skipped = prepared.skipped
    for (const row of prepared.rows) {
      const response = await savePayrollAdjustment(row)
      response.ok ? imported += 1 : skipped += 1
    }
    setMessage(`${mode === 'arrears' ? 'Arrears' : 'Scheduled earning'} import completed. Imported ${imported}, skipped ${skipped}.${prepared.errors[0] ? ` ${prepared.errors[0]}` : ''}`)
    await load()
  }

  const action = async (path: string, success: string) => {
    if (!selected) return
    setBusy(true)
    const response = await runPayRunAction(selected.id, path)
    if (response.ok && response.data) { setSelected(response.data); setDiagnostics(await getPayRunDiagnostics(response.data.id)); setMessage(success); await load() }
    if (!response.ok) setMessage(response.error || 'Payroll action failed.')
    setBusy(false)
  }

  const chooseComponent = (id: number) => {
    const component = variableComponents.find(item => item.id === id)
    if (component) setAdjustment({ ...adjustment, componentId: component.id, componentCode: component.code, componentName: component.name, adjustmentType: componentToAdjustmentType(component), taxable: component.taxable })
  }

  const openReviewRun = async (run: PayRun) => {
    if (run.payPeriod !== period) setPeriod(run.payPeriod)
    await open(run.id)
    if (tab === 'Regular Run') setRegularView('Run payroll')
  }

  const hardDeleteRun = async (run: PayRun) => {
    if (!window.confirm(`Hard delete payroll run "${run.runName || run.runCode}" for ${run.payPeriod}?`)) return
    const response = await deletePayRun(run.id)
    if (!response.ok) return setMessage(response.error || 'Unable to hard delete payroll run.')
    if (selected?.id === run.id) { setSelected(null); setDiagnostics(null) }
    setMessage('Payroll run hard deleted.')
    await load()
  }

  const selectedReviewRun = selected && (scopedReviewRuns.some(run => run.id === selected.id) || selected.status === 'AwaitingProcessing' && selected.clientId === clientId && selected.payPeriod === period && canCreateSelectedDraft) ? selected : null
  const reviewPanel = <>
    <ReviewRunPicker runs={reviewRuns} selectedId={selectedReviewRun?.id} openRun={openReviewRun} deleteRun={hardDeleteRun} />
    {selectedReviewRun && <SelectedPayRunProcessing run={selectedReviewRun}
      permitted={!!session?.user.isActive && session.user.clientId == null && !!session.user.roles.includes('super_admin') && !!session?.user.permissions.includes('payroll.run')}
      onChanged={async () => { await load(); await open(selectedReviewRun.id) }} />}
    {selectedReviewRun ? <PayRunReview selected={selectedReviewRun} diagnostics={diagnostics} busy={busy} action={action} onChanged={session?.user.permissions.includes('payroll.run') ? async () => { await open(selectedReviewRun.id); await load() } : undefined} materialVarianceCount={materialVarianceCount} /> : <section className="card report-empty"><p>Select a draft payroll run to review.</p></section>}
  </>
  const attendancePolicyOptions = attendancePolicyBatches.length
    ? attendancePolicyBatches.map(batch => ({ value: batch.id, label: `${batch.name} (${batch.employeeCount} employees / ${batch.comboCount} combos)` }))
    : [{ value: '', label: 'No policy configured', disabled: true }]
  formulaScope.current = JSON.stringify([clientId, period, attendancePolicyBatchId, selectedAttendanceGroupIds, includedIds, selectedPayrollLocationId, locationContext?.version])
  const formulaSelection=(()=>{try{return {request:preparePayrollFormulaWorkbookRequest(clientId,period,selectedPolicyBatch,includedIds),error:''}}catch(error){return {request:null,error:error instanceof Error?error.message:'Select an attendance policy and its employees.'}}})()
  const downloadFormulaWorkbook = async () => {
    if (busy || formulaBusy) return
    if(!formulaSelection.request)return setMessage(formulaSelection.error)
    const requestedScope = formulaScope.current
    const clientName = clients.find(client => client.id === clientId)?.name.trim() || `Client-${clientId}`
    const filenameClient = clientName.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_').replace(/[. ]+$/g, '').slice(0, 160) || `Client-${clientId}`
    const filenameLocation = selectedPayrollLocationId ? '-' + (locationContext?.rows.find(row => row.workLocationId === selectedPayrollLocationId)?.workLocationName || String(selectedPayrollLocationId)).replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_').slice(0, 80) : ''
    setFormulaBusy(true); setMessage('Preparing Excel formulas from saved employee salary and attendance...')
    try {
      const response = await getPayrollFormulaWorkbook({ ...formulaSelection.request, includeLocationParts: !!locationContext,
        attendanceContextVersion: locationContext?.version || '', workLocationId: selectedPayrollLocationId })
      if (formulaScope.current !== requestedScope) return
      if (!response.ok || !response.data.length) throw Error(response.error || 'Formula workbook could not be prepared.')
      const sheets = buildPayrollFormulaWorkbook(clientName, period, response.data)
      downloadXlsx(`${filenameClient}${filenameLocation}-payroll-formulas-${period}.xlsx`, sheets)
      setMessage(`${response.data.length} salary rows exported with editable attendance and salary formulas. This download does not create a payroll run.`)
    } catch (error) { if (formulaScope.current === requestedScope) setMessage(error instanceof Error ? error.message : 'Formula workbook download failed.') }
    finally { setFormulaBusy(false) }
  }
  const contextPanel = <div className="card payroll-control-panel">
    <header><i className="blue">R</i><div><h3>Run context</h3><p>Client, attendance policy and pay period are selected before draft processing. Working days come from Attendance Review.</p></div></header>
    <div className={`pay-run-form enterprise${tab === 'Off-cycle Run' ? ' offcycle-context' : ''}`}>
      {!clientScoped && <label>Client<SearchSelect disabled={busy} value={clientId} onChange={value => changeClient(Number(value))} options={clients.filter(client => client.isActive).map(client => ({ value: client.id, label: client.name }))} /></label>}
      {tab === 'Regular Run' && mode !== 'adjustments' && <label>Attendance Policy<Select disabled={busy} className="app-search-select payroll-policy-select" popupClassName="app-search-select-dropdown" showSearch optionFilterProp="label" value={attendancePolicyBatchId} onChange={value => changeAttendancePolicyBatch(String(value))} options={attendancePolicyOptions} /></label>}
      {tab === 'Regular Run' && mode !== 'adjustments' && <label>Location<SearchSelect value={selectedPayrollLocationId} disabled={busy || !locationContext} options={locationOptions} onChange={value => {
        const next = Number(value); setPayrollLocationId(next)
        const ids = new Set(locationContext?.rows.filter(row => !next || row.workLocationId === next).map(row => row.employeeId))
        setIncludedIds(clientEmployees.filter(employee => groupEmployeeIds.has(employee.id) && (!next || ids.has(employee.id))).map(employee => employee.id))
      }} /></label>}
      <label>Pay period<input disabled={busy} type="month" value={period} onChange={event => changePeriod(event.target.value)} /></label>
      <label>Working days<input value={derivedWorkingDays} readOnly /></label>
      {selected && <PayrollLocationPayslips key={selected.id} run={selected} />}
      {tab === 'Regular Run' && mode !== 'adjustments' && <Button type="primary" aria-label="Download payroll Excel with formulas" title={formulaSelection.error||'Download the selected policy employees with editable attendance and Excel cell formulas.'} loading={formulaBusy} disabled={busy || !formulaSelection.request} onClick={() => void downloadFormulaWorkbook()}>Excel with formulas</Button>}
      {tab === 'Regular Run' && mode !== 'adjustments' && <Button className="payroll-queue-action" type="primary" icon={<RocketOutlined />} loading={busy} onClick={() => void createRegular()} disabled={!clientId || !selectedAttendanceGroups.length || includedCount === 0}>Queue regular draft</Button>}
      {tab === 'Regular Run' && mode !== 'adjustments' && canCreateSelectedDraft && <Button aria-label="Create selected draft for review" loading={busy} disabled={busy || selectedDraftUncertain || !attendanceReady || !selectedDraft.request} onClick={() => void createSelectedDraft()}>Create selected draft for review</Button>}
      {tab === 'Regular Run' && mode !== 'adjustments' && canCreateSelectedDraft && selectedDraft.error && includedCount > 0 && <p role="status">{selectedDraft.error}</p>}
      {tab === 'Off-cycle Run' && <Button className="payroll-queue-action" type="primary" icon={<RocketOutlined />} loading={busy} onClick={() => void createOffcycle()} disabled={!clientId || (offcycleEmployeeIds.length === 0 && offcycleAdjustmentIds.length === 0)}>Queue off-cycle draft</Button>}
    </div>
    {tab === 'Off-cycle Run' && <div className="offcycle-fields"><label>Run name<input value={offcycleName} onChange={event => setOffcycleName(event.target.value)} /></label><label>Reason<input value={offcycleReason} onChange={event => setOffcycleReason(event.target.value)} /></label></div>}
    {!selectedAttendanceGroups.length && tab === 'Regular Run' && mode !== 'adjustments' && <div className="payroll-context-warning" role="status"><WarningOutlined /><span>Select at least one attendance policy before regular payroll can run.</span></div>}
    {selectedAttendanceGroups.length > 0 && !attendanceReady && tab === 'Regular Run' && mode !== 'adjustments' && <div className="payroll-context-warning" role="status"><WarningOutlined /><span>Attendance review must be clean for the selected policies before regular payroll can run.</span></div>}
    {!!selectedPayrollLocationId && locationContext?.rows.some(row => row.nativePart) && <p className="payroll-location-hint">Payroll includes all recorded location parts of each selected employee once. Choose All locations for the complete monthly draft.</p>}
    <div className="payroll-kpis"><div><span>{tab === 'Off-cycle Run' ? 'Selected employees' : 'Policy employees'}</span><strong>{tab === 'Off-cycle Run' ? `${offcycleEmployeeIds.length}/${clientEmployees.length}` : `${includedCount}/${regularEmployees.length}`}</strong></div><div><span>{tab === 'Off-cycle Run' ? 'Selected adjustments' : 'Approved adjustments'}</span><strong>{tab === 'Off-cycle Run' ? `${offcycleAdjustmentIds.length}/${offcycleAdjustments.length}` : regularAdjustments.length}</strong></div><div><span>{estimateLabel}</span><strong>{money(selectedEstimate)}</strong></div><div><span>Current draft net</span><strong>{money(selected?.netPay)}</strong></div></div>
  </div>

  if (tab === 'Regular Run' && mode !== 'adjustments') {
    const run = selectedReviewRun?.payPeriod === period ? selectedReviewRun : null
    const previouslySaved = !!run && !explicitlyStartedRuns.current.has(run.id)
    const journey = buildRegularRunJourney({ run, diagnostics: run ? diagnostics : null, clientId,
      policySelected: !!selectedPolicyBatch, includedCount, attendanceReady, attendanceIssues, locationLoading,
      adjustmentCount: regularAdjustments.length, workingDays: derivedWorkingDays, policyName: selectedPolicyLabel })
    const periodLabel = /^\d{4}-\d{2}$/.test(period) ? new Date(`${period}-01T12:00:00`).toLocaleDateString('en-IN', { month: 'long', year: 'numeric' }) : period
    const processingPermitted = !!session?.user.isActive && session.user.clientId == null && !!session.user.roles.includes('super_admin') && !!session.user.permissions.includes('payroll.run')
    const canRun = !!session?.user.permissions.includes('payroll.run')
    const clientIndicators = payrollClientIndicators(employees, runs)
    const locationOverview = regularPayrollLocations(clientId, period, workLocations, employees, locationContext, reviewRuns)
    const scopeLocationOptions = [{ value: 0, label: 'All locations', available: locationOverview.some(row => row.runIds.length > 0), count: new Set((locationContext?.rows ?? []).map(row => row.employeeId)).size || regularEmployees.length, hint: `${periodLabel} · all locations` }, ...locationOverview.filter(row => row.id > 0).map(row => ({ value: row.id, label: row.name, count: row.count, available: row.runIds.length > 0, hint: row.runIds.length ? `${row.runIds.length} saved payroll run${row.runIds.length === 1 ? '' : 's'} · ${periodLabel}` : `No saved payroll · ${periodLabel}` }))]
    const openLocation = (id: number) => { setPayrollLocationId(id); setSelected(null); setDiagnostics(null); setMessage(''); const ids = new Set(locationContext?.rows.filter(row => !id || row.workLocationId === id).map(row => row.employeeId)); setIncludedIds(clientEmployees.filter(employee => groupEmployeeIds.has(employee.id) && (!id || ids.has(employee.id))).map(employee => employee.id)); setRegularView(id ? 'Run payroll' : 'Locations') }
    const locationsTable = <DataTable title={`Locations · ${periodLabel}`} rows={locationOverview} getRowId={row => row.id} emptyText={locationLoading ? 'Loading locations for this month…' : 'No locations configured for this client.'} exportFileName={`payroll-locations-${clientId}-${period}`} pageSizeOptions={[10, 25, 50]} columns={[{ key: 'name', label: 'Location', render: row => <span className={row.count ? 'regular-location-available' : ''}>{row.name}</span> }, { key: 'count', label: 'Employees', value: row => row.count }, { key: 'payroll', label: 'This month', value: row => row.runIds.length, render: row => row.runIds.length ? `${row.runIds.length} saved run${row.runIds.length === 1 ? '' : 's'}` : 'Not run' }]} actions={row => <Button size="small" onClick={() => openLocation(row.id)}>Open location</Button>} />
    const tools = [
      { key: 'formulas', label: formulaBusy ? 'Preparing Excel…' : 'Excel with formulas', disabled: busy || formulaBusy || !formulaSelection.request, onClick: () => void downloadFormulaWorkbook() },
      { key: 'refresh', label: 'Refresh run status', disabled: busy || !run, onClick: () => { if (run) void open(run.id) } },
      { key: 'export', label: 'Export payroll register', disabled: !run, onClick: () => { if (run) window.location.assign(exportPayRunUrl(run.id)) } },
    ]
    const primaryAction = run?.status === 'AwaitingProcessing'
      ? <SelectedPayRunProcessing run={run} permitted={processingPermitted} label="Review & calculate payroll" onChanged={async () => { await load(); await open(run.id) }} />
      : run && processingStatuses.has(run.status)
        ? <Button type="primary" loading disabled>Calculating payroll</Button>
        : run?.status === 'Pending Approval'
          ? <Button type="primary" loading={busy} disabled={!canRun} onClick={() => void action('approve', 'Payroll approved.')} data-mail-event="PAYRUN.APPROVED" data-mail-resource-type="PayRun" data-mail-resource-id={run.id} data-mail-client-id={run.clientId} data-mail-action-label="Approve payroll">Approve payroll</Button>
          : run && ['Approved', 'Paid', 'Partially Paid'].includes(run.status)
            ? <Link className="regular-primary-link" to={`/reports/payroll-reports?report=payslip-register&clientId=${run.clientId}`}>Open payslip register</Link>
            : run?.status === 'Draft'
              ? <Button type="primary" onClick={() => setRegularView('Review & correct')}>Review & lock payroll</Button>
              : journey.status === 'blocked'
                ? !run && selectedPolicyBatch && includedCount > 0 ? <Link className="regular-primary-link" to={attendanceReviewLink}>Review attendance</Link> : <Button type="primary" onClick={() => setRegularView(run ? 'Review & correct' : 'Employees')}>Review blockers</Button>
                : <Button type="primary" icon={<RocketOutlined />} loading={busy} disabled={!canRun || !attendanceReady || !clientId} onClick={() => void createRegular()}>Run payroll</Button>
    const roster = <RosterCard key={JSON.stringify([clientId, period, attendancePolicyBatchId])} selectionScope={{ clientId, period, policyBatchId: attendancePolicyBatchId }} disabled={busy} employees={regularEmployees} includedIds={includedIds} setIncludedIds={ids => { if (!selectedDraftPending.current) { setIncludedIds(ids); setSelectedAdjustmentReview({ key: '', ids: [] }) } }} adjustments={regularAdjustments}
      selectedDraftAdjustmentReview={canCreateSelectedDraft ? <SelectedDraftAdjustmentSelection rows={applicableSelectedAdjustments} selectedIds={selectedDraftAdjustmentIds} disabled={busy || selectedDraftUncertain} onSelect={ids => { if (!selectedDraftPending.current) setSelectedAdjustmentReview({ key: adjustmentReviewKey, ids }) }} sourceRequest={selectedDraftBase.request} sourceRequestKey={sourceReviewKey} onSourceReviewChange={choice => { if (!selectedDraftPending.current) setSelectedAdjustmentReview(current => current.key === adjustmentReviewKey ? { ...current, sourceReview: choice } : current) }} /> : undefined} />
    return <section className="regular-run-workspace">
      <PageHeaderPortal slot="regular-payroll-client-controls">{!clientScoped && <label className="regular-header-client"><span>Client</span><PayrollScopeSelect ariaLabel="Client" disabled={busy} value={clientId} onChange={value => changeClient(Number(value))} options={clients.filter(client => client.isActive).map(client => ({ value: client.id, label: client.name, count: clientIndicators.get(client.id)?.count ?? 0, hasPayroll: clientIndicators.get(client.id)?.hasPayroll ?? false }))} /></label>}</PageHeaderPortal>
      <PageHeaderPortal slot="regular-payroll-page-controls"><nav className="regular-view-tabs" aria-label="Regular payroll workspace">{(['Run payroll', 'Employees', 'Locations', 'Review & correct', 'Run history'] as const).map(view => <button type="button" key={view} aria-current={regularView === view ? 'page' : undefined} onClick={() => setRegularView(view)}>{view}</button>)}</nav></PageHeaderPortal>
      <div className="regular-context-bar">
        <label>Pay period<input disabled={busy} type="month" value={period} onChange={event => changePeriod(event.target.value)} /></label>
        <label>Attendance policy<Select disabled={busy} className="app-search-select" showSearch optionFilterProp="label" value={attendancePolicyBatchId} onChange={value => changeAttendancePolicyBatch(String(value))} options={attendancePolicyOptions} /></label>
        <label>Location<PayrollScopeSelect ariaLabel="Location" availabilityKind="monthly-payroll" value={selectedPayrollLocationId} disabled={busy || locationLoading} options={scopeLocationOptions} onChange={value => openLocation(Number(value))} /></label>
        <Dropdown menu={{ items: tools }} trigger={['click']}><Button aria-label="Payroll tools" icon={<MoreOutlined />}>Tools</Button></Dropdown>
      </div>
      <div className="regular-workspace-content">
        {regularView === 'Run payroll' && <RegularRunJourney title={`${periodLabel} payroll`} preparation={!run} subtitle={previouslySaved ? `Previously saved · ${run!.employeeCount} employees · Run #${run!.id}` : journey.subtitle} status={journey.status} statusLabel={previouslySaved ? `Saved payroll · ${run!.status}` : journey.title} steps={journey.steps.map(step => ({ ...step, content: step.id === 'attendance' ? <Link to={attendanceReviewLink}>Open attendance review →</Link> : step.id === 'scope' ? <Button type="link" onClick={() => setRegularView('Employees')}>Review employee selection →</Button> : step.id === 'validation' && run ? <Button type="link" onClick={() => setRegularView('Review & correct')}>View employee calculations & diagnostics →</Button> : undefined }))} action={primaryAction}
          notice={<>{journey.blockers.length > 0 && <div className="regular-blockers" role="alert"><strong>{journey.blockers.length} item{journey.blockers.length === 1 ? '' : 's'} need attention</strong>{journey.blockers.slice(0, 3).map((item, index) => <p key={index}>{item.employeeCode && <b>{item.employeeCode} · </b>}{item.message}</p>)}{journey.blockers.length > 3 && <Button type="link" onClick={() => setRegularView('Review & correct')}>View all blockers</Button>}</div>}{message !== 'Select client, verify employees, then run payroll.' && <p className="regular-last-message" role="status">{message}</p>}</>}
          metrics={[{ label: 'Employees', value: String(run ? run.employeeCount : includedCount), hint: run ? `Saved run #${run.id}` : 'Selected for this cycle' }, { label: 'Gross pay', value: run && run.employees.length ? money(run.employees.filter(row => !row.isSkipped).reduce((sum, row) => sum + row.grossPay, 0)) : 'Not calculated' }, { label: 'Net payable', value: run && run.employees.length ? money(run.netPay) : 'Not calculated' }]}>
          <div className="regular-cycle-facts"><h3>Cycle details</h3><dl><div><dt>Salary divisor</dt><dd>{run ? run.totalWorkingDays : derivedWorkingDays} days <small>Automatic</small></dd></div><div><dt>{run ? 'Saved run' : 'Attendance policy'}</dt><dd>{run ? run.runName || run.runCode : selectedPolicyLabel || 'Not selected'}</dd></div><div><dt>{run ? 'Employee scope' : 'Location'}</dt><dd>{run ? `${run.employeeCount} saved employees` : locationOptions.find(option => option.value === selectedPayrollLocationId)?.label || 'All locations'}</dd></div>{run && <div><dt>Run status</dt><dd>{run.status}</dd></div>}</dl><Button type="link" onClick={() => setRegularView('Run history')}>View saved runs →</Button></div>
          {!selectedPayrollLocationId && <div className="regular-location-summary"><h3>Locations this month</h3>{locationOverview.slice(0, 5).map(row => <button type="button" key={row.id} onClick={() => openLocation(row.id)}><span>{row.name}<small>{row.count} employees · {row.runIds.length ? `${row.runIds.length} saved runs` : 'Not run'}</small></span><span aria-hidden="true">→</span></button>)}<Button type="link" onClick={() => setRegularView('Locations')}>View location table →</Button></div>}
        </RegularRunJourney>}
        {regularView === 'Employees' && <>{roster}{canCreateSelectedDraft && <details className="regular-advanced"><summary>Advanced · create a draft for an explicit employee selection</summary><p>Review the selected employees and source adjustments before saving.</p><Button loading={busy} disabled={busy || selectedDraftUncertain || !attendanceReady || !selectedDraft.request} onClick={() => { void createSelectedDraft().then(() => setRegularView('Run payroll')) }}>Create selected draft for review</Button>{selectedDraft.error && <p role="status">{selectedDraft.error}</p>}</details>}</>}
        {regularView === 'Run history' && <ReviewRunPicker runs={scopedReviewRuns.filter(value => value.payPeriod === period)} selectedId={run?.id} openRun={openReviewRun} deleteRun={hardDeleteRun} />}
        {regularView === 'Locations' && locationsTable}
        {regularView === 'Review & correct' && <>{run ? <><div className="regular-review-toolbar"><span>Run #{run.id} · {run.status} · {run.employeeCount} employees</span>{run.status === 'Failed' && <Button disabled={busy || !canRun || !attendanceReady} onClick={() => void createRegular()}>Retry calculation</Button>}<Button icon={<ReloadOutlined />} disabled={busy} onClick={() => void open(run.id)}>Refresh</Button><PayrollLocationPayslips key={run.id} run={run} /></div>{['Approved', 'Pending Approval'].includes(run.status) && <p className="regular-correction-help">To correct an employee, recall this unpaid run to Draft, then use that employee’s correction action. Approval is required again after changes.</p>}<PayRunReview selected={run} diagnostics={diagnostics?.payRunId === run.id ? diagnostics : null} busy={busy} action={action} onChanged={canRun ? async () => { await open(run.id); await load() } : undefined} materialVarianceCount={materialVarianceCount} /></> : <div className="regular-empty"><h3>No calculation to review yet</h3><p>Select the period and policy, then run payroll or open a saved run.</p><Button onClick={() => setRegularView('Run history')}>View saved runs</Button></div>}</>}
      </div>
    </section>
  }

  return <section className={`pay-runs payroll-cockpit${tab === 'Regular Run' && mode !== 'adjustments' ? ' payroll-location-flow' : ''}`}>
    <div className="payroll-status-strip"><span>{message}</span>{attendanceReady ? <span className="status-chip paid">Attendance ready</span> : <Link className="status-chip pending-approval attendance-review-link" to={attendanceReviewLink} state={{ clientId, period, policyBatchId: selectedPolicyBatch?.id, groupIds: selectedAttendanceGroupIds, employeeIds: includedIds }} title="Open Attendance Review">{`${attendanceIssues || includedCount} attendance issue${(attendanceIssues || includedCount) === 1 ? '' : 's'}`}</Link>}</div>
    {contextPanel}
    {mode !== 'adjustments' && <PageTabs items={stageItems} value={stage} onChange={setStage} label="Payroll flow" className="payrun-flow-tabs" />}
    {mode === 'adjustments' && <PageTabs items={adjustmentStages} value={adjustmentStage} onChange={setAdjustmentStage} label="Adjustment flow" className="payrun-flow-tabs" />}
    <section className="payroll-command-grid">
      {tab === 'Regular Run' && stage === 'Employees' && <RosterCard key={JSON.stringify([clientId, period, attendancePolicyBatchId])} selectionScope={{ clientId, period, policyBatchId: attendancePolicyBatchId }} disabled={busy} employees={regularEmployees} includedIds={includedIds} setIncludedIds={ids => { if (!selectedDraftPending.current) { setIncludedIds(ids); setSelectedAdjustmentReview({ key: '', ids: [] }) } }} adjustments={regularAdjustments}
        selectedDraftAdjustmentReview={canCreateSelectedDraft ? <SelectedDraftAdjustmentSelection rows={applicableSelectedAdjustments} selectedIds={selectedDraftAdjustmentIds} disabled={busy || selectedDraftUncertain} onSelect={ids => { if (!selectedDraftPending.current) setSelectedAdjustmentReview({ key: adjustmentReviewKey, ids }) }} sourceRequest={selectedDraftBase.request} sourceRequestKey={sourceReviewKey} onSourceReviewChange={choice => { if (!selectedDraftPending.current) setSelectedAdjustmentReview(current => current.key === adjustmentReviewKey ? { ...current, sourceReview: choice } : current) }} /> : undefined} />}
      {tab === 'Adjustments' && adjustmentStage === 'Entry' && <AdjustmentCard adjustment={adjustment} setAdjustment={setAdjustment} employees={clientEmployees} components={variableComponents} adjustments={adjustments.filter(item => item.clientId === clientId && item.payPeriod === period)} chooseComponent={chooseComponent} saveAdjustment={saveAdjustment} edit={row => setAdjustment({ ...row, amount: Number(row.amount) })} cancel={async id => { await cancelPayrollAdjustment(id); await load() }} />}
      {tab === 'Adjustments' && adjustmentStage === 'Import' && <AdjustmentImportPanel importScheduled={file => void importAdjustments(file, 'scheduled')} importArrears={file => void importAdjustments(file, 'arrears')} />}
      {tab === 'Off-cycle Run' && stage === 'Employees' && <OffcycleCard employees={clientEmployees} selectedIds={offcycleEmployeeIds} setSelectedIds={setOffcycleEmployeeIds} adjustments={offcycleAdjustments} selectedAdjustmentIds={offcycleAdjustmentIds} setSelectedAdjustmentIds={setOffcycleAdjustmentIds} name={offcycleName} setName={setOffcycleName} reason={offcycleReason} setReason={setOffcycleReason} />}
      {mode !== 'adjustments' && stage === 'Review' && reviewPanel}
    </section>
  </section>
}

function RosterCard(p: { selectionScope: EmployeeCodeScope; disabled: boolean; employees: Employee[]; includedIds: number[]; setIncludedIds: (fn: number[] | ((ids: number[]) => number[])) => void; adjustments: PayrollAdjustment[]; selectedDraftAdjustmentReview?: ReactNode }) {
  const [codesOpen, setCodesOpen] = useState(false)
  const selected = new Set(p.includedIds)
  const columns = employeeSelectionColumns(p.includedIds, p.setIncludedIds, 'Include')
  return <div className="card payroll-roster employee-selection-table"><header><i className="blue">E</i><div><h3>Employee roster</h3><p>{p.selectedDraftAdjustmentReview ? 'Choose employees, then explicitly review their existing approved period adjustments below.' : 'Deselect employees before payroll is calculated. Approved regular adjustments are auto-included.'}</p></div></header><div className="roster-actions"><button type="button" onClick={() => p.setIncludedIds(p.employees.map(employee => employee.id))}>Select all</button><button type="button" onClick={() => p.setIncludedIds([])}>Clear</button><button type="button" disabled={p.disabled} aria-expanded={codesOpen} onClick={() => setCodesOpen(value => !value)}>Select employee codes</button><span>{selected.size} selected</span></div>{codesOpen && <RegularEmployeeCodeSelection scope={p.selectionScope} employees={p.employees} disabled={p.disabled} onSelect={ids => p.setIncludedIds(ids)} onClose={() => setCodesOpen(false)} />}<DataTable rows={p.employees} title="Employees" getRowId={row => row.id} emptyText="No employees found for this client." exportFileName="regular-payroll-employees" columns={columns} pageSizeOptions={[25, 50, 100, 250]} hideInactiveClear />{p.selectedDraftAdjustmentReview || <AdjustmentMini rows={p.adjustments} title="Approved regular adjustments" />}</div>
}

function AdjustmentCard(p: { adjustment: PayrollAdjustment; setAdjustment: (row: PayrollAdjustment) => void; employees: Employee[]; components: Component[]; adjustments: PayrollAdjustment[]; chooseComponent: (id: number) => void; saveAdjustment: () => void; edit: (row: PayrollAdjustment) => void; cancel: (id: number) => Promise<void> }) {
  return <div className="card payroll-adjustments"><header><i className="blue">A</i><div><h3>{p.adjustment.id ? 'Edit adjustment entry' : 'Variable earning / adjustment entry'}</h3><p>Use approved one-time entries for overtime, arrears, bonus, reimbursements and recoveries.</p></div></header><div className="adjustment-form"><label>Employee<SearchSelect value={p.adjustment.employeeId} onChange={value => p.setAdjustment({ ...p.adjustment, employeeId: Number(value) })} options={[{ value: 0, label: 'Select employee' }, ...p.employees.map(employee => ({ value: employee.id, label: `${employee.firstName} ${employee.lastName} / ${employee.employeeCode}` }))]} /></label><label>Component<SearchSelect value={p.adjustment.componentId} onChange={value => p.chooseComponent(Number(value))} options={[{ value: 0, label: 'Select variable component' }, ...p.components.map(component => ({ value: component.id, label: `${component.name} / ${component.category}` }))]} /></label><label>Pay Run Type<SearchSelect value={p.adjustment.payRunType} onChange={value => p.setAdjustment({ ...p.adjustment, payRunType: value })} options={['Regular', 'Off Cycle'].map(value => ({ value, label: value }))} /></label><label>Adjustment Type<SearchSelect value={p.adjustment.adjustmentType} onChange={value => p.setAdjustment({ ...p.adjustment, adjustmentType: value })} options={['Earning', 'Reimbursement', 'Deduction'].map(value => ({ value, label: value }))} /></label><label>Amount<input type="number" min="0" value={p.adjustment.amount || ''} onChange={event => p.setAdjustment({ ...p.adjustment, amount: Number(event.target.value) })} /></label><label>Reason<SearchSelect value={p.adjustment.reasonCode} onChange={value => p.setAdjustment({ ...p.adjustment, reasonCode: value })} options={['Overtime', 'Arrears', 'Bonus/Incentive', 'Missed Salary', 'Reimbursement', 'Recovery/Correction'].map(value => ({ value, label: value }))} /></label><label className="wide">Notes<input value={p.adjustment.notes} onChange={event => p.setAdjustment({ ...p.adjustment, notes: event.target.value })} placeholder="Business justification, approval reference or reimbursement details" /></label><button type="button" onClick={p.saveAdjustment}>{p.adjustment.id ? 'Update adjustment' : 'Save approved adjustment'}</button></div><AdjustmentMini rows={p.adjustments} title="Adjustment register" edit={p.edit} cancel={p.cancel} /></div>
}

function AdjustmentImportPanel(p: { importScheduled: (file: File) => void; importArrears: (file: File) => void }) {
  return <div className="card adjustment-import-panel"><header><i className="blue">I</i><div><h3>Bulk adjustment imports</h3><p>Import rows become approved adjustment entries and are picked by regular or off-cycle payroll based on Pay Run Type.</p></div></header><div className="adjustment-import-grid"><article><h3>Scheduled earnings import</h3><p>Use for planned bonuses, incentives, overtime or one-time reimbursements.</p><small>CSV columns: employeeCode, componentCode, amount, payPeriod, payRunType, reason, notes</small><FileDropZone accept=".csv,text/csv" title="Drop scheduled CSV here" hint="Browse or drag CSV for scheduled earnings." onFile={p.importScheduled} /></article><article><h3>Automatic arrears from revision</h3><p>Use salary-revision differences to generate arrears without manual amount calculation.</p><small>CSV columns: employeeCode, componentCode, oldMonthly, newMonthly, months, payPeriod, payRunType, notes</small><FileDropZone accept=".csv,text/csv" title="Drop arrears CSV here" hint="Browse or drag CSV for arrears import." onFile={p.importArrears} /></article></div></div>
}

function OffcycleCard(p: { employees: Employee[]; selectedIds: number[]; setSelectedIds: (fn: number[] | ((ids: number[]) => number[])) => void; adjustments: PayrollAdjustment[]; selectedAdjustmentIds: number[]; setSelectedAdjustmentIds: (fn: number[] | ((ids: number[]) => number[])) => void; name: string; setName: (value: string) => void; reason: string; setReason: (value: string) => void }) {
  const selected = new Set(p.selectedIds)
  const columns = employeeSelectionColumns(p.selectedIds, p.setSelectedIds, 'Pay')
  return <div className="card payroll-roster employee-selection-table"><header><i className="blue">E</i><div><h3>Off-cycle employees</h3><p>Select employees and approved off-cycle adjustments to include.</p></div></header><div className="roster-actions"><button type="button" onClick={() => p.setSelectedIds(p.employees.map(employee => employee.id))}>Select all</button><button type="button" onClick={() => p.setSelectedIds([])}>Clear</button><span>{selected.size} selected</span></div><DataTable rows={p.employees} title="Employees to pay" getRowId={row => row.id} emptyText="No employees found for this client." exportFileName="off-cycle-employees" columns={columns} pageSizeOptions={[25, 50, 100, 250]} /><h3>Approved off-cycle adjustments</h3><div className="adjustment-select-list">{p.adjustments.map(row => <label key={row.id}><Checkbox checked={p.selectedAdjustmentIds.includes(row.id)} onChange={event => p.setSelectedAdjustmentIds(ids => event.target.checked ? [...ids, row.id] : ids.filter(id => id !== row.id))} /><span><strong>{row.employeeName} / {row.componentName}</strong><small>{row.adjustmentType} / {row.reasonCode} / {money(row.amount)}</small></span></label>)}{!p.adjustments.length && <p className="empty">No approved off-cycle adjustments for this period.</p>}</div></div>
}

function employeeSelectionColumns(selectedIds: number[], setSelectedIds: (fn: number[] | ((ids: number[]) => number[])) => void, selectLabel: string): Column<Employee>[] {
  const selected = new Set(selectedIds)
  const toggle = (employeeId: number, checked: boolean) => setSelectedIds(ids => checked ? ids.includes(employeeId) ? ids : [...ids, employeeId] : ids.filter(id => id !== employeeId))
  return [
    { key: 'selected', label: selectLabel, sortable: false, filterable: false, width: '76px', render: employee => <Checkbox checked={selected.has(employee.id)} onChange={event => toggle(employee.id, event.target.checked)} />, exportValue: employee => selected.has(employee.id) ? 'Yes' : 'No' },
    { key: 'employeeName', label: 'Employee', value: employee => `${employee.firstName} ${employee.lastName}`.trim(), render: employee => <>{employee.firstName} {employee.lastName}<small>{employee.employeeCode}</small></> },
    { key: 'employeeCode', label: 'Code' },
    { key: 'department', label: 'Department', value: employee => employee.department || 'No department' },
    { key: 'monthlyCost', label: 'Monthly cost', value: employee => Math.round(Number(employee.annualCtc || 0) / 12), render: employee => money(Number(employee.annualCtc || 0) / 12) }
  ]
}

function AdjustmentMini(p: { rows: PayrollAdjustment[]; title: string; edit?: (row: PayrollAdjustment) => void; cancel?: (id: number) => Promise<void> }) {
  const columns: Column<PayrollAdjustment>[] = [
    { key: 'employeeName', label: 'Employee', render: row => <>{row.employeeName}<small>{row.employeeCode}</small></>, exportValue: row => `${row.employeeName} ${row.employeeCode}`.trim() },
    { key: 'adjustmentType', label: 'Type', render: row => <>{row.adjustmentType}<small>{row.payRunType}</small></>, exportValue: row => `${row.adjustmentType} / ${row.payRunType}` },
    { key: 'componentName', label: 'Component', render: row => <>{row.componentName}<small>{row.reasonCode}</small></>, exportValue: row => `${row.componentName} / ${row.reasonCode}` },
    { key: 'amount', label: 'Amount', value: row => Number(row.amount || 0), render: row => money(row.amount) },
    { key: 'status', label: 'Status' }
  ]
  return <div className="adjustment-mini"><DataTable rows={p.rows} title={p.title} getRowId={row => row.id} emptyText="No adjustments found." exportFileName={p.title.toLowerCase().replace(/\s+/g, '-')} columns={columns} pageSizeOptions={[5, 10, 25]} hideInactiveClear actions={row => <span className="adjustment-actions">{p.edit && row.status !== 'Applied' && <button type="button" onClick={() => p.edit?.(row)}>Edit</button>}{p.cancel && row.status !== 'Applied' && <button type="button" className="danger" onClick={() => void p.cancel?.(row.id)}>Cancel</button>}</span>} /></div>
}

function ReviewRunPicker(p: { runs: PayRun[]; selectedId?: number; openRun: (run: PayRun) => Promise<void>; deleteRun: (run: PayRun) => Promise<void> }) {
  const columns: Column<PayRun>[] = [
    { key: 'payPeriod', label: 'Month', width: '120px' },
    { key: 'runName', label: 'Run', render: run => <>{run.runName || 'Regular payroll'}<small>{run.runType || 'Regular'}</small></>, exportValue: run => `${run.runName || 'Regular payroll'} / ${run.runType || 'Regular'}` },
    { key: 'status', label: 'Status', width: '140px', render: run => <span className={`status-chip ${run.status.toLowerCase().replace(/\s+/g, '-')}`}>{run.status}</span>, exportValue: run => run.status },
    { key: 'processingError', label: 'Failure reason', width: '260px', render: run => run.status === 'Failed' ? <span className="payroll-failure-reason">{run.processingError || 'Open diagnostics to view failure reason.'}</span> : '-', exportValue: run => run.status === 'Failed' ? run.processingError || '' : '' },
    { key: 'employeeCount', label: 'Employees', width: '110px', value: run => run.employeeCount },
    { key: 'netPay', label: 'Net payable', width: '150px', value: run => run.netPay, render: run => money(run.netPay) }
  ]
  return <section className="card payroll-review-runs"><header><i className="blue">D</i><div><h3>Payroll runs</h3><p>Select a run to load its saved review and approval data.</p></div></header><DataTable rows={p.runs} title="Payroll runs" getRowId={row => row.id} emptyText="No payroll run found for this client." exportFileName="payroll-runs" columns={columns} pageSizeOptions={[5, 10, 25]} actions={run => <span className="adjustment-actions"><button type="button" className={p.selectedId === run.id ? 'secondary' : ''} onClick={() => void p.openRun(run)}>{p.selectedId === run.id ? 'Opened' : 'Open'}</button><button type="button" className="danger" onClick={() => void p.deleteRun(run)}>Hard delete</button></span>} /></section>
}

export function PayRunReview(p: { selected: PayRun; diagnostics: PayRunDiagnostics | null; busy: boolean; materialVarianceCount: number; action?: (path: string, success: string) => Promise<void>; onChanged?: () => Promise<void>; actions?: ReactNode }) {
  const selected = p.selected
  const includedEmployees = selected.employees.filter(employee => !employee.isSkipped)
  const presentDays = presentDayTotal(includedEmployees.map(employee => employee.presentDays))
  const payableDays = includedEmployees.reduce((sum, employee) => sum + Number(employee.payableDays || 0), 0)
  const workingDays = selected.runType === 'Off Cycle' ? 0 : includedEmployees.reduce((sum, employee) => sum + savedPayrollDays(employee, selected.totalWorkingDays, selected.clientId, selected.payPeriod), 0)
  const lopDays = Math.max(0, workingDays - payableDays)
  const isHeld = selected.status === 'AwaitingProcessing'
  const isProcessing = processingStatuses.has(selected.status)
  const failureReason = selected.processingError || p.diagnostics?.exceptions[0]?.message || p.diagnostics?.validationIssues.find(issue => issue.isBlocking)?.message || p.diagnostics?.validationIssues[0]?.message || ''
  const defaultActions = p.action ? <div className="pay-run-actions"><button type="button" className="lock-action" disabled={p.busy || selected.status !== 'Draft'} onClick={() => void p.action?.('submit', 'Payroll locked and sent for approval.')} data-mail-event="PAYRUN.SUBMIT" data-mail-resource-type="PayRun" data-mail-resource-id={selected.id} data-mail-client-id={selected.clientId} data-mail-action-label="Lock payroll for approval">Lock payroll</button><button type="button" className="approve-action" disabled={p.busy || selected.status !== 'Pending Approval'} onClick={() => void p.action?.('approve', 'Payroll approved.')} data-mail-event="PAYRUN.APPROVED" data-mail-resource-type="PayRun" data-mail-resource-id={selected.id} data-mail-client-id={selected.clientId} data-mail-action-label="Approve payroll">Approve payroll</button><button type="button" className="secondary recall-action" disabled={p.busy || !['Approved', 'Pending Approval'].includes(selected.status)} onClick={() => void p.action?.('recall', 'Payroll recalled to draft.')} data-mail-event="PAYRUN.RECALL" data-mail-resource-type="PayRun" data-mail-resource-id={selected.id} data-mail-client-id={selected.clientId} data-mail-action-label="Recall payroll">Recall</button><a className="secondary export-action" href={exportPayRunUrl(selected.id)}>Export</a></div> : null
  const actions = p.actions === undefined ? <>{defaultActions}{selected.status === 'Draft' && p.onChanged && <DraftEmployerCosts key={selected.id} run={selected} onChanged={p.onChanged} />}</> : p.actions
  const salaryRegisterColumns = useMemo(() => buildSalaryRegisterColumns(selected.employees), [selected.employees])
  const componentDetailRows = useMemo(() => buildPayrollComponentDetailRows(selected.employees), [selected.employees])
  const columns: Column<RunEmployee>[] = [
    ...(selected.status === 'Draft' && p.onChanged ? [{ key: 'draftAdjustments', label: 'Correct employee', width: '210px', sortable: false, filterable: false, render: (row: RunEmployee) => <span className="adjustment-actions"><EmployeePayrollRecalculation key={`${selected.id}:${row.employeeId}:${row.detailsJson}`} run={selected} employee={row} disabled={p.busy} onChanged={p.onChanged!} /><DraftPayrollAdjustments run={selected} employee={row} disabled={p.busy} onChanged={p.onChanged!} /></span> }] : []),
    { key: 'employeeCode', label: 'Code', width: '110px' },
    { key: 'employeeName', label: 'Employee', width: '190px', render: employee => <>{employee.employeeName}<small>{employee.department || employee.employeeCode}</small></>, exportValue: row => `${row.employeeName} ${row.employeeCode}`.trim() },
    { key: 'presentDays', label: 'Present', width: '110px', value: row => row.presentDays, render: row => presentDayText(row.presentDays), exportValue: row => row.presentDays ?? 'Not supplied' },
    { key: 'payableDays', label: 'Payable', width: '95px', value: row => row.payableDays ?? 0 },
    { key: 'leaveBreakdown', label: 'Leave', sortable: false, filterable: false, render: employee => <LeaveChips employee={employee} totalWorkingDays={savedPayrollDays(employee, selected.totalWorkingDays, selected.clientId, selected.payPeriod)} offcycle={selected.runType === 'Off Cycle'} />, exportValue: row => leaveText(row, savedPayrollDays(row, selected.totalWorkingDays, selected.clientId, selected.payPeriod), selected.runType === 'Off Cycle') },
    ...salaryRegisterColumns,
    { key: 'grossPay', label: 'Gross', value: row => row.grossPay, render: row => money(row.grossPay) },
    { key: 'deductions', label: 'Total deductions', value: row => row.statutoryDeductions + row.oneTimeDeductions, render: row => money(row.statutoryDeductions + row.oneTimeDeductions) },
    { key: 'netPay', label: 'Net', value: row => row.netPay, render: row => <strong>{money(row.netPay)}</strong> },
    { key: 'previousNetPay', label: 'Last net', value: row => row.previousNetPay ?? 0, render: row => row.previousNetPay == null ? '-' : money(row.previousNetPay) },
    { key: 'previousTwoMonthAverageNetPay', label: '2-mo avg', value: row => row.previousTwoMonthAverageNetPay ?? 0, render: row => row.previousTwoMonthAverageNetPay == null ? '-' : money(row.previousTwoMonthAverageNetPay) },
    { key: 'twoMonthAverageVariance', label: 'Variance', value: row => row.twoMonthAverageVariance ?? 0, render: row => row.twoMonthAverageVariance == null ? '-' : <span className={`variance-badge ${Math.abs(row.twoMonthAverageVariancePercent || 0) >= 10 ? 'risk' : ''}`}>{money(row.twoMonthAverageVariance)}<small>{row.twoMonthAverageVariancePercent == null ? '' : `${row.twoMonthAverageVariancePercent}%`}</small></span>, exportValue: row => row.twoMonthAverageVariance ?? '' }
  ]
  return <section className="card pay-run-details enterprise-review"><header><i className="blue">R</i><div><h3>{selected.clientName} / {selected.payPeriod}</h3><p>{selected.runType || 'Regular'} / {selected.runName || 'Regular payroll'} / {selected.status} / {isHeld ? 'Employee scope awaits explicit processing review; no calculated rows' : selected.employeeCount + ' included employees'}</p></div><span className={`status-chip ${selected.status.toLowerCase().replace(/\s+/g, '-')}`}>{selected.status}</span></header>{actions}{isProcessing && <p className="payment-warning">Payroll request is under process. This review will refresh automatically and show pass/fail diagnostics once the worker completes.</p>}{selected.status === 'Failed' && <p className="payment-warning"><b>Failure reason:</b> {failureReason || 'Open diagnostics below to trace the failure.'}</p>}<div className="pay-run-summary"><div><span>Payroll cost</span><strong>{isHeld ? 'Not calculated' : money(selected.payrollCost)}</strong></div><div><span>Net payable</span><strong>{isHeld ? 'Not calculated' : money(selected.netPay)}</strong></div><div><span>Payable days</span><strong>{isHeld ? 'Not calculated' : payableDays}</strong></div><div><span>LOP days</span><strong>{isHeld ? 'Not calculated' : lopDays}</strong></div><div><span>Present days</span><strong>{presentDayText(presentDays)}</strong></div><div><span>Variance alerts</span><strong>{p.materialVarianceCount}</strong></div></div>{selected.runType === 'Off Cycle' && <p className="payment-warning">Off-cycle runs do not use monthly attendance payable days. Regular payroll pulls payable days from Attendance Review.</p>}<div className="pay-run-table"><DataTable rows={selected.employees} title="Employee payroll review" getRowId={row => row.employeeId} rowClassName={row => row.isSkipped ? 'skipped' : ''} emptyText={isHeld ? 'Selected draft awaits explicit processing review.' : isProcessing ? 'Payroll is queued or processing.' : 'No employees in this run.'} exportFileName={`pay-run-${selected.payPeriod}`} columns={columns} pageSizeOptions={[10, 25, 50, 100]} /></div><div className="pay-run-table component-detail-table"><DataTable rows={componentDetailRows} title="Component-wise payroll detail" getRowId={row => row.id} emptyText={isProcessing ? 'Payroll is queued or processing.' : 'No payroll component lines found.'} exportFileName={`pay-run-components-${selected.payPeriod}`} columns={componentDetailColumns} pageSizeOptions={[25, 50, 100, 200]} /></div><DiagnosticsPanel diagnostics={p.diagnostics} payPeriod={selected.payPeriod} /></section>
}

function DiagnosticsPanel({ diagnostics, payPeriod }: { diagnostics: PayRunDiagnostics | null; payPeriod: string }) {
  if (!diagnostics) return <section className="payroll-diagnostics"><p className="empty">Diagnostics are not available for this run yet.</p></section>
  const failedSteps = diagnostics.stepLogs.filter(row => row.status !== 'Success')
  const failedRecon = diagnostics.reconciliationResults.filter(row => row.status !== 'Passed')
  const validationRows = diagnostics.validationIssues.slice(0, 50)
  const traceRows = diagnostics.calculationTraces.slice(0, 100)
  return <section className="payroll-diagnostics"><div className="pay-run-summary diagnostics-summary"><div><span>Pipeline steps</span><strong>{diagnostics.stepLogs.length}</strong></div><div><span>Failed steps</span><strong>{failedSteps.length}</strong></div><div><span>Validation issues</span><strong>{diagnostics.validationIssues.length}</strong></div><div><span>Recon failures</span><strong>{failedRecon.length}</strong></div><div><span>Trace rows</span><strong>{diagnostics.calculationTraces.length}</strong></div></div><DataTable rows={diagnostics.reconciliationResults} title="Reconciliation controls" getRowId={row => row.id} emptyText="No reconciliation checks found." exportFileName={`reconciliation-${payPeriod}`} pageSizeOptions={[5, 10, 25]} columns={[
    { key: 'status', label: 'Status', render: row => <span className={`status-chip ${row.status.toLowerCase()}`}>{row.status}</span>, exportValue: row => row.status },
    { key: 'checkName', label: 'Check' },
    { key: 'expectedAmount', label: 'Expected', value: row => row.expectedAmount, render: row => money(row.expectedAmount) },
    { key: 'actualAmount', label: 'Actual', value: row => row.actualAmount, render: row => money(row.actualAmount) },
    { key: 'differenceAmount', label: 'Difference', value: row => row.differenceAmount, render: row => money(row.differenceAmount) }
  ]} /><DataTable rows={validationRows} title="Validation and exception log" getRowId={row => row.id} emptyText="No validation issues found." exportFileName={`validation-${payPeriod}`} pageSizeOptions={[5, 10, 25]} columns={[
    { key: 'severity', label: 'Severity' },
    { key: 'employeeCode', label: 'Employee' },
    { key: 'stepName', label: 'Step' },
    { key: 'message', label: 'Message' },
    { key: 'isBlocking', label: 'Blocking', render: row => row.isBlocking ? 'Yes' : 'No', exportValue: row => row.isBlocking ? 'Yes' : 'No' }
  ]} /><DataTable rows={traceRows} title="Calculation trace sample" getRowId={row => row.id} emptyText="No calculation trace rows found." exportFileName={`calculation-trace-${payPeriod}`} pageSizeOptions={[10, 25, 50]} columns={[
    { key: 'employeeCode', label: 'Employee' },
    { key: 'componentCode', label: 'Component', render: row => <>{row.componentCode}<small>{row.componentName}</small></>, exportValue: row => `${row.componentCode} ${row.componentName}` },
    { key: 'formulaUsed', label: 'Formula' },
    { key: 'baseAmount', label: 'Base', value: row => row.baseAmount, render: row => money(row.baseAmount) },
    { key: 'factor', label: 'Factor' },
    { key: 'calculatedAmount', label: 'Amount', value: row => row.calculatedAmount, render: row => money(row.calculatedAmount) }
  ]} /></section>
}

function isAdjustmentComponent(component: Component) {
  return component.active && ['Earning', 'Deduction', 'Reimbursement', 'Benefit', 'Correction'].includes(component.category)
}

function buildSalaryRegisterColumns(rows: RunEmployee[]): Column<RunEmployee>[] {
  const components = new Map<string, { code: string; label: string; category: string; order: number }>()
  rows.forEach(row => salaryLines(row).forEach((line, index) => {
    const code = lineCode(line)
    const category = lineCategory(line)
    if (!code || category === 'Summary') return
    const label = lineName(line) || code
    const key = `${category}:${code}`
    if (!components.has(key)) components.set(key, { code, label: `${componentPrefix(category)} ${label}`, category, order: index })
  }))
  return Array.from(components.values())
    .sort((a, b) => componentGroupOrder(a.category) - componentGroupOrder(b.category) || a.order - b.order || a.label.localeCompare(b.label))
    .map(component => ({
      key: `component_${component.category}_${component.code}`,
      label: component.label,
      value: row => componentAmount(row, component.code, component.category),
      render: row => money(componentAmount(row, component.code, component.category)),
      exportValue: row => componentAmount(row, component.code, component.category),
      width: '140px'
    }))
}

type PayrollComponentDetailRow = { id: string; employeeCode: string; employeeName: string; department: string; category: string; componentRole: string; statutoryType: string; componentCode: string; componentName: string; monthlyRate: number; amount: number; proRata: string }

const componentDetailColumns: Column<PayrollComponentDetailRow>[] = [
  { key: 'employeeCode', label: 'Code', width: '110px' },
  { key: 'employeeName', label: 'Employee', width: '190px', render: row => <>{row.employeeName}<small>{row.department || row.employeeCode}</small></>, exportValue: row => `${row.employeeName} ${row.employeeCode}`.trim() },
  { key: 'category', label: 'Category', width: '130px' },
  { key: 'componentRole', label: 'Role', width: '170px' },
  { key: 'statutoryType', label: 'Statutory', width: '150px', render: row => row.statutoryType && row.statutoryType !== 'None' ? row.statutoryType : '-', exportValue: row => row.statutoryType },
  { key: 'componentCode', label: 'Component code', width: '140px' },
  { key: 'componentName', label: 'Component', width: '220px' },
  { key: 'monthlyRate', label: 'Monthly rate', width: '130px', value: row => row.monthlyRate, render: row => money(row.monthlyRate) },
  { key: 'amount', label: 'Amount', width: '130px', value: row => row.amount, render: row => money(row.amount) },
  { key: 'proRata', label: 'Pro-rata', width: '110px' }
]

function buildPayrollComponentDetailRows(rows: RunEmployee[]): PayrollComponentDetailRow[] {
  return rows.flatMap(employee => salaryLines(employee)
    .map((line, index) => {
      const category = lineCategory(line)
      const code = lineCode(line)
      if (!code || category === 'Summary') return null
      return {
        id: `${employee.employeeId}-${index}-${category}-${code}`,
        employeeCode: employee.employeeCode,
        employeeName: employee.employeeName,
        department: employee.department,
        category,
        componentRole: lineRole(line),
        statutoryType: lineStatutoryType(line),
        componentCode: code,
        componentName: lineName(line) || code,
        monthlyRate: lineMonthlyAmount(line),
        amount: lineAmount(line),
        proRata: lineProRata(line) ? 'Yes' : 'No'
      }
    })
    .filter((row): row is PayrollComponentDetailRow => row !== null))
}

function salaryLines(employee: RunEmployee): PayRunSalaryLine[] {
  if (!employee.detailsJson) return []
  try {
    const parsed = JSON.parse(employee.detailsJson)
    return Array.isArray(parsed) ? parsed : []
  } catch {
    return []
  }
}

function componentAmount(employee: RunEmployee, code: string, category: string) {
  return salaryLines(employee).filter(line => lineCode(line) === code && lineCategory(line) === category).reduce((sum, line) => sum + lineAmount(line), 0)
}

function lineCode(line: PayRunSalaryLine) {
  return String(line.id ?? line.Id ?? '').trim()
}

function lineName(line: PayRunSalaryLine) {
  return String(line.name ?? line.Name ?? '').trim()
}

function lineCategory(line: PayRunSalaryLine) {
  return String(line.category ?? line.Category ?? '').trim()
}

function lineRole(line: PayRunSalaryLine) {
  return String(line.componentRole ?? line.ComponentRole ?? '').trim()
}

function lineStatutoryType(line: PayRunSalaryLine) {
  return String(line.statutoryType ?? line.StatutoryType ?? 'None').trim() || 'None'
}

function lineMonthlyAmount(line: PayRunSalaryLine) {
  return Number(line.monthlyAmount ?? line.MonthlyAmount ?? 0)
}

function lineAmount(line: PayRunSalaryLine) {
  return Number(line.amount ?? line.Amount ?? 0)
}

function lineProRata(line: PayRunSalaryLine) {
  return Boolean(line.proRata ?? line.ProRata)
}

function componentPrefix(category: string) {
  if (['Deduction', 'Recovery'].includes(category)) return 'D:'
  if (category === 'Reimbursement') return 'R:'
  if (category === 'Benefit') return 'B:'
  if (category === 'Correction') return 'C:'
  return 'E:'
}

function componentGroupOrder(category: string) {
  if (['Earning', 'Benefit', 'Reimbursement'].includes(category)) return 1
  if (['Deduction', 'Recovery'].includes(category)) return 2
  return 3
}

function LeaveChips(p: { employee: RunEmployee; totalWorkingDays: number; offcycle: boolean }) {
  const items = leaveItems(p.employee, p.totalWorkingDays, p.offcycle)
  return <span className="leave-chip-list">{items.length ? items.map(item => <span className={`leave-chip ${item.type.toLowerCase()}`} title={item.name} key={item.code}>{item.code} {item.days}d</span>) : <small>No leave</small>}</span>
}

function leaveItems(employee: RunEmployee, totalWorkingDays: number, offcycle: boolean) {
  const rows = employee.leaveBreakdown ?? []
  if (rows.length || offcycle || employee.isSkipped) return rows
  const lop = Math.max(0, totalWorkingDays - Number(employee.payableDays || 0))
  return lop ? [{ code: 'LOP', name: 'Loss of Pay', type: 'Unpaid', days: lop, payableDays: 0 }] : []
}

function leaveText(employee: RunEmployee, totalWorkingDays: number, offcycle: boolean) {
  return leaveItems(employee, totalWorkingDays, offcycle).map(item => `${item.code} ${item.days}d`).join(', ')
}

function isDeductionAdjustment(adjustment: PayrollAdjustment) {
  return ['Deduction', 'Recovery'].includes(adjustment.adjustmentType)
}
