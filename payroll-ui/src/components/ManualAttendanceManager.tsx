import { useEffect, useMemo, useState, type ChangeEvent, type ReactNode } from 'react'
import { DownloadOutlined, UploadOutlined } from '@ant-design/icons'
import { Button, Card, Input, Space, Typography } from 'antd'
import type { AttendanceGroup, AttendanceReviewContext, Drop, EmployeeDailyAttendance, EmployeeMonthlyAttendance, LeaveAttendancePreferences, LeaveType } from '../types/payroll'
import { getAttendanceReviewContext, getDailyAttendanceGrid, getLeaveTypes, getMonthlyAttendance, saveDailyAttendanceBatch } from '../services/leaveAttendanceService'
import { getDropdowns } from '../services/settingsService'
import SearchSelect from './SearchSelect'
import type { ToastType } from './ToastProvider'

type Props = { clientId: number; group?: AttendanceGroup | null; reviewMonth?: string; onMessage: (message: string, type?: ToastType) => void; clientControl?: ReactNode }
type DailyStatus = string
type ReviewStatus = 'Ready' | 'Missing attendance' | 'Check values'
type GridEdit = { employeeId: number; date: string } | null
type BulkScope = 'all' | 'cycle' | 'weekends' | 'saturday' | 'sunday' | 'date'
type RowPatch = Partial<Pick<EmployeeDailyAttendance, 'checkInTime' | 'checkOutTime' | 'payableValue'>>
type WorkWeekConfig = { workingDays: number[]; offSaturdays: number[] }

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
const toNumber = (value: number | string | undefined | null) => Number.isFinite(Number(value)) ? Number(value) : 0
const isoDate = (value: string) => value.slice(0, 10)
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
const isDateInCycle = (date: string, preferences: LeaveAttendancePreferences) => {
  const day = Number(date.slice(8, 10))
  const start = preferences.attendanceCycleStartDay || 1
  const end = preferences.attendanceCycleEndDay || 31
  return start <= end ? day >= start && day <= end : day >= start || day <= end
}

const reviewStatus = (row: EmployeeMonthlyAttendance): ReviewStatus => {
  const workingDays = toNumber(row.workingDays), presentDays = toNumber(row.presentDays), payableDays = toNumber(row.payableDays), lopDays = toNumber(row.lopDays)
  if (workingDays <= 0 && presentDays <= 0 && payableDays <= 0) return 'Missing attendance'
  if (payableDays < 0 || presentDays < 0 || lopDays < 0 || payableDays > workingDays || presentDays > workingDays) return 'Check values'
  if (Math.abs((payableDays + lopDays) - workingDays) > 0.01) return 'Check values'
  return 'Ready'
}

