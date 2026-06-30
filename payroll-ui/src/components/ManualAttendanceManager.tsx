import { useEffect, useMemo, useState, type ChangeEvent, type ReactNode } from 'react'
import { DownloadOutlined, UploadOutlined } from '@ant-design/icons'
import { Button, Card, Input, Space, Typography } from 'antd'
import type { AttendanceReviewContext, EmployeeDailyAttendance, EmployeeMonthlyAttendance, LeaveType } from '../types/payroll'
import { getAttendanceReviewContext, getDailyAttendanceGrid, getLeaveTypes, getMonthlyAttendance, saveDailyAttendanceBatch } from '../services/leaveAttendanceService'
import SearchSelect from './SearchSelect'
import type { ToastType } from './ToastProvider'

type Props = { clientId: number; onMessage: (message: string, type?: ToastType) => void; clientControl?: ReactNode }
type DailyStatus = string
type ReviewStatus = 'Ready' | 'Missing attendance' | 'Check values'
type GridEdit = { employeeId: number; date: string } | null
type BulkScope = 'all' | 'weekends' | 'saturday' | 'sunday' | 'date'
type RowPatch = Partial<Pick<EmployeeDailyAttendance, 'checkInTime' | 'checkOutTime' | 'payableValue'>>

const fallbackContext: AttendanceReviewContext = {
  settings: { id: 0, clientId: 0, checkInTime: '09:00:00', checkOutTime: '18:00:00', workingHoursCalculation: 'First check-in and last check-out', minimumHoursForHalfDay: 4, minimumHoursForFullDay: 8, maximumHoursAllowedForFullDay: 12, allowRegularizationRequests: true, regularizationWindow: 'Anytime', pastDaysAllowed: 7, restrictRegularizationRequestsPerMonth: false, maxRegularizationRequestsPerMonth: 3 },
  schedule: { workWeek: 'Monday - Friday', salaryDays: 'Actual days', fixedDays: '30', payDay: 'Last working day', firstPayPeriod: '' },
  holidays: [],
  leaveBalances: []
}

const bulkScopeOptions = [
  { value: 'all', label: 'All visible dates' },
  { value: 'weekends', label: 'Sat + Sun' },
  { value: 'saturday', label: 'Saturdays' },
  { value: 'sunday', label: 'Sundays' },
  { value: 'date', label: 'Selected date' }
]

const currentMonth = () => new Date().toISOString().slice(0, 7)
const toNumber = (value: number | string | undefined | null) => Number.isFinite(Number(value)) ? Number(value) : 0
const isoDate = (value: string) => value.slice(0, 10)
const unique = (items: string[]) => Array.from(new Set(items.map(item => item.trim()).filter(Boolean))).sort((a, b) => a.localeCompare(b))
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

