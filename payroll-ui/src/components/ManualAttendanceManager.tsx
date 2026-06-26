import { useEffect, useMemo, useState } from 'react'
import type { EmployeeDailyAttendance, EmployeeMonthlyAttendance, LeaveType } from '../types/payroll'
import { getDailyAttendance, getLeaveTypes, getMonthlyAttendance, saveDailyAttendance, saveMonthlyAttendance } from '../services/leaveAttendanceService'
import PageTabs from './PageTabs'

type Props = { clientId: number; onMessage: (message: string) => void }
type DailyStatus = string
type DailyUiRow = EmployeeDailyAttendance & { isMissing?: boolean }
type ReviewStatus = 'Ready' | 'Missing attendance' | 'Check values'
type AttendanceTab = 'summary' | 'employees' | 'daily'

const attendanceTabs = ['summary', 'employees', 'daily'] as const

const currentMonth = () => new Date().toISOString().slice(0, 7)
const toNumber = (value: number | string) => Number.isFinite(Number(value)) ? Number(value) : 0
const isoDate = (value: string) => value.slice(0, 10)

const monthDates = (month: string) => {
  const [year, monthNumber] = month.split('-').map(Number)
  const days = new Date(year, monthNumber, 0).getDate()
  return Array.from({ length: days }, (_, index) => `${month}-${String(index + 1).padStart(2, '0')}`)
}

const reviewStatus = (row: EmployeeMonthlyAttendance): ReviewStatus => {
  const workingDays = toNumber(row.workingDays)
  const presentDays = toNumber(row.presentDays)
  const payableDays = toNumber(row.payableDays)
  const lopDays = toNumber(row.lopDays)
  if (workingDays <= 0 && presentDays <= 0 && payableDays <= 0) return 'Missing attendance'
  if (payableDays < 0 || presentDays < 0 || lopDays < 0 || payableDays > workingDays || presentDays > workingDays) return 'Check values'
  if (Math.abs((presentDays + lopDays) - workingDays) > 0.01) return 'Check values'
  return 'Ready'
}

const badgeClass = (status: ReviewStatus) => status === 'Ready' ? 'attendance-status' : 'attendance-status risk'