export default function ManualAttendanceManager({ clientId, group = null, reviewMonth = '', onMessage, clientControl }: Props) {
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
  const [dirtyEmployeeIds, setDirtyEmployeeIds] = useState<Set<number>>(new Set())
  const [loadingMonthly, setLoadingMonthly] = useState(false)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (reviewMonth && /^\d{4}-\d{2}$/.test(reviewMonth)) setMonth(reviewMonth)
  }, [reviewMonth])

  const settings = reviewContext.settings
  const preferences = reviewContext.preferences
  const groupEmployeeKey = useMemo(() => (group?.employeeIds ?? []).join(','), [group?.employeeIds])
  const cycleSettings = group
    ? { workLocationId: group.workLocationId, workWeek: group.workWeek, attendanceCycleStartDay: group.attendanceCycleStartDay, attendanceCycleEndDay: group.attendanceCycleEndDay, payrollReportGenerationDay: group.payrollReportGenerationDay }
    : preferences
  const activeLeaveTypes = useMemo(() => leaveTypes.filter((leaveType) => leaveType.isActive), [leaveTypes])
  const leaveTypeByCode = useMemo(() => new Map(activeLeaveTypes.map((leaveType) => [leaveType.code.toLowerCase(), leaveType])), [activeLeaveTypes])
  const statusChoices = useMemo(() => [
    { value: 'Present', label: 'P - Present' },
    { value: 'A', label: 'A - Absent' },
    { value: 'WO', label: 'WO - Weekly Off' },
    { value: 'H', label: 'H - Holiday' },
    ...activeLeaveTypes.map((leaveType) => ({ value: leaveType.code, label: `${leaveType.code} - ${leaveType.name}` }))
  ], [activeLeaveTypes])
  const statusOptions = useMemo(() => [{ value: '', label: 'Select status' }, ...statusChoices], [statusChoices])
  const cycleRange = useMemo(() => cycleRangeFor(month, cycleSettings.attendanceCycleStartDay, cycleSettings.attendanceCycleEndDay), [month, cycleSettings.attendanceCycleStartDay, cycleSettings.attendanceCycleEndDay])
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
  const guardedBulkDates = bulkDates
  const guardedRowDates = monthDays
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
    if (group) return `${group.name} / ${group.workWeek || 'No off pattern selected'}`
    const groupPatterns = unique(monthlyRows.map(row => row.workWeek))
    if (groupPatterns.length > 1) return `${groupPatterns.length} group off patterns`
    return reviewContext.schedule.workWeek || groupPatterns[0] || 'No off pattern selected'
  }, [group, monthlyRows, reviewContext.schedule.workWeek])

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
  const workWeekFor = (row: EmployeeMonthlyAttendance) => group ? group.workWeek || row.workWeek : cycleSettings.workWeek || reviewContext.schedule.workWeek
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
  const missingCountFor = (employee: EmployeeMonthlyAttendance) => monthDays.filter((date) => !dailyByEmployee.get(employee.employeeId)?.has(date) && !defaultStatusFor(employee, date)).length
  const rowTone = (row: EmployeeMonthlyAttendance) => reviewStatus(row) === 'Ready' ? 'ready' : reviewStatus(row) === 'Missing attendance' ? 'warn' : 'danger'
  const shouldSkipBulk = (employee: EmployeeMonthlyAttendance, date: string) => {
    const defaultStatus = defaultStatusFor(employee, date)
    const current = defaultStatus || normalizeStatus(dailyByEmployee.get(employee.employeeId)?.get(date)?.status)
    return current === 'WO' || current === 'H' || leaveTypeByCode.has(current.toLowerCase())
  }

  const loadMonthly = async () => {
    setLoadingMonthly(true)
    try {
      const scopeLocationId = group?.workLocationId || 0
      const context = await getAttendanceReviewContext(clientId, month, scopeLocationId)
      const nextCycle = group
        ? { attendanceCycleStartDay: group.attendanceCycleStartDay, attendanceCycleEndDay: group.attendanceCycleEndDay }
        : context.preferences
      const nextRange = cycleRangeFor(month, nextCycle.attendanceCycleStartDay, nextCycle.attendanceCycleEndDay)
      const dailyMonths = monthsInRange(nextRange.start, nextRange.end)
      const [rows, leaveTypeRows, dailyGridRows, dropdownRows] = await Promise.all([getMonthlyAttendance(clientId, month, scopeLocationId), getLeaveTypes(clientId), Promise.all(dailyMonths.map(item => getDailyAttendanceGrid(clientId, item, scopeLocationId))), getDropdowns()])
      const groupEmployeeIds = new Set(group?.employeeIds ?? [])
      const scopedRows = group ? rows.filter(row => groupEmployeeIds.has(row.employeeId)) : rows
      const scopedDailyRows = dailyGridRows.flat().filter(row => !group || groupEmployeeIds.has(row.employeeId))
      setMonthlyRows(scopedRows); setLeaveTypes(leaveTypeRows); setAllDailyRows(scopedDailyRows); setReviewContext(context); setDropdowns(dropdownRows); setDirtyEmployeeIds(new Set()); setGridEdit(null)
    } catch (error) {
      onMessage(error instanceof Error ? error.message : 'Unable to load monthly attendance', 'error')
    } finally {
      setLoadingMonthly(false)
    }
  }

  useEffect(() => { void loadMonthly() }, [clientId, group?.id, group?.workLocationId, groupEmployeeKey, month])

  const upsertGridRow = (rows: EmployeeDailyAttendance[], employeeId: number, date: string, status: DailyStatus, patch: RowPatch = {}) => {
    let found = false
    const next = rows.map((row) => {
      if (row.employeeId !== employeeId || isoDate(row.attendanceDate) !== date) return row
      found = true
      return makeRow(employeeId, date, status, row, patch)
    })
    if (!found) next.push(makeRow(employeeId, date, status, undefined, patch))
    return next
  }
  const updateGridStatus = (employeeId: number, date: string, status: DailyStatus) => {
    if (!normalizeStatus(status)) setAllDailyRows((rows) => rows.filter((row) => row.employeeId !== employeeId || isoDate(row.attendanceDate) !== date))
    else setAllDailyRows((rows) => upsertGridRow(rows, employeeId, date, status))
    setDirtyEmployeeIds((ids) => new Set(ids).add(employeeId))
    setGridEdit(null)
  }
  const employeeRowsForSave = (employeeId: number, rows: EmployeeDailyAttendance[]) => {
    const employee = monthlyRows.find((item) => item.employeeId === employeeId)
    if (!employee) return null
    const byDate = new Map(rows.filter((row) => row.employeeId === employeeId).map((row) => [isoDate(row.attendanceDate), row]))
    const prepared: EmployeeDailyAttendance[] = []
    for (const date of monthDays) {
      const row = byDate.get(date)
      if (row && normalizeStatus(row.status)) prepared.push(makeRow(employeeId, date, row.status, row, { checkInTime: row.checkInTime, checkOutTime: row.checkOutTime, payableValue: row.payableValue }))
      else {
        const status = defaultStatusFor(employee, date)
        if (!status) return null
        prepared.push(makeRow(employeeId, date, status))
      }
    }
    return prepared
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
  const saveGridChanges = async (sourceRows = allDailyRows, ids = dirtyEmployeeIds) => {
    const employeeIds = Array.from(ids)
    if (!employeeIds.length) return
    const prepared = new Map<number, EmployeeDailyAttendance[]>()
    for (const employeeId of employeeIds) {
      const rows = employeeRowsForSave(employeeId, sourceRows)
      if (!rows) { onMessage('Fill complete working days before saving.', 'warning'); return }
      prepared.set(employeeId, rows)
    }
    const balanceError = validateLeaveBalances(prepared)
    if (balanceError) { onMessage(balanceError, 'error'); return }
    setSaving(true)
    try {
      const response = await saveDailyAttendanceBatch(clientId, month, Array.from(prepared.values()).flat())
      if (!response.ok) { onMessage(response.error || 'Unable to save attendance.', 'error'); return }
      onMessage(`${employeeIds.length} employee attendance saved.`, 'success')
      await loadMonthly()
    } finally {
      setSaving(false)
    }
  }

  const exportAttendanceCsv = () => {
    const rows = [
      ['EmployeeCode', 'EmployeeName', 'EmployeeId', 'Department', ...monthDays],
      ...filteredRows.map((row) => [row.employeeCode, row.employeeName, row.employeeId, row.department, ...monthDays.map((date) => {
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
    const file = event.target.files?.[0]
    event.target.value = ''
    if (!file) return
    const rows = parseCsv(await file.text()), header = rows[0]?.map((value) => value.trim()) ?? []
    const dateIndexes = monthDays.map((date) => header.findIndex((column) => column === date))
    if (dateIndexes.some((index) => index < 0)) { onMessage('Selected file does not match current month template.', 'error'); return }
    const employeeIdIndex = header.findIndex((column) => column === 'EmployeeId'), employeeCodeIndex = header.findIndex((column) => column === 'EmployeeCode')
    const byId = new Map(monthlyRows.map((row) => [String(row.employeeId), row.employeeId])), byCode = new Map(monthlyRows.map((row) => [row.employeeCode.toLowerCase(), row.employeeId]))
    let nextRows = allDailyRows
    const imported = new Set<number>()
    for (const row of rows.slice(1)) {
      const employeeId = byId.get(row[employeeIdIndex]?.trim()) ?? byCode.get(row[employeeCodeIndex]?.trim().toLowerCase())
      if (!employeeId) continue
      monthDays.forEach((date, index) => {
        const parsed = parseAttendanceCell(row[dateIndexes[index]] ?? '')
        if (!parsed) return
        nextRows = upsertGridRow(nextRows, employeeId, date, parsed.status, parsed.patch)
        imported.add(employeeId)
      })
    }
    if (!imported.size) { onMessage('No attendance rows imported.', 'warning'); return }
    const saveIds = new Set([...dirtyEmployeeIds, ...imported])
    setAllDailyRows(nextRows); setDirtyEmployeeIds(saveIds)
    await saveGridChanges(nextRows, saveIds)
  }
  const applyBulkToEmployees = (employees: EmployeeMonthlyAttendance[], dates: string[], label: string) => {
    if (!dates.length) { onMessage('No dates match selected bulk scope.', 'warning'); return }
    let nextRows = allDailyRows
    const touched = new Set<number>()
    let applied = 0, skipped = 0
    employees.forEach((row) => {
      let rowApplied = false
      dates.forEach((date) => {
        if (shouldSkipBulk(row, date)) { skipped += 1; return }
        nextRows = upsertGridRow(nextRows, row.employeeId, date, bulkStatus)
        applied += 1
        rowApplied = true
      })
      if (rowApplied) touched.add(row.employeeId)
    })
    if (!applied) { onMessage('Only protected days matched. Nothing changed.', 'warning'); return }
    setAllDailyRows(nextRows)
    setDirtyEmployeeIds((ids) => new Set([...ids, ...touched]))
    onMessage(`Applied ${normalizeStatus(bulkStatus)} to ${label}. ${skipped ? `${skipped} protected skipped.` : ''}`, 'success')
  }
  const bulkApplyVisible = () => applyBulkToEmployees(filteredRows, guardedBulkDates, `${filteredRows.length} employees / ${guardedBulkDates.length} dates`)
  const applyEmployeeRow = (row: EmployeeMonthlyAttendance) => applyBulkToEmployees([row], guardedRowDates, row.employeeName)

  return <div className="manual-attendance">
    <div className="attendance-toolbar attendance-simple-toolbar">
      {clientControl}
      <label className="attendance-cycle-field">Payroll month / cycle<Input type="month" value={month} onChange={(event) => setMonth(event.target.value)} /><small>{cycleRangeDisplay}</small></label>
      <Button onClick={loadMonthly} loading={loadingMonthly}>Refresh review</Button>
    </div>
    <Card className="attendance-panel" title={<div><Typography.Title level={4}>Attendance Summary</Typography.Title><Typography.Text type="secondary">{reviewPatternText} / {cleanTime(settings.checkInTime)}-{cleanTime(settings.checkOutTime)}</Typography.Text></div>} extra={
        <span className={issueRows.length ? 'attendance-status risk' : 'attendance-status'}>{issueRows.length ? `${issueRows.length} employees need review` : 'Ready for payroll'}</span>
      }>
      <div className="attendance-summary">
        <span>Total employees<b>{summary.total}</b></span><span>Ready<b>{summary.ready}</b></span><span>Missing<b>{summary.missing}</b></span><span>Check values<b>{summary.check}</b></span><span>Payable Days<b>{summary.payableDays.toFixed(1)}</b></span><span>LOP<b>{summary.lopDays.toFixed(1)}</b></span>
      </div>
      <Space className="attendance-next-actions" wrap size={8}>
        <label className="attendance-action-field"><span>Attendance status</span><SearchSelect value={bulkStatus} onChange={(value) => setBulkStatus(String(value))} options={statusChoices} /></label>
        <label className="attendance-action-field"><span>Date scope</span><SearchSelect value={bulkScope} onChange={(value) => setBulkScope(value as BulkScope)} options={bulkScopeOptions} /></label>
        {bulkScope === 'date' && <Input className="attendance-bulk-date" type="date" min={monthDays[0]} max={monthDays[monthDays.length - 1]} value={selectedBulkDate} onChange={(event) => setBulkDate(event.target.value)} />}
        <Button onClick={bulkApplyVisible} disabled={!filteredRows.length}>Apply scope</Button>
        <Button type="primary" onClick={() => void saveGridChanges()} loading={saving} disabled={!dirtyEmployeeIds.size}>Save {dirtyEmployeeIds.size ? `(${dirtyEmployeeIds.size})` : ''}</Button>
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
          <label className="attendance-file-action ant-btn ant-btn-default"><input type="file" accept=".csv,text/csv" onChange={importAttendanceCsv} /><UploadOutlined /><span>Import</span></label>
        </Space>
      </div>
      <div className="attendance-calendar-grid">
        <table>
          <thead><tr><th className="employee-col">Employee</th>{monthDays.map((date) => <th key={date}><button type="button" className={bulkScope === 'date' && selectedBulkDate === date ? 'bulk-date-on' : ''} onClick={() => { setBulkScope('date'); setBulkDate(date) }}><b>{date.slice(8)}</b><em>{new Date(`${date}T00:00:00`).toLocaleDateString(undefined, { month: 'short' })}</em><span>{new Date(`${date}T00:00:00`).toLocaleDateString(undefined, { weekday: 'short' })}</span></button></th>)}</tr></thead>
          <tbody>{filteredRows.map((row) => <tr key={row.employeeId} className={`attendance-grid-${rowTone(row)}`}><th className="employee-col"><div className="employee-cell"><strong>{row.employeeName}</strong><small>{row.employeeCode || 'No code'} {row.department ? `- ${row.department}` : ''}</small><span className="employee-attendance-line"><em className={`attendance-dot ${rowTone(row)}`} /><i>{reviewStatus(row)}</i><i>Pay {toNumber(row.payableDays).toFixed(1)}</i><i>LOP {toNumber(row.lopDays).toFixed(1)}</i><i>Miss {missingCountFor(row)}</i><button type="button" className="row-apply" onClick={() => applyEmployeeRow(row)}>Apply row</button></span></div></th>{monthDays.map((date) => {
            const cell = gridCell(row, date)
            const editing = gridEdit?.employeeId === row.employeeId && gridEdit.date === date
            const editStatus = cell.row?.status ?? cell.status
            return <td key={date} className={cell.cls} data-tip={cell.title} onClick={() => setGridEdit({ employeeId: row.employeeId, date })}>{editing ? <div className="attendance-cell-editor" onClick={(event) => event.stopPropagation()}>
              <SearchSelect value={editStatus} onChange={(value) => updateGridStatus(row.employeeId, date, value)} options={statusOptions} />
            </div> : <button type="button" className="attendance-cell-label"><span>{cell.text}</span>{cell.hoursText && <small>{cell.hoursText}</small>}</button>}</td>
          })}</tr>)}{!filteredRows.length && <tr><td colSpan={monthDays.length + 1}>No employees match this review.</td></tr>}</tbody>
        </table>
      </div>
    </Card>
  </div>
}
