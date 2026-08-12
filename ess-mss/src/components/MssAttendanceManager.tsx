import { useEffect, useMemo, useRef, useState, type ChangeEvent, type MouseEvent as ReactMouseEvent } from 'react'
import { DownloadOutlined, UploadOutlined } from '@ant-design/icons'
import { Button, Card, Input, Space, Typography } from 'antd'
import type { AttendanceBatchJobStatus, AttendanceReviewContext, Drop, EmployeeDailyAttendance, EmployeeMonthlyAttendance, LeaveType } from '../types'
import { getAttendanceReviewContextResult, getDailyAttendanceBatchJob, getDailyAttendanceGridResult, getLeaveTypesResult, getMonthlyAttendanceResult, startDailyAttendanceBatchJob } from '../services/mssAttendanceService'
import { getDropdownsResult } from '../services/mssAttendanceService'
import BulkUploadProgressModal, { type BulkUploadState } from './MssAttendanceProgressModal'
import SearchSelect from './MssAttendanceSearchSelect'
type ToastType = 'success' | 'error' | 'warning' | 'info'

type Props = { clientId: number; reviewMonth?: string; onMessage: (message: string, type?: ToastType) => void }
type DailyStatus = string
type ReviewStatus = 'Ready' | 'Missing attendance' | 'Check values'
type GridEdit = { employeeId: number; date: string } | null
type CellSelectionMode = 'all' | number | null
type CellSelectionAnchor = { employeeId: number; date: string } | null
type BulkScope = 'all' | 'cycle' | 'weekends' | 'saturday' | 'sunday' | 'date'
type RowPatch = Partial<Pick<EmployeeDailyAttendance, 'checkInTime' | 'checkOutTime' | 'payableValue'>>
type WorkWeekConfig = { workingDays: number[]; offSaturdays: number[] }
type AttendanceSaveProgress = { open: boolean; state: BulkUploadState; percent: number; summary: AttendanceBatchJobStatus }

const fallbackContext: AttendanceReviewContext = {
  settings: { id: 0, clientId: 0, checkInTime: '09:00:00', checkOutTime: '18:00:00', workingHoursCalculation: 'First check-in and last check-out', minimumHoursForHalfDay: 4, minimumHoursForFullDay: 8, maximumHoursAllowedForFullDay: 12, allowRegularizationRequests: true, regularizationWindow: 'Anytime', pastDaysAllowed: 7, restrictRegularizationRequestsPerMonth: false, maxRegularizationRequestsPerMonth: 3 },
  schedule: { workWeek: '', salaryDays: 'Actual days', fixedDays: '30', payDay: 'Last working day', firstPayPeriod: '' },
  preferences: { id: 0, clientId: 0, workLocationId: null, workLocationName: 'All locations', workWeek: '', attendanceCycleStartDay: 1, attendanceCycleEndDay: 25, payrollReportGenerationDay: 28, includeLeaveEncashmentInPayRun: false, leaveEncashmentSalaryComponentId: null },
  holidays: [],
  leaveBalances: []
}

const bulkScopeOptions = [
  { value: 'all', label: 'All visible dates' },
  { value: 'cycle', label: 'Attendance cycle' },
  { value: 'weekends', label: 'Sat + Sun' },
  { value: 'saturday', label: 'Saturdays' },
  { value: 'sunday', label: 'Sundays' },
  { value: 'date', label: 'Selected date' }
]

const currentMonth = () => new Date().toISOString().slice(0, 7)
const wait = (ms: number) => new Promise(resolve => window.setTimeout(resolve, ms))
const toNumber = (value: number | string | undefined | null) => Number.isFinite(Number(value)) ? Number(value) : 0
const isoDate = (value: string) => value.slice(0, 10)
const attendanceCellKey = (employeeId: number, date: string) => `${employeeId}|${date}`
const attendanceCellFromKey = (key: string) => {
  const [employeeId, date] = key.split('|')
  return { employeeId: Number(employeeId), date }
}
const unique = (items: string[]) => Array.from(new Set(items.map(item => item.trim()).filter(Boolean))).sort((a, b) => a.localeCompare(b))
const monthStartDate = (month: string) => {
  const [year, monthNumber] = month.split('-').map(Number)
  return new Date(year, monthNumber - 1, 1)
}
const addMonths = (date: Date, months: number) => new Date(date.getFullYear(), date.getMonth() + months, 1)
const ymd = (date: Date) => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
const clampDay = (date: Date, day: number) => Math.min(Math.max(1, day || 1), new Date(date.getFullYear(), date.getMonth() + 1, 0).getDate())
const cycleRangeFor = (month: string, startDay: number, endDay: number) => {
  const endMonth = monthStartDate(month)
  const startMonth = startDay > 1 ? addMonths(endMonth, -1) : endMonth
  const start = new Date(startMonth.getFullYear(), startMonth.getMonth(), clampDay(startMonth, startDay))
  const end = new Date(endMonth.getFullYear(), endMonth.getMonth(), clampDay(endMonth, endDay))
  return { start: ymd(start), end: ymd(end) }
}
const dateRangeDates = (start: string, end: string) => {
  const rows: string[] = []
  const current = new Date(`${start}T00:00:00`), last = new Date(`${end}T00:00:00`)
  while (current <= last) {
    rows.push(ymd(current))
    current.setDate(current.getDate() + 1)
  }
  return rows
}
const monthsInRange = (start: string, end: string) => unique(dateRangeDates(start, end).map(date => date.slice(0, 7)))
const rangeLabel = (start: string, end: string) => `${new Date(`${start}T00:00:00`).toLocaleDateString(undefined, { day: '2-digit', month: 'short', year: 'numeric' })} - ${new Date(`${end}T00:00:00`).toLocaleDateString(undefined, { day: '2-digit', month: 'short', year: 'numeric' })}`
const attendanceSaveStatus = (clientId: number, month: string, totalRows = 0, stage = 'Preparing'): AttendanceBatchJobStatus => ({ jobId: '', clientId, month, state: 'Queued', stage, totalRows, completedRows: 0, savedRows: 0, errors: [] })
const attendanceSavePercent = (job: AttendanceBatchJobStatus) => job.state === 'Completed'
  ? 100
  : job.totalRows > 0
    ? Math.max(5, Math.min(99, Math.round((job.completedRows / job.totalRows) * 100)))
    : job.state === 'Processing' ? 10 : 5
const csvEscape = (value: unknown) => {
  const text = String(value ?? '')
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
}

