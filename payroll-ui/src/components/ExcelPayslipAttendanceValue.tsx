import { Tooltip } from 'antd'
import { attendanceDayText, type AttendanceDayValue } from '../utils/excelPayslipAttendanceDisplay'

export default function ExcelPayslipAttendanceValue({ value, showLabel = false }: { value: AttendanceDayValue; showLabel?: boolean }) {
  return <Tooltip title={[value.label, value.reason].filter(Boolean).join(' · ')}>
    <span className="excel-payslip-attendance-value">
      <span>{attendanceDayText(value)}</span>
      {showLabel && value.label && <small>{value.label}</small>}
    </span>
  </Tooltip>
}