export default function ManualAttendanceManager({ clientId, onMessage }: Props) {
  const [month, setMonth] = useState(currentMonth())
  const [activeTab, setActiveTab] = useState<AttendanceTab>('summary')
  const [monthlyRows, setMonthlyRows] = useState<EmployeeMonthlyAttendance[]>([])
  const [selectedEmployeeId, setSelectedEmployeeId] = useState<number>(0)
  const [dailyRows, setDailyRows] = useState<DailyUiRow[]>([])
  const [leaveTypes, setLeaveTypes] = useState<LeaveType[]>([])
  const [loadingMonthly, setLoadingMonthly] = useState(false)
  const [loadingDaily, setLoadingDaily] = useState(false)
  const [saving, setSaving] = useState(false)

  const selectedEmployee = useMemo(
    () => monthlyRows.find((row) => row.employeeId === selectedEmployeeId) ?? null,
    [monthlyRows, selectedEmployeeId]
  )

  const summary = useMemo(() => {
    const base = {
      total: monthlyRows.length,
      ready: 0,
      missing: 0,
      check: 0,
      workingDays: 0,
      presentDays: 0,
      payableDays: 0,
      lopDays: 0
    }
    return monthlyRows.reduce((acc, row) => {
      const status = reviewStatus(row)
      if (status === 'Ready') acc.ready += 1
      if (status === 'Missing attendance') acc.missing += 1
      if (status === 'Check values') acc.check += 1
      acc.workingDays += toNumber(row.workingDays)
      acc.presentDays += toNumber(row.presentDays)
      acc.payableDays += toNumber(row.payableDays)
      acc.lopDays += toNumber(row.lopDays)
      return acc
    }, base)
  }, [monthlyRows])

  const missingDailyCount = useMemo(() => dailyRows.filter((row) => row.isMissing).length, [dailyRows])
  const issueRows = useMemo(() => monthlyRows.filter((row) => reviewStatus(row) !== 'Ready'), [monthlyRows])
  const activeLeaveTypes = useMemo(() => leaveTypes.filter((leaveType) => leaveType.isActive), [leaveTypes])
  const payableForStatus = (status: DailyStatus) => status === 'Present' ? 1 : activeLeaveTypes.find((leaveType) => leaveType.code === status)?.type === 'Paid' ? 1 : 0

  const buildDailyMonth = (employeeId: number, rows: EmployeeDailyAttendance[], availableLeaveTypes = activeLeaveTypes) => {
    const availableCodes = new Set(availableLeaveTypes.filter((leaveType) => leaveType.isActive).map((leaveType) => leaveType.code))
    const byDate = new Map(rows.map((row) => [isoDate(row.attendanceDate), row]))
    return monthDates(month).map((attendanceDate) => {
      const existing = byDate.get(attendanceDate)
      if (existing) {
        const status = existing.status === 'Present' || availableCodes.has(existing.status) ? existing.status : 'Present'
        const leaveType = availableLeaveTypes.find((item) => item.code === status)
        return { ...existing, attendanceDate: isoDate(existing.attendanceDate), status, payableValue: status === 'Present' || leaveType?.type === 'Paid' ? 1 : 0, isMissing: false }
      }
      const status: DailyStatus = 'Present'
      return {
        id: 0,
        clientId,
        employeeId,
        attendanceDate,
        status,
        payableValue: payableForStatus(status),
        remarks: '',
        isMissing: true
      }
    })
  }

  const loadMonthly = async () => {
    setLoadingMonthly(true)
    try {
      const rows = await getMonthlyAttendance(clientId, month)
      setMonthlyRows(rows)
      const preferred = rows.find((row) => reviewStatus(row) !== 'Ready') ?? rows[0]
      setSelectedEmployeeId((current) => rows.some((row) => row.employeeId === current) ? current : preferred?.employeeId ?? 0)
    } catch (error) {
      onMessage(error instanceof Error ? error.message : 'Unable to load monthly attendance')
    } finally {
      setLoadingMonthly(false)
    }
  }

  const loadDaily = async (employeeId: number) => {
    if (!employeeId) {
      setDailyRows([])
      return
    }
    setLoadingDaily(true)
    try {
      const [rows, leaveTypeRows] = await Promise.all([getDailyAttendance(clientId, employeeId, month), getLeaveTypes(clientId)])
      const activeTypes = leaveTypeRows.filter((leaveType) => leaveType.isActive)
      setLeaveTypes(leaveTypeRows)
      setDailyRows(buildDailyMonth(employeeId, rows, activeTypes))
    } catch (error) {
      onMessage(error instanceof Error ? error.message : 'Unable to load daily attendance')
    } finally {
      setLoadingDaily(false)
    }
  }

  useEffect(() => {
    loadMonthly()
  }, [clientId, month])

  useEffect(() => {
    loadDaily(selectedEmployeeId)
  }, [selectedEmployeeId, month])

  const updateMonthly = (employeeId: number, field: keyof EmployeeMonthlyAttendance, value: string) => {
    setMonthlyRows((rows) => rows.map((row) => row.employeeId === employeeId ? { ...row, [field]: value } : row))
  }

  const updateDaily = (date: string, patch: Partial<DailyUiRow>) => {
    setDailyRows((rows) => rows.map((row) => {
      if (row.attendanceDate !== date) return row
      const next = { ...row, ...patch, isMissing: false }
      if (patch.status) next.payableValue = payableForStatus(patch.status)
      return next
    }))
  }

  const fillMissing = (status: DailyStatus) => {
    setDailyRows((rows) => rows.map((row) => row.isMissing
      ? { ...row, status, payableValue: payableForStatus(status), remarks: row.remarks || 'Filled during payroll attendance review', isMissing: false }
      : row
    ))
  }

  const openDailyReview = (employeeId: number) => {
    setSelectedEmployeeId(employeeId)
    setActiveTab('daily')
  }

  const saveMonthly = async () => {
    setSaving(true)
    try {
      const response = await saveMonthlyAttendance(clientId, month, monthlyRows)
      if (!response.ok) {
        onMessage(response.error || 'Unable to save monthly attendance')
        return
      }
      setMonthlyRows(response.data)
      onMessage('Monthly attendance saved.')
    } finally {
      setSaving(false)
    }
  }

  const saveDaily = async () => {
    if (!selectedEmployeeId) return
    if (missingDailyCount > 0) {
      onMessage('Resolve missing dates before saving daily attendance.')
      return
    }
    setSaving(true)
    try {
      const payload = dailyRows.map(({ isMissing, ...row }) => row)
      const response = await saveDailyAttendance(clientId, selectedEmployeeId, month, payload)
      if (!response.ok) {
        onMessage(response.error || 'Unable to save daily attendance')
        return
      }
      setDailyRows(buildDailyMonth(selectedEmployeeId, response.data))
      await loadMonthly()
      onMessage('Daily attendance saved and monthly summary refreshed.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="manual-attendance">
      <div className="attendance-toolbar attendance-simple-toolbar">
        <label>
          Payroll month
          <input type="month" value={month} onChange={(event) => setMonth(event.target.value)} />
        </label>
        <div className="attendance-mode">
          <button type="button" onClick={loadMonthly} disabled={loadingMonthly}>Refresh review</button>
        </div>
      </div>

      <PageTabs items={attendanceTabs} value={activeTab} onChange={setActiveTab} label="Attendance review sections" getLabel={item => item === 'daily' ? 'Daily Attendance' : item[0].toUpperCase() + item.slice(1)} />

      {activeTab === 'summary' && (
        <section className="attendance-panel">
          <div className="attendance-panel-head">
            <div>
              <h3>Attendance Summary</h3>
              <p>Review client attendance readiness before running payroll.</p>
            </div>
            <span className={issueRows.length ? 'attendance-status risk' : 'attendance-status'}>
              {issueRows.length ? `${issueRows.length} employees need review` : 'Ready for payroll'}
            </span>
          </div>
          <div className="attendance-summary">
            <span>Total employees<b>{summary.total}</b></span>
            <span>Ready<b>{summary.ready}</b></span>
            <span>Missing<b>{summary.missing}</b></span>
            <span>Check values<b>{summary.check}</b></span>
            <span>Payable Days<b>{summary.payableDays.toFixed(1)}</b></span>
            <span>LOP<b>{summary.lopDays.toFixed(1)}</b></span>
          </div>
          <div className="attendance-next-actions">
            <button type="button" onClick={() => setActiveTab('employees')}>Open employee totals</button>
            <button type="button" className="primary" onClick={() => openDailyReview((issueRows[0] ?? monthlyRows[0])?.employeeId ?? 0)} disabled={!monthlyRows.length}>Fix daily attendance</button>
          </div>
          <div className="manual-attendance-table attendance-simple-table">
            <table>
              <thead>
                <tr>
                  <th>Employee</th>
                  <th>Issue</th>
                  <th>Payable Days</th>
                  <th>LOP</th>
                  <th>Action</th>
                </tr>
              </thead>
              <tbody>
                {(issueRows.length ? issueRows : monthlyRows.slice(0, 8)).map((row) => {
                  const status = reviewStatus(row)
                  return (
                    <tr key={row.employeeId} className={status === 'Ready' ? '' : 'attendance-row-risk'}>
                      <td>
                        <strong>{row.employeeName}</strong>
                        <small>{row.employeeCode || 'No code'} {row.department ? `- ${row.department}` : ''}</small>
                      </td>
                      <td><span className={badgeClass(status)}>{status}</span></td>
                      <td>{toNumber(row.payableDays).toFixed(1)}</td>
                      <td>{toNumber(row.lopDays).toFixed(1)}</td>
                      <td><button type="button" onClick={() => openDailyReview(row.employeeId)}>Review</button></td>
                    </tr>
                  )
                })}
                {!monthlyRows.length && (
                  <tr>
                    <td colSpan={5}>{loadingMonthly ? 'Loading employees...' : 'No employees found for this client and month.'}</td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {activeTab === 'employees' && (
        <section className="attendance-panel">
          <div className="attendance-panel-head">
            <div>
              <h3>Employee Monthly Totals</h3>
              <p>Edit only the monthly totals here. Use Daily Attendance for date-wise fixes.</p>
            </div>
            <button type="button" className="attendance-primary-button" onClick={saveMonthly} disabled={saving || loadingMonthly || !monthlyRows.length}>Save monthly corrections</button>
          </div>
          <div className="manual-attendance-table">
            <table>
              <thead>
                <tr>
                  <th>Employee</th>
                  <th>Attendance Status</th>
                  <th>Work</th>
                  <th>Present</th>
                  <th>Payable Days</th>
                  <th>LOP</th>
                  <th>Action</th>
                </tr>
              </thead>
              <tbody>
                {monthlyRows.map((row) => {
                  const status = reviewStatus(row)
                  const active = row.employeeId === selectedEmployeeId
                  return (
                    <tr
                      key={row.employeeId}
                      className={`${status === 'Ready' ? '' : 'attendance-row-risk'} ${active ? 'attendance-row-active' : ''}`}
                      onClick={() => setSelectedEmployeeId(row.employeeId)}
                    >
                      <td>
                        <strong>{row.employeeName}</strong>
                        <small>{row.employeeCode || 'No code'} {row.department ? `- ${row.department}` : ''}</small>
                      </td>
                      <td><span className={badgeClass(status)}>{status}</span></td>
                      <td><input type="number" step="0.5" value={row.workingDays} onChange={(event) => updateMonthly(row.employeeId, 'workingDays', event.target.value)} /></td>
                      <td><input type="number" step="0.5" value={row.presentDays} onChange={(event) => updateMonthly(row.employeeId, 'presentDays', event.target.value)} /></td>
                      <td><input type="number" step="0.5" value={row.payableDays} onChange={(event) => updateMonthly(row.employeeId, 'payableDays', event.target.value)} /></td>
                      <td><input type="number" step="0.5" value={row.lopDays} onChange={(event) => updateMonthly(row.employeeId, 'lopDays', event.target.value)} /></td>
                      <td><button type="button" onClick={() => openDailyReview(row.employeeId)}>Daily</button></td>
                    </tr>
                  )
                })}
                {!monthlyRows.length && (
                  <tr>
                    <td colSpan={7}>{loadingMonthly ? 'Loading employees...' : 'No employees found for this client and month.'}</td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {activeTab === 'daily' && (
        <section className="attendance-panel attendance-drilldown">
          <div className="attendance-panel-head">
            <div>
              <h3>Daily Attendance</h3>
              <p>{selectedEmployee ? `${selectedEmployee.employeeName} - ${selectedEmployee.employeeCode || 'No code'}` : 'Select one employee and review the complete month.'}</p>
            </div>
            <span className={missingDailyCount ? 'attendance-status risk' : 'attendance-status'}>
              {missingDailyCount ? `${missingDailyCount} dates missing` : 'Daily ready'}
            </span>
          </div>
          <div className="attendance-daily-picker">
            <label>
              Employee
              <select value={selectedEmployeeId} onChange={(event) => setSelectedEmployeeId(Number(event.target.value))}>
                <option value={0}>Select employee</option>
                {monthlyRows.map((row) => (
                  <option key={row.employeeId} value={row.employeeId}>{row.employeeName} - {reviewStatus(row)}</option>
                ))}
              </select>
            </label>
          </div>
          <div className="attendance-quick-actions">
            <button type="button" onClick={() => fillMissing('Present')} disabled={!missingDailyCount}>Mark missing present</button>
            <button type="button" className="primary" onClick={saveDaily} disabled={saving || loadingDaily || !selectedEmployeeId}>Save daily month</button>
          </div>
          <div className="manual-attendance-table attendance-date-table">
            <table>
              <thead>
                <tr>
                  <th>Date</th>
                  <th>Day</th>
                  <th>Record</th>
                  <th>Attendance Status</th>
                  <th>Payable Days</th>
                  <th>Remarks</th>
                </tr>
              </thead>
              <tbody>
                {dailyRows.map((row) => {
                  const day = new Date(`${row.attendanceDate}T00:00:00`).toLocaleDateString(undefined, { weekday: 'short' })
                  return (
                    <tr key={row.attendanceDate} className={row.isMissing ? 'missing-day' : ''}>
                      <td><strong>{row.attendanceDate}</strong></td>
                      <td>{day}</td>
                      <td><span className={row.isMissing ? 'attendance-status risk' : 'attendance-status'}>{row.isMissing ? 'Missing' : 'Saved'}</span></td>
                      <td>
                        <select value={row.status === 'Present' || activeLeaveTypes.some((leaveType) => leaveType.code === row.status) ? row.status : 'Present'} onChange={(event) => updateDaily(row.attendanceDate, { status: event.target.value })}>
                          <option value="Present">Present</option>
                          {activeLeaveTypes.map((leaveType) => <option key={leaveType.id} value={leaveType.code}>{leaveType.name}</option>)}
                        </select>
                      </td>
                      <td><input type="number" min="0" max="1" step="1" value={row.payableValue} readOnly title="Automatically derived from the selected attendance or leave payroll rule." /></td>
                      <td><input value={row.remarks} onChange={(event) => updateDaily(row.attendanceDate, { remarks: event.target.value })} placeholder="Optional note" /></td>
                    </tr>
                  )
                })}
                {!dailyRows.length && (
                  <tr>
                    <td colSpan={6}>{loadingDaily ? 'Loading daily attendance...' : 'Select an employee to open date-wise attendance.'}</td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </div>
  )
}