const parseCsv = (text: string) => {
  const rows: string[][] = []
  let row: string[] = [], cell = '', quoted = false
  for (let index = 0; index < text.length; index += 1) {
    const char = text[index]
    if (char === '"') {
      if (quoted && text[index + 1] === '"') { cell += '"'; index += 1 } else quoted = !quoted
    } else if (char === ',' && !quoted) { row.push(cell); cell = '' }
    else if ((char === '\n' || char === '\r') && !quoted) {
      if (char === '\r' && text[index + 1] === '\n') index += 1
      row.push(cell); if (row.some(value => value.trim())) rows.push(row); row = []; cell = ''
    } else cell += char
  }
  row.push(cell)
  if (row.some(value => value.trim())) rows.push(row)
  return rows
}

const cleanTime = (value?: string | null) => {
  if (!value) return ''
  const [hour, minute] = String(value).split(':')
  return hour && minute ? `${hour.padStart(2, '0')}:${minute.padStart(2, '0')}` : ''
}
const apiTime = (value?: string | null) => {
  const time = cleanTime(value)
  return time ? `${time}:00` : null
}
const hoursBetween = (checkIn?: string | null, checkOut?: string | null) => {
  const start = cleanTime(checkIn), end = cleanTime(checkOut)
  if (!start || !end) return 0
  const [sh, sm] = start.split(':').map(Number), [eh, em] = end.split(':').map(Number)
  let minutes = (eh * 60 + em) - (sh * 60 + sm)
  if (minutes < 0) minutes += 1440
  return Math.round((minutes / 60) * 100) / 100
}
const parseWorkWeekConfig = (value: string, drops: Drop[]) => {
  const row = drops.find(item => item.type === 'Work Week' && item.isActive && item.value === value && item.configJson)
  if (!row?.configJson) return null
  try {
    const parsed = JSON.parse(row.configJson) as WorkWeekConfig
    const workingDays = Array.from(new Set((parsed.workingDays ?? []).map(Number).filter(day => day >= 0 && day <= 6)))
    const offSaturdays = Array.from(new Set((parsed.offSaturdays ?? []).map(Number).filter(day => day >= 1 && day <= 5)))
    return workingDays.length ? { workingDays, offSaturdays } : null
  } catch {
    return null
  }
}
const isWorkingDateFor = (workWeek: string, date: string, config?: WorkWeekConfig | null) => {
  const current = new Date(`${date}T00:00:00`)
  const day = current.getDay()
  if (config) {
    if (!config.workingDays.includes(day)) return false
    if (day === 6) {
      const saturdayNumber = Math.ceil(current.getDate() / 7)
      return !config.offSaturdays.includes(saturdayNumber)
    }
    return true
  }
  const text = workWeek.toLowerCase()
  if (!text.trim()) return true
  if (text.includes('no fixed')) return true
  if (text.includes('all')) return true
  if (text.includes('friday-saturday') || text.includes('friday saturday')) return ![5, 6].includes(day)
  if (text.includes('friday off')) return day !== 5
  if (text.includes('saturday-sunday') || text.includes('saturday sunday')) return ![0, 6].includes(day)
  if (day === 0) return false
  if (day !== 6) return day >= 1 && day <= 5
  const saturdayNumber = Math.ceil(current.getDate() / 7)
  if (text.includes('second & fourth') || text.includes('alternate saturday')) return ![2, 4].includes(saturdayNumber)
  if (text.includes('2nd/4th') || text.includes('2nd and 4th')) return ![2, 4].includes(saturdayNumber)
  if (text.includes('second saturday')) return saturdayNumber !== 2
  if (text.includes('2nd saturday') || text.includes('only 2nd')) return saturdayNumber !== 2
  return text.includes('saturday')
}
const reviewStatus = (row: EmployeeMonthlyAttendance): ReviewStatus => {
  const workingDays = toNumber(row.workingDays), presentDays = toNumber(row.presentDays), payableDays = toNumber(row.payableDays), lopDays = toNumber(row.lopDays)
  if (workingDays <= 0 && presentDays <= 0 && payableDays <= 0) return 'Missing attendance'
  if (payableDays < 0 || presentDays < 0 || lopDays < 0 || payableDays > workingDays || presentDays > workingDays) return 'Check values'
  if (Math.abs((payableDays + lopDays) - workingDays) > 0.01) return 'Check values'
  return 'Ready'
}

