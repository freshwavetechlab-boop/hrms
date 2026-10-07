import { useEffect, useState } from 'react'
import { Select } from 'antd'
import { getAttendanceShifts } from '../services/leaveAttendanceService'
import type { AttendanceShift } from '../types/payroll'

export default function AttendanceShiftSelect({ clientId, value, onChange, defaultShift = false }: { clientId: number; value?: number | null; onChange: (id: number | null) => void; defaultShift?: boolean }) {
  const [shifts, setShifts] = useState<AttendanceShift[]>([])
  const [loading, setLoading] = useState(false)
  useEffect(() => {
    let active = true; setShifts([]); setLoading(true)
    void getAttendanceShifts(clientId).then(rows => { if (active) { setShifts(rows); setLoading(false) } })
    return () => { active = false }
  }, [clientId])
  return <Select aria-label={defaultShift ? 'Default shift' : 'Assigned shift'} loading={loading} disabled={!clientId} showSearch allowClear optionFilterProp="label" value={value || undefined} onChange={id => onChange(id || null)} placeholder={defaultShift ? 'No default shift — use existing attendance rules' : 'Use client default shift'}
    options={shifts.filter(shift => shift.clientId === clientId && (shift.isActive || shift.id === value)).map(shift => ({ value: shift.id, label: `${shift.shiftCode} · ${shift.shiftName}${shift.isActive ? '' : ' (Inactive)'}`, disabled: !shift.isActive }))} />
}