const monthDates = (month: string) => {
  const [year, monthNumber] = month.split('-').map(Number)
  return Array.from({ length: new Date(year, monthNumber, 0).getDate() }, (_, index) => `${month}-${String(index + 1).padStart(2, '0')}`)
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
const workDaysFor = (workWeek: string) => {
  const text = workWeek.toLowerCase()
  if (text.includes('all')) return new Set([0, 1, 2, 3, 4, 5, 6])
  if (text.includes('saturday')) return new Set([1, 2, 3, 4, 5, 6])
  return new Set([1, 2, 3, 4, 5])
}

const reviewStatus = (row: EmployeeMonthlyAttendance): ReviewStatus => {
  const workingDays = toNumber(row.workingDays), presentDays = toNumber(row.presentDays), payableDays = toNumber(row.payableDays), lopDays = toNumber(row.lopDays)
  if (workingDays <= 0 && presentDays <= 0 && payableDays <= 0) return 'Missing attendance'
  if (payableDays < 0 || presentDays < 0 || lopDays < 0 || payableDays > workingDays || presentDays > workingDays) return 'Check values'
  if (Math.abs((payableDays + lopDays) - workingDays) > 0.01) return 'Check values'
  return 'Ready'
}

export default function ManualAttendanceManager({ clientId, onMessage, clientControl }: Props) {
  const [month, setMonth] = useState(currentMonth())
  const [monthlyRows, setMonthlyRows] = useState<EmployeeMonthlyAttendance[]>([])
  const [allDailyRows, setAllDailyRows] = useState<EmployeeDailyAttendance[]>([])
  const [leaveTypes, setLeaveTypes] = useState<LeaveType[]>([])
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

  const settings = reviewContext.settings
  const workDays = useMemo(() => workDaysFor(reviewContext.schedule.workWeek), [reviewContext.schedule.workWeek])
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
  const monthDays = useMemo(() => monthDates(month), [month])
  const selectedBulkDate = bulkDate.startsWith(month) ? bulkDate : monthDays[0]
  const bulkDates = useMemo(() => monthDays.filter((date) => {
    const day = new Date(`${date}T00:00:00`).getDay()
    if (bulkScope === 'weekends') return day === 0 || day === 6
    if (bulkScope === 'saturday') return day === 6
    if (bulkScope === 'sunday') return day === 0
    if (bulkScope === 'date') return date === selectedBulkDate
    return true
  }), [monthDays, bulkScope, selectedBulkDate])
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
  const isWorkingDate = (date: string) => workDays.has(new Date(`${date}T00:00:00`).getDay())
  const holidayFor = (row: EmployeeMonthlyAttendance, date: string) => reviewContext.holidays.find((holiday) =>
    isoDate(holiday.startDate) <= date && isoDate(holiday.endDate) >= date && (holiday.allLocations || !holiday.workLocationIds.length || holiday.workLocationIds.includes(row.workLocationId)))
  const defaultStatusFor = (row: EmployeeMonthlyAttendance, date: string) => holidayFor(row, date) ? 'H' : isWorkingDate(date) ? '' : 'WO'
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
      const [rows, leaveTypeRows, dailyGridRows, context] = await Promise.all([getMonthlyAttendance(clientId, month), getLeaveTypes(clientId), getDailyAttendanceGrid(clientId, month), getAttendanceReviewContext(clientId, month)])
      setMonthlyRows(rows); setLeaveTypes(leaveTypeRows); setAllDailyRows(dailyGridRows); setReviewContext(context); setDirtyEmployeeIds(new Set()); setGridEdit(null)
    } catch (error) {
      onMessage(error instanceof Error ? error.message : 'Unable to load monthly attendance', 'error')
    } finally {
      setLoadingMonthly(false)
    }
  }

  useEffect(() => { void loadMonthly() }, [clientId, month])

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
  const bulkApplyVisible = () => applyBulkToEmployees(filteredRows, bulkDates, `${filteredRows.length} employees / ${bulkDates.length} dates`)
  const applyEmployeeRow = (row: EmployeeMonthlyAttendance) => applyBulkToEmployees([row], monthDays, row.employeeName)

  return <div className="manual-attendance">
    <div className="attendance-toolbar attendance-simple-toolbar">
      {clientControl}
      <label>Payroll month<Input type="month" value={month} onChange={(event) => setMonth(event.target.value)} /></label>
      <Button onClick={loadMonthly} loading={loadingMonthly}>Refresh review</Button>
    </div>
    <Card className="attendance-panel" title={<div><Typography.Title level={4}>Attendance Summary</Typography.Title><Typography.Text type="secondary">{reviewContext.schedule.workWeek} / {cleanTime(settings.checkInTime)}-{cleanTime(settings.checkOutTime)}</Typography.Text></div>} extra={
        <span className={issueRows.length ? 'attendance-status risk' : 'attendance-status'}>{issueRows.length ? `${issueRows.length} employees need review` : 'Ready for payroll'}</span>
      }>
      <div className="attendance-summary">
        <span>Total employees<b>{summary.total}</b></span><span>Ready<b>{summary.ready}</b></span><span>Missing<b>{summary.missing}</b></span><span>Check values<b>{summary.check}</b></span><span>Payable Days<b>{summary.payableDays.toFixed(1)}</b></span><span>LOP<b>{summary.lopDays.toFixed(1)}</b></span>
      </div>
      <Space className="attendance-next-actions" wrap size={8}>
        <SearchSelect value={bulkStatus} onChange={(value) => setBulkStatus(String(value))} options={statusChoices} />
        <SearchSelect value={bulkScope} onChange={(value) => setBulkScope(value as BulkScope)} options={bulkScopeOptions} />
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
        <div><strong>Attendance Calendar</strong><span>{filteredRows.length} employees shown</span></div>
        <Space className="attendance-table-actions" size={8}>
          <Button icon={<DownloadOutlined />} onClick={exportAttendanceCsv} disabled={!filteredRows.length}>Export</Button>
          <label className="attendance-file-action ant-btn ant-btn-default"><input type="file" accept=".csv,text/csv" onChange={importAttendanceCsv} /><UploadOutlined /><span>Import</span></label>
        </Space>
      </div>
      <div className="attendance-calendar-grid">
        <table>
          <thead><tr><th className="employee-col">Employee</th>{monthDays.map((date) => <th key={date}><button type="button" className={bulkScope === 'date' && selectedBulkDate === date ? 'bulk-date-on' : ''} onClick={() => { setBulkScope('date'); setBulkDate(date) }}><b>{date.slice(8)}</b><span>{new Date(`${date}T00:00:00`).toLocaleDateString(undefined, { weekday: 'short' })}</span></button></th>)}</tr></thead>
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