export default function ManualAttendanceManager({ clientId, reviewMonth = '', onMessage }: Props) {
  const [month, setMonth] = useState(reviewMonth || currentMonth())
  const [monthlyRows, setMonthlyRows] = useState<EmployeeMonthlyAttendance[]>([])
  const [allDailyRows, setAllDailyRows] = useState<EmployeeDailyAttendance[]>([])
  const [leaveTypes, setLeaveTypes] = useState<LeaveType[]>([])
  const [dropdowns, setDropdowns] = useState<Drop[]>([])
  const [reviewContext, setReviewContext] = useState<AttendanceReviewContext>(fallbackContext)
  const [query, setQuery] = useState('')
  const [departmentFilter, setDepartmentFilter] = useState('')
  const [reviewFilter, setReviewFilter] = useState<ReviewStatus | ''>('')
  const [gridEdit, setGridEdit] = useState<GridEdit>(null)
  const [bulkStatus, setBulkStatus] = useState<DailyStatus>('Present')
  const [bulkScope, setBulkScope] = useState<BulkScope>('all')
  const [bulkDate, setBulkDate] = useState(`${currentMonth()}-01`)
  const [dirtyCellKeys, setDirtyCellKeys] = useState<Set<string>>(new Set())
  const [selectedCellKeys, setSelectedCellKeys] = useState<Set<string>>(new Set())
  const [cellSelectionMode, setCellSelectionMode] = useState<CellSelectionMode>(null)
  const [cellSelectionAnchor, setCellSelectionAnchor] = useState<CellSelectionAnchor>(null)
  const [loadingMonthly, setLoadingMonthly] = useState(false)
  const [saving, setSaving] = useState(false)
  const [saveProgress, setSaveProgress] = useState<AttendanceSaveProgress>({ open: false, state: 'uploading', percent: 0, summary: attendanceSaveStatus(clientId, reviewMonth || currentMonth()) })
  const activeScope = useRef({ clientId, month })

  useEffect(() => {
    activeScope.current = { clientId, month }
  }, [clientId, month])

  useEffect(() => {
    if (reviewMonth && /^\d{4}-\d{2}$/.test(reviewMonth)) setMonth(reviewMonth)
  }, [reviewMonth])

  const settings = reviewContext.settings
  const preferences = reviewContext.preferences
  const activeLeaveTypes = useMemo(() => leaveTypes.filter((leaveType) => leaveType.isActive), [leaveTypes])
  const leaveTypeByCode = useMemo(() => new Map(activeLeaveTypes.map((leaveType) => [leaveType.code.toLowerCase(), leaveType])), [activeLeaveTypes])
  const statusChoices = useMemo(() => [
    { value: 'Present', label: 'P - Present' },
    { value: 'A', label: 'A - Absent' },
    { value: 'WO', label: 'WO - Weekly Off' },
    { value: 'H', label: 'H - Holiday' },
    ...activeLeaveTypes.map((leaveType) => ({ value: leaveType.code, label: `${leaveType.code} - ${leaveType.name}` }))
  ], [activeLeaveTypes])
  const statusOptions = statusChoices
  const employeeCycleRanges = useMemo(() => new Map(monthlyRows.map((row) => [
    row.employeeId,
    cycleRangeFor(month, row.attendanceCycleStartDay ?? preferences.attendanceCycleStartDay, row.attendanceCycleEndDay ?? preferences.attendanceCycleEndDay),
  ])), [month, monthlyRows, preferences.attendanceCycleEndDay, preferences.attendanceCycleStartDay])
  const employeeCycleDates = useMemo(() => new Map(Array.from(employeeCycleRanges, ([employeeId, range]) => [employeeId, dateRangeDates(range.start, range.end)])), [employeeCycleRanges])
  const fallbackCycleRange = useMemo(() => cycleRangeFor(month, preferences.attendanceCycleStartDay, preferences.attendanceCycleEndDay), [month, preferences.attendanceCycleEndDay, preferences.attendanceCycleStartDay])
  const cycleRange = useMemo(() => {
    const ranges = Array.from(employeeCycleRanges.values())
    if (!ranges.length) return fallbackCycleRange
    return {
      start: ranges.map((range) => range.start).sort()[0],
      end: ranges.map((range) => range.end).sort().at(-1) ?? fallbackCycleRange.end,
    }
  }, [employeeCycleRanges, fallbackCycleRange])
  const monthDays = useMemo(() => dateRangeDates(cycleRange.start, cycleRange.end), [cycleRange.start, cycleRange.end])
  const cycleRangeDisplay = useMemo(() => rangeLabel(cycleRange.start, cycleRange.end), [cycleRange.start, cycleRange.end])
  const selectedBulkDate = monthDays.includes(bulkDate) ? bulkDate : monthDays[0]
  const workWeekConfigMap = useMemo(() => new Map(dropdowns.filter(item => item.type === 'Work Week' && item.isActive && item.configJson).map(item => [item.value, parseWorkWeekConfig(item.value, dropdowns)])), [dropdowns])
  const bulkDates = useMemo(() => monthDays.filter((date) => {
    const day = new Date(`${date}T00:00:00`).getDay()
    if (bulkScope === 'cycle') return true
    if (bulkScope === 'weekends') return day === 0 || day === 6
    if (bulkScope === 'saturday') return day === 6
    if (bulkScope === 'sunday') return day === 0
    if (bulkScope === 'date') return date === selectedBulkDate
    return true
  }), [monthDays, bulkScope, selectedBulkDate])
  const datesForEmployee = (row: EmployeeMonthlyAttendance) => employeeCycleDates.get(row.employeeId) ?? dateRangeDates(fallbackCycleRange.start, fallbackCycleRange.end)
  const isDateInEmployeeCycle = (row: EmployeeMonthlyAttendance, date: string) => datesForEmployee(row).includes(date)
  const departments = useMemo(() => unique(monthlyRows.map((row) => row.department)), [monthlyRows])
  const filteredRows = useMemo(() => monthlyRows.filter((row) => {
    const text = `${row.employeeName} ${row.employeeCode} ${row.department}`.toLowerCase()
    return (!query || text.includes(query.toLowerCase())) && (!departmentFilter || row.department === departmentFilter) && (!reviewFilter || reviewStatus(row) === reviewFilter)
  }), [monthlyRows, query, departmentFilter, reviewFilter])
  const dailyByEmployee = useMemo(() => {
    const map = new Map<number, Map<string, EmployeeDailyAttendance>>()
    allDailyRows.forEach((row) => {
      if (!map.has(row.employeeId)) map.set(row.employeeId, new Map())
      map.get(row.employeeId)!.set(isoDate(row.attendanceDate), row)
    })
    return map
  }, [allDailyRows])
  const balanceMap = useMemo(() => new Map(reviewContext.leaveBalances.map((row) => [`${row.employeeId}:${row.leaveTypeCode.toLowerCase()}`, row.balance])), [reviewContext.leaveBalances])
  const summary = useMemo(() => monthlyRows.reduce((acc, row) => {
    const status = reviewStatus(row)
    if (status === 'Ready') acc.ready += 1
    if (status === 'Missing attendance') acc.missing += 1
    if (status === 'Check values') acc.check += 1
    acc.workingDays += toNumber(row.workingDays)
    acc.presentDays += toNumber(row.presentDays)
    acc.payableDays += toNumber(row.payableDays)
    acc.lopDays += toNumber(row.lopDays)
    return acc
  }, { total: monthlyRows.length, ready: 0, missing: 0, check: 0, workingDays: 0, presentDays: 0, payableDays: 0, lopDays: 0 }), [monthlyRows])
  const issueRows = useMemo(() => monthlyRows.filter((row) => reviewStatus(row) !== 'Ready'), [monthlyRows])
  const reviewPatternText = useMemo(() => {
    const policyNames = unique(monthlyRows.map(row => row.attendanceGroupName))
    const workWeeks = unique(monthlyRows.map(row => row.workWeek))
    if (policyNames.length > 1) return `${policyNames.length} assigned attendance policies`
    if (policyNames.length === 1) return `${policyNames[0]} / ${workWeeks[0] || 'No off pattern selected'}`
    return workWeeks[0] || reviewContext.schedule.workWeek || 'No attendance policy assigned'
  }, [monthlyRows, reviewContext.schedule.workWeek])

  const normalizeStatus = (status?: string) => {
    const text = (status || '').trim()
    if (!text) return ''
    if (['p', 'present'].includes(text.toLowerCase())) return 'Present'
    if (['a', 'absent'].includes(text.toLowerCase())) return 'A'
    if (['wo', 'weekly off', 'week off'].includes(text.toLowerCase())) return 'WO'
    if (['h', 'holiday'].includes(text.toLowerCase())) return 'H'
    return activeLeaveTypes.find((leaveType) => leaveType.code.toLowerCase() === text.toLowerCase() || leaveType.name.toLowerCase() === text.toLowerCase())?.code ?? text.toUpperCase()
  }
  const payableForStatus = (status: DailyStatus, hours = 0, hasTimes = false) => {
    const normalized = normalizeStatus(status)
    if (normalized === 'Present') {
      if (hasTimes) return hours >= settings.minimumHoursForFullDay ? 1 : hours >= settings.minimumHoursForHalfDay ? 0.5 : 0
      return 1
    }
    if (['WO', 'H'].includes(normalized)) return 1
    if (['A', ''].includes(normalized)) return 0
    return leaveTypeByCode.get(normalized.toLowerCase())?.type === 'Paid' ? 1 : 0
  }
  const workWeekFor = (row: EmployeeMonthlyAttendance) => row.workWeek || reviewContext.schedule.workWeek || preferences.workWeek
  const isWorkingDate = (row: EmployeeMonthlyAttendance, date: string) => {
    const workWeek = workWeekFor(row)
    return isWorkingDateFor(workWeek, date, workWeekConfigMap.get(workWeek))
  }
  const holidayFor = (row: EmployeeMonthlyAttendance, date: string) => reviewContext.holidays.find((holiday) =>
    isoDate(holiday.startDate) <= date && isoDate(holiday.endDate) >= date && (holiday.allLocations || !holiday.workLocationIds.length || holiday.workLocationIds.includes(row.workLocationId)))
  const defaultStatusFor = (row: EmployeeMonthlyAttendance, date: string) => holidayFor(row, date) ? 'H' : isWorkingDate(row, date) ? '' : 'WO'
  const totalHoursFor = (row: EmployeeDailyAttendance) => toNumber(row.totalHours) || hoursBetween(row.checkInTime, row.checkOutTime)
  const makeRow = (employeeId: number, date: string, status: DailyStatus, existing?: EmployeeDailyAttendance, patch: RowPatch = {}): EmployeeDailyAttendance => {
    const normalized = normalizeStatus(status)
    const checkIn = normalized === 'Present' ? apiTime(patch.checkInTime !== undefined ? patch.checkInTime : existing?.checkInTime || settings.checkInTime) : null
    const checkOut = normalized === 'Present' ? apiTime(patch.checkOutTime !== undefined ? patch.checkOutTime : existing?.checkOutTime || settings.checkOutTime) : null
    const hours = normalized === 'Present' ? hoursBetween(checkIn, checkOut) : 0
    const payableValue = patch.payableValue ?? payableForStatus(normalized, hours, Boolean(checkIn && checkOut))
    return { id: existing?.id ?? 0, clientId, employeeId, attendanceDate: date, status: normalized, payableValue: Math.max(0, Math.min(1, payableValue)), checkInTime: checkIn, checkOutTime: checkOut, totalHours: hours, remarks: existing?.remarks || '' }
  }
  const cellText = (status: string, payableValue: number) => status === 'Present' ? payableValue === 0.5 ? 'P.5' : 'P' : payableValue === 0.5 ? `${status}.5` : status
  const gridCell = (employee: EmployeeMonthlyAttendance, date: string) => {
    if (!isDateInEmployeeCycle(employee, date)) return { text: '', cls: 'out-of-cycle', title: 'Outside this employee policy cycle', status: '', row: undefined as EmployeeDailyAttendance | undefined, hoursText: '' }
    const row = dailyByEmployee.get(employee.employeeId)?.get(date)
    const status = row ? normalizeStatus(row.status) : defaultStatusFor(employee, date)
    const holiday = holidayFor(employee, date)
    if (!status) return { text: '-', cls: 'missing', title: 'Missing', status: '', row: undefined as EmployeeDailyAttendance | undefined, hoursText: '' }
    const hours = row ? totalHoursFor(row) : 0
    const payable = row ? toNumber(row.payableValue) : 0
    const leave = leaveTypeByCode.get(status.toLowerCase())
    const cls = status === 'Present'
      ? hours > 0 && hours < settings.minimumHoursForHalfDay ? 'short' : hours > 0 && hours < settings.minimumHoursForFullDay ? 'half' : payable === 0.5 ? 'half' : payable === 0 ? 'short' : 'present'
      : status === 'WO' ? 'weekoff' : status === 'H' ? 'holiday' : status === 'A' ? 'absent' : leave?.type === 'Paid' ? 'paid' : 'absent'
    const hoursText = status === 'Present' && hours > 0 ? `${hours.toFixed(hours % 1 ? 1 : 0)}h` : ''
    return { text: cellText(status, payable), cls, title: holiday?.name || leave?.name || status, status, row, hoursText }
  }
  const missingCountFor = (employee: EmployeeMonthlyAttendance) => datesForEmployee(employee).filter((date) => !dailyByEmployee.get(employee.employeeId)?.has(date) && !defaultStatusFor(employee, date)).length
  const rowTone = (row: EmployeeMonthlyAttendance) => reviewStatus(row) === 'Ready' ? 'ready' : reviewStatus(row) === 'Missing attendance' ? 'warn' : 'danger'
  const shouldSkipBulk = (employee: EmployeeMonthlyAttendance, date: string) => {
    const defaultStatus = defaultStatusFor(employee, date)
    const current = defaultStatus || normalizeStatus(dailyByEmployee.get(employee.employeeId)?.get(date)?.status)
    return current === 'WO' || current === 'H' || leaveTypeByCode.has(current.toLowerCase())
  }

  const loadMonthly = async () => {
    setLoadingMonthly(true)
    try {
      const [contextResult, rowsResult, leaveTypeResult, dropdownResult] = await Promise.all([
        getAttendanceReviewContextResult(clientId, month),
        getMonthlyAttendanceResult(clientId, month),
        getLeaveTypesResult(clientId),
        getDropdownsResult()
      ])
      const failedResult = [contextResult, rowsResult, leaveTypeResult, dropdownResult].find(result => !result.ok)
      if (failedResult) throw new Error(failedResult.error || 'Unable to load attendance review data.')
      const rows = rowsResult.data
      const context = contextResult.data
      const dailyMonths = unique(rows.flatMap((row) => {
        const range = cycleRangeFor(month, row.attendanceCycleStartDay ?? context.preferences.attendanceCycleStartDay, row.attendanceCycleEndDay ?? context.preferences.attendanceCycleEndDay)
        return monthsInRange(range.start, range.end)
      }))
      const dailyGridResults = await Promise.all(dailyMonths.map(item => getDailyAttendanceGridResult(clientId, item)))
      const failedDailyResult = dailyGridResults.find(result => !result.ok)
      if (failedDailyResult) throw new Error(failedDailyResult.error || 'Unable to load daily attendance data.')
      const leaveTypeRows = leaveTypeResult.data
      const dailyGridRows = dailyGridResults.map(result => result.data)
      const dropdownRows = dropdownResult.data
      const directReportIds = new Set(rows.map(row => row.employeeId))
      const scopedDailyRows = dailyGridRows.flat().filter(row => directReportIds.has(row.employeeId))
      setMonthlyRows(rows); setLeaveTypes(leaveTypeRows); setAllDailyRows(scopedDailyRows); setReviewContext(context); setDropdowns(dropdownRows); setDirtyCellKeys(new Set()); setSelectedCellKeys(new Set()); setCellSelectionMode(null); setCellSelectionAnchor(null); setGridEdit(null)
      return true
    } catch (error) {
      onMessage(error instanceof Error ? error.message : 'Unable to load monthly attendance', 'error')
      return false
    } finally {
      setLoadingMonthly(false)
    }
  }

  useEffect(() => { void loadMonthly() }, [clientId, month])

  const gridRowMap = (rows: EmployeeDailyAttendance[]) => new Map(rows.map((row) => [attendanceCellKey(row.employeeId, isoDate(row.attendanceDate)), row]))
  const upsertGridRow = (rows: Map<string, EmployeeDailyAttendance>, employeeId: number, date: string, status: DailyStatus, patch: RowPatch = {}) => {
    const key = attendanceCellKey(employeeId, date)
    rows.set(key, makeRow(employeeId, date, status, rows.get(key), patch))
  }
  const updateGridStatus = (employeeId: number, date: string, status: DailyStatus) => {
    const employee = monthlyRows.find(row => row.employeeId === employeeId)
    if (saving || !employee || !isDateInEmployeeCycle(employee, date) || !normalizeStatus(status)) return
    setAllDailyRows((rows) => {
      const next = gridRowMap(rows)
      upsertGridRow(next, employeeId, date, status)
      return Array.from(next.values())
    })
    setDirtyCellKeys((keys) => new Set(keys).add(attendanceCellKey(employeeId, date)))
    setGridEdit(null)
  }
  const validateLeaveBalances = (prepared: Map<number, EmployeeDailyAttendance[]>) => {
    const requested = new Map<string, number>()
    prepared.forEach((rows, employeeId) => rows.forEach((row) => {
      const leave = leaveTypeByCode.get(normalizeStatus(row.status).toLowerCase())
      if (!leave || leave.type !== 'Paid' || leave.allowNegativeLeaveBalance) return
      const key = `${employeeId}:${leave.code.toLowerCase()}`
      requested.set(key, (requested.get(key) || 0) + (toNumber(row.payableValue) || 1))
    }))
    for (const [key, days] of requested) {
      const balance = balanceMap.get(key) || 0
      if (days > balance + 0.001) {
        const [employeeId, code] = key.split(':')
        const employee = monthlyRows.find((row) => row.employeeId === Number(employeeId))
        return `${employee?.employeeName || 'Employee'} ${code.toUpperCase()} balance ${balance}; selected ${days}.`
      }
    }
    return ''
  }
  const saveGridChanges = async (sourceRows = allDailyRows, keys = dirtyCellKeys) => {
    if (!keys.size || saving) return
    const rowByKey = new Map(sourceRows.map((row) => [attendanceCellKey(row.employeeId, isoDate(row.attendanceDate)), row]))
    const prepared = new Map<number, EmployeeDailyAttendance[]>()
    keys.forEach((key) => {
      const row = rowByKey.get(key)
      if (!row || !normalizeStatus(row.status)) return
      const normalized = makeRow(row.employeeId, isoDate(row.attendanceDate), row.status, row, { checkInTime: row.checkInTime, checkOutTime: row.checkOutTime, payableValue: row.payableValue })
      const employeeRows = prepared.get(row.employeeId) ?? []
      employeeRows.push(normalized)
      prepared.set(row.employeeId, employeeRows)
    })
    if (!prepared.size) {
      onMessage('Select a status for at least one edited cell.', 'warning')
      return
    }
    const balanceError = validateLeaveBalances(prepared)
    if (balanceError) { onMessage(balanceError, 'error'); return }
    const preparedRows = Array.from(prepared.values()).flat()
    const sourceByKey = new Map(sourceRows.map(row => [attendanceCellKey(row.employeeId, isoDate(row.attendanceDate)), row]))
    const employeeById = new Map(monthlyRows.map(row => [row.employeeId, row]))
    const rollupEmployeeIds = Array.from(prepared.keys()).filter(employeeId => {
      const employee = employeeById.get(employeeId)
      return employee && datesForEmployee(employee).every(date => sourceByKey.has(attendanceCellKey(employeeId, date)) || Boolean(defaultStatusFor(employee, date)))
    })
    const rowsToSave = [...preparedRows]
    rollupEmployeeIds.forEach(employeeId => {
      const employee = employeeById.get(employeeId)
      if (!employee) return
      datesForEmployee(employee).forEach(date => {
        if (sourceByKey.has(attendanceCellKey(employeeId, date))) return
        const status = defaultStatusFor(employee, date)
        if (status) rowsToSave.push(makeRow(employeeId, date, status))
      })
    })
    setSaveProgress({ open: true, state: 'uploading', percent: 1, summary: attendanceSaveStatus(clientId, month, rowsToSave.length, 'Queueing attendance save') })
    setSaving(true)
    try {
      const start = await startDailyAttendanceBatchJob(clientId, month, rowsToSave, rollupEmployeeIds)
      if (!start.ok || !start.data.jobId) {
        const message = start.data.errors?.[0] || start.error || 'Unable to start attendance save.'
        const failed = { ...attendanceSaveStatus(clientId, month, rowsToSave.length, 'Failed'), ...start.data, state: 'Failed' as const, stage: 'Failed', totalRows: start.data.totalRows || rowsToSave.length, errors: start.data.errors?.length ? start.data.errors : [message] }
        setSaveProgress({ open: true, state: 'error', percent: 100, summary: failed })
        onMessage(message, 'error')
        return
      }
      let job = { ...start.data, totalRows: start.data.totalRows || rowsToSave.length, errors: start.data.errors ?? [] }
      const pollingStartedAt = Date.now()
      let consecutivePollFailures = 0
      let mustConfirmJob = true
      while (mustConfirmJob || job.state === 'Queued' || job.state === 'Processing') {
        setSaveProgress({ open: true, state: 'uploading', percent: attendanceSavePercent(job), summary: job })
        await wait(job.state === 'Queued' || job.state === 'Processing' ? 700 : 100)
        const latest = await getDailyAttendanceBatchJob(job.jobId)
        if (!latest.ok) {
          consecutivePollFailures += 1
          if (consecutivePollFailures >= 5) throw new Error(`${latest.error || 'Unable to read attendance save progress.'} The background save may still be running; refresh the review before retrying.`)
          await wait(consecutivePollFailures * 500)
          continue
        }
        consecutivePollFailures = 0
        mustConfirmJob = false
        job = { ...latest.data, clientId: latest.data.clientId || clientId, month: latest.data.month || month, totalRows: latest.data.totalRows || job.totalRows || rowsToSave.length, errors: latest.data.errors ?? [] }
        if (Date.now() - pollingStartedAt > 15 * 60 * 1000) throw new Error('Attendance save is taking longer than expected. The background job is still available; refresh the review before retrying.')
      }
      if (job.state !== 'Completed') {
        const message = job.errors?.[0] || 'Attendance save failed. No cells were saved.'
        setSaveProgress({ open: true, state: 'error', percent: attendanceSavePercent(job), summary: { ...job, stage: job.stage || 'Failed', errors: job.errors?.length ? job.errors : [message] } })
        onMessage(message, 'error')
        return
      }
      const savedCells = job.savedRows
      if (activeScope.current.clientId !== clientId || activeScope.current.month !== month) {
        setDirtyCellKeys(new Set())
        setSaveProgress({ open: true, state: 'success', percent: 100, summary: { ...job, stage: 'Saved - refresh required' } })
        onMessage(`${savedCells} attendance ${savedCells === 1 ? 'cell' : 'cells'} saved. Attendance scope changed; refresh the selected review to load the result.`, 'warning')
        return
      }
      setSaveProgress({ open: true, state: 'uploading', percent: 99, summary: { ...job, stage: 'Refreshing attendance review' } })
      const refreshed = await loadMonthly()
      if (!refreshed) {
        setDirtyCellKeys(new Set())
        setSaveProgress({ open: true, state: 'success', percent: 100, summary: { ...job, stage: 'Saved - refresh required' } })
        onMessage(`${savedCells} attendance ${savedCells === 1 ? 'cell' : 'cells'} saved, but the review could not refresh. Click Refresh review before making more changes.`, 'warning')
        return
      }
      setSaveProgress({ open: true, state: 'success', percent: 100, summary: { ...job, stage: job.stage || 'Completed' } })
      onMessage(`${savedCells} attendance ${savedCells === 1 ? 'cell' : 'cells'} saved for ${prepared.size} ${prepared.size === 1 ? 'employee' : 'employees'}.`, 'success')
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unable to save attendance.'
      setSaveProgress((current) => ({ open: true, state: 'error', percent: current.percent, summary: { ...current.summary, state: 'Failed', stage: 'Failed', errors: [message] } }))
      onMessage(message, 'error')
    } finally {
      setSaving(false)
    }
  }

  const exportAttendanceCsv = () => {
    const rows = [
      ['EmployeeCode', 'EmployeeName', 'EmployeeId', 'Department', ...monthDays],
      ...filteredRows.map((row) => [row.employeeCode, row.employeeName, row.employeeId, row.department, ...monthDays.map((date) => {
        if (!isDateInEmployeeCycle(row, date)) return ''
        const cell = gridCell(row, date), source = cell.row
        if (cell.text === '-') return ''
        return source?.status === 'Present' && source.checkInTime && source.checkOutTime ? `${cell.text} ${cleanTime(source.checkInTime)}-${cleanTime(source.checkOutTime)}` : cell.text
      })])
    ]
    const blob = new Blob([rows.map((row) => row.map(csvEscape).join(',')).join('\r\n')], { type: 'text/csv;charset=utf-8' })
    const link = document.createElement('a')
    link.href = URL.createObjectURL(blob); link.download = `attendance-${month}.csv`; link.click(); URL.revokeObjectURL(link.href)
  }
  const parseAttendanceCell = (value: string) => {
    const text = value.trim()
    if (!text) return null
    const timeMatch = text.match(/(\d{1,2}:\d{2})\s*(?:-|to)\s*(\d{1,2}:\d{2})/i)
    let code = timeMatch ? text.replace(timeMatch[0], '').replace('@', '').trim() : text
    const half = /\.5$/i.test(code)
    code = code.replace(/\.5$/i, '').trim()
    const status = code ? normalizeStatus(code) : timeMatch ? 'Present' : ''
    if (!status) return null
    return { status, patch: { payableValue: half ? 0.5 : payableForStatus(status), checkInTime: timeMatch ? apiTime(timeMatch[1]) : undefined, checkOutTime: timeMatch ? apiTime(timeMatch[2]) : undefined } as RowPatch }
  }
  const importAttendanceCsv = async (event: ChangeEvent<HTMLInputElement>) => {
    if (saving) return
    const file = event.target.files?.[0]
    event.target.value = ''
    if (!file) return
    const rows = parseCsv(await file.text()), header = rows[0]?.map((value) => value.trim()) ?? []
    const dateIndexes = monthDays.map((date) => header.findIndex((column) => column === date))
    if (dateIndexes.some((index) => index < 0)) { onMessage('Selected file does not match current month template.', 'error'); return }
    const employeeIdIndex = header.findIndex((column) => column === 'EmployeeId'), employeeCodeIndex = header.findIndex((column) => column === 'EmployeeCode')
    const byId = new Map(monthlyRows.map((row) => [String(row.employeeId), row.employeeId])), byCode = new Map(monthlyRows.map((row) => [row.employeeCode.toLowerCase(), row.employeeId]))
    const nextRows = gridRowMap(allDailyRows)
    const imported = new Set<number>()
    const importedCellKeys = new Set<string>()
    for (const row of rows.slice(1)) {
      const employeeId = byId.get(row[employeeIdIndex]?.trim()) ?? byCode.get(row[employeeCodeIndex]?.trim().toLowerCase())
      if (!employeeId) continue
      const employee = monthlyRows.find(item => item.employeeId === employeeId)
      if (!employee) continue
      monthDays.forEach((date, index) => {
        if (!isDateInEmployeeCycle(employee, date)) return
        const parsed = parseAttendanceCell(row[dateIndexes[index]] ?? '')
        if (!parsed) return
        upsertGridRow(nextRows, employeeId, date, parsed.status, parsed.patch)
        imported.add(employeeId)
        importedCellKeys.add(attendanceCellKey(employeeId, date))
      })
    }
    if (!imported.size) { onMessage('No attendance rows imported.', 'warning'); return }
    const saveKeys = new Set([...dirtyCellKeys, ...importedCellKeys])
    const nextGridRows = Array.from(nextRows.values())
    setAllDailyRows(nextGridRows); setDirtyCellKeys(saveKeys)
    await saveGridChanges(nextGridRows, saveKeys)
  }
  const applyBulkToEmployees = (employees: EmployeeMonthlyAttendance[], dates: string[], label: string) => {
    if (saving) return
    if (!dates.length) { onMessage('No dates match selected bulk scope.', 'warning'); return }
    const nextRows = gridRowMap(allDailyRows)
    const touchedCells = new Set<string>()
    let applied = 0, skipped = 0
    employees.forEach((row) => {
      dates.filter(date => isDateInEmployeeCycle(row, date)).forEach((date) => {
        if (shouldSkipBulk(row, date)) { skipped += 1; return }
        upsertGridRow(nextRows, row.employeeId, date, bulkStatus)
        touchedCells.add(attendanceCellKey(row.employeeId, date))
        applied += 1
      })
    })
    if (!applied) { onMessage('Only protected days matched. Nothing changed.', 'warning'); return }
    setAllDailyRows(Array.from(nextRows.values()))
    setDirtyCellKeys((keys) => new Set([...keys, ...touchedCells]))
    onMessage(`Applied ${normalizeStatus(bulkStatus)} to ${label}. ${skipped ? `${skipped} protected skipped.` : ''}`, 'success')
  }
  const bulkApplyVisible = () => applyBulkToEmployees(filteredRows, bulkDates, `${filteredRows.length} employees / assigned policy dates`)
  const applyEmployeeRow = (row: EmployeeMonthlyAttendance) => applyBulkToEmployees([row], datesForEmployee(row), row.employeeName)
  const toggleCellSelectionMode = (mode: Exclude<CellSelectionMode, null>) => {
    setCellSelectionMode((current) => current === mode ? null : mode)
    setGridEdit(null)
  }
  const handleCellClick = (event: ReactMouseEvent<HTMLTableCellElement>, employeeId: number, date: string) => {
    const employee = monthlyRows.find(row => row.employeeId === employeeId)
    if (saving || !employee || !isDateInEmployeeCycle(employee, date)) return
    const selecting = cellSelectionMode === 'all' || cellSelectionMode === employeeId || event.ctrlKey || event.metaKey || event.shiftKey
    if (!selecting) {
      setGridEdit({ employeeId, date })
      return
    }
    const key = attendanceCellKey(employeeId, date)
    setSelectedCellKeys((current) => {
      const next = new Set(current)
      if (event.shiftKey && cellSelectionAnchor?.employeeId === employeeId) {
        const employeeDates = datesForEmployee(employee)
        const from = employeeDates.indexOf(cellSelectionAnchor.date)
        const to = employeeDates.indexOf(date)
        if (from >= 0 && to >= 0) employeeDates.slice(Math.min(from, to), Math.max(from, to) + 1).forEach((item) => next.add(attendanceCellKey(employeeId, item)))
      } else if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
    setCellSelectionAnchor({ employeeId, date })
    setGridEdit(null)
  }
  const applySelectedCells = () => {
    if (saving) return
    if (!selectedCellKeys.size) { onMessage('Select one or more attendance cells first.', 'warning'); return }
    const nextRows = gridRowMap(allDailyRows)
    const touchedCells = new Set<string>()
    selectedCellKeys.forEach((key) => {
      const { employeeId, date } = attendanceCellFromKey(key)
      const employee = monthlyRows.find(row => row.employeeId === employeeId)
      if (!employeeId || !employee || !isDateInEmployeeCycle(employee, date)) return
      upsertGridRow(nextRows, employeeId, date, bulkStatus)
      touchedCells.add(key)
    })
    if (!touchedCells.size) { onMessage('No valid attendance cells were selected.', 'warning'); return }
    setAllDailyRows(Array.from(nextRows.values()))
    setDirtyCellKeys((keys) => new Set([...keys, ...touchedCells]))
    setSelectedCellKeys(new Set())
    setCellSelectionAnchor(null)
    onMessage(`Applied ${normalizeStatus(bulkStatus)} to ${touchedCells.size} selected ${touchedCells.size === 1 ? 'cell' : 'cells'}.`, 'success')
  }

  return <div className="manual-attendance">
    <Card className="attendance-panel" title={<div><Typography.Title level={4}>Attendance Summary</Typography.Title><Typography.Text type="secondary">{reviewPatternText} / {cleanTime(settings.checkInTime)}-{cleanTime(settings.checkOutTime)}</Typography.Text></div>} extra={
        <div className="attendance-summary-controls">
          <label className="attendance-cycle-field attendance-cycle-field-compact">
            <span>Payroll month / cycle</span>
            <Input disabled={saving || loadingMonthly} type="month" value={month} onChange={(event) => setMonth(event.target.value)} />
            <small>{cycleRangeDisplay}</small>
          </label>
          <span className={issueRows.length ? 'attendance-status risk' : 'attendance-status'}>{issueRows.length ? `${issueRows.length} employees need review` : 'Ready for payroll'}</span>
        </div>
      }>
      <div className="attendance-summary">
        <span>Total employees<b>{summary.total}</b></span><span>Ready<b>{summary.ready}</b></span><span>Missing<b>{summary.missing}</b></span><span>Check values<b>{summary.check}</b></span><span>Payable Days<b>{summary.payableDays.toFixed(1)}</b></span><span>LOP<b>{summary.lopDays.toFixed(1)}</b></span>
      </div>
      <Space className="attendance-next-actions" wrap size={8}>
        <label className="attendance-action-field"><span>Attendance status</span><SearchSelect disabled={saving} value={bulkStatus} onChange={(value) => setBulkStatus(String(value))} options={statusChoices} /></label>
        <label className="attendance-action-field"><span>Date scope</span><SearchSelect disabled={saving} value={bulkScope} onChange={(value) => setBulkScope(value as BulkScope)} options={bulkScopeOptions} /></label>
        {bulkScope === 'date' && <Input disabled={saving} className="attendance-bulk-date" type="date" min={monthDays[0]} max={monthDays[monthDays.length - 1]} value={selectedBulkDate} onChange={(event) => setBulkDate(event.target.value)} />}
        <Button onClick={bulkApplyVisible} disabled={saving || !filteredRows.length}>Apply scope</Button>
        <Button disabled={saving} className={cellSelectionMode === 'all' ? 'cell-selection-action active' : 'cell-selection-action'} onClick={() => toggleCellSelectionMode('all')}>{cellSelectionMode === 'all' ? 'Selecting cells' : 'Select cells'}</Button>
        {selectedCellKeys.size > 0 && <Button disabled={saving} className="apply-selected-action" onClick={applySelectedCells}>Apply selected ({selectedCellKeys.size})</Button>}
        {selectedCellKeys.size > 0 && <Button disabled={saving} onClick={() => { setSelectedCellKeys(new Set()); setCellSelectionAnchor(null) }}>Clear selection</Button>}
        <Button type="primary" onClick={() => void saveGridChanges()} loading={saving} disabled={saving || !dirtyCellKeys.size}>Save {dirtyCellKeys.size ? `(${dirtyCellKeys.size} cells)` : ''}</Button>
      </Space>
      <div className="attendance-filterbar">
        <Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search employee" allowClear />
        <SearchSelect value={departmentFilter} onChange={setDepartmentFilter} options={[{ value: '', label: 'All departments' }, ...departments.map((department) => ({ value: department, label: department }))]} />
        <SearchSelect value={reviewFilter} onChange={(value) => setReviewFilter(value as ReviewStatus | '')} options={[{ value: '', label: 'All status' }, { value: 'Ready', label: 'Ready' }, { value: 'Missing attendance', label: 'Missing attendance' }, { value: 'Check values', label: 'Check values' }]} />
      </div>
      <div className="attendance-table-head">
        <div><strong>Attendance Calendar</strong><span>{filteredRows.length} employees shown / {cycleRangeDisplay}</span></div>
        <Space className="attendance-table-actions" size={8}>
          <Button icon={<DownloadOutlined />} onClick={exportAttendanceCsv} disabled={!filteredRows.length}>Export</Button>
          <label className={`attendance-file-action ant-btn ant-btn-default${saving ? ' ant-btn-disabled' : ''}`}><input disabled={saving} type="file" accept=".csv,text/csv" onChange={importAttendanceCsv} /><UploadOutlined /><span>Import</span></label>
        </Space>
      </div>
      <div className={cellSelectionMode !== null ? 'attendance-calendar-grid cell-selection-mode' : 'attendance-calendar-grid'}>
        <table>
          <thead><tr><th className="employee-col">Employee</th>{monthDays.map((date) => <th key={date}><button disabled={saving} type="button" className={bulkScope === 'date' && selectedBulkDate === date ? 'bulk-date-on' : ''} onClick={() => { setBulkScope('date'); setBulkDate(date) }}><b>{date.slice(8)}</b><em>{new Date(`${date}T00:00:00`).toLocaleDateString(undefined, { month: 'short' })}</em><span>{new Date(`${date}T00:00:00`).toLocaleDateString(undefined, { weekday: 'short' })}</span></button></th>)}</tr></thead>
          <tbody>{filteredRows.map((row) => <tr key={row.employeeId} className={`attendance-grid-${rowTone(row)}`}><th className="employee-col"><div className="employee-cell"><strong>{row.employeeName}</strong><small>{row.employeeCode || 'No code'} {row.department ? `- ${row.department}` : ''}{row.attendanceGroupName ? ` / ${row.attendanceGroupName}` : ''}</small><span className="employee-attendance-line"><em className={`attendance-dot ${rowTone(row)}`} /><i>{reviewStatus(row)}</i><i>Pay {toNumber(row.payableDays).toFixed(1)}</i><i>LOP {toNumber(row.lopDays).toFixed(1)}</i><i>Miss {missingCountFor(row)}</i><button disabled={saving} type="button" className="row-apply" onClick={() => applyEmployeeRow(row)}>Apply row</button><button disabled={saving} type="button" className={cellSelectionMode === row.employeeId ? 'row-apply row-select-cells active' : 'row-apply row-select-cells'} onClick={() => toggleCellSelectionMode(row.employeeId)}>{cellSelectionMode === row.employeeId ? 'Selecting' : 'Select cells'}</button></span></div></th>{monthDays.map((date) => {
            const cell = gridCell(row, date)
            const outsideCycle = !isDateInEmployeeCycle(row, date)
            const editing = gridEdit?.employeeId === row.employeeId && gridEdit.date === date
            const editStatus = cell.row?.status ?? cell.status
            const selected = selectedCellKeys.has(attendanceCellKey(row.employeeId, date))
            return <td key={date} className={`${cell.cls}${selected ? ' cell-selected' : ''}`} data-tip={selected ? `Selected - ${cell.title}` : cell.title} aria-selected={selected} aria-disabled={outsideCycle} onClick={(event) => handleCellClick(event, row.employeeId, date)}>{editing ? <div className="attendance-cell-editor" onClick={(event) => event.stopPropagation()}>
              <SearchSelect disabled={saving} value={editStatus} onChange={(value) => updateGridStatus(row.employeeId, date, value)} options={statusOptions} />
            </div> : <button type="button" className="attendance-cell-label" disabled={outsideCycle}><span>{cell.text}</span>{cell.hoursText && <small>{cell.hoursText}</small>}</button>}</td>
          })}</tr>)}{!filteredRows.length && <tr><td colSpan={monthDays.length + 1}>{monthlyRows.length ? 'No employees match this review.' : reviewContext.accessScope === 'Client' ? 'No active employees found in your assigned client.' : 'No employees report to you.'}</td></tr>}</tbody>
        </table>
      </div>
    </Card>
    <BulkUploadProgressModal
      open={saveProgress.open}
      title="Attendance save progress"
      state={saveProgress.state}
      percent={saveProgress.percent}
      summary={saveProgress.summary}
      stage={saveProgress.summary.stage}
      labels={{ total: 'Total cells', completed: 'Processed', saved: 'Saved' }}
      successMessage="Attendance saved successfully."
      successDescription={saveProgress.summary.stage === 'Saved - refresh required' ? 'Attendance was saved. Refresh the review before making more changes.' : 'Attendance changes were saved and the review was refreshed.'}
      errorMessage="Attendance save could not be completed or confirmed."
      onClose={() => setSaveProgress((current) => ({ ...current, open: false }))}
    />
  </div>
}
