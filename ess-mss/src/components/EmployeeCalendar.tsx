import type { CalendarDayInfo, DailyAttendance, Holiday, LeaveRequest } from '../types'

type Props = {
  month: string
  requests: Pick<LeaveRequest, 'leaveType' | 'fromDate' | 'toDate' | 'status'>[]
  holidays: Holiday[]
  daily: DailyAttendance[]
  onSelect: (day: CalendarDayInfo) => void
}

export function EmployeeCalendar({ month, requests, holidays, daily, onSelect }: Props) {
  const first = new Date(`${month}-01T00:00:00`)
  const days = new Date(first.getFullYear(), first.getMonth() + 1, 0).getDate()
  const cells = Array.from({ length: first.getDay() + days }, (_, index) => index < first.getDay() ? null : index - first.getDay() + 1)
  const iso = (day: number) => `${month}-${String(day).padStart(2, '0')}`
  const info = (day: number): CalendarDayInfo => {
    const date = iso(day)
    const leave = requests.find(item => item.fromDate.slice(0, 10) <= date && item.toDate.slice(0, 10) >= date)
    const holiday = holidays.find(item => item.startDate.slice(0, 10) <= date && item.endDate.slice(0, 10) >= date)
    const row = daily.find(item => item.attendanceDate.slice(0, 10) === date)
    const status = row?.status || 'Not marked'
    const label = holiday ? `Holiday: ${holiday.name}` : leave ? `${leave.leaveType}: ${leave.status}` : status
    return { date, status, label, canApply: !holiday && !leave && ['not marked', 'absent', 'half day'].includes(status.toLowerCase()), leave: leave && { leaveType: leave.leaveType, status: leave.status }, holiday: holiday && { name: holiday.name } }
  }
  return <div className="employee-calendar"><div>Sun</div><div>Mon</div><div>Tue</div><div>Wed</div><div>Thu</div><div>Fri</div><div>Sat</div>{cells.map((day, index) => { const item = day ? info(day) : null; return <button type="button" className={`calendar-day ${item?.holiday ? 'holiday' : ''} ${item?.leave ? 'leave' : ''} ${item?.status.toLowerCase().replace(/\s+/g, '-') || ''}`} key={index} onClick={() => item && onSelect(item)} disabled={!item}>{day && <><b>{day}</b><small>{item?.label}</small></>}</button> })}</div>
}
