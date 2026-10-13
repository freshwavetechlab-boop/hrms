import { useEffect, useState } from 'react'
import { Alert, Form, Select } from 'antd'
import type { AttendanceGroup, AttendanceShift } from '../types/payroll'
import { getJsonResult } from '../services/apiClient'
import AttendanceFieldHelp from './AttendanceFieldHelp'

type Fields = Pick<AttendanceGroup, 'clientId' | 'attendanceMode' | 'allowedShiftIds' | 'shiftId'>
export default function AttendancePolicyShiftFields({ value, onChange, allowLegacy }: { value: Fields; onChange: (patch: Partial<Fields>) => void; allowLegacy: boolean }) {
  const [shifts, setShifts] = useState<AttendanceShift[]>([]), [error, setError] = useState(''), [loading, setLoading] = useState(false)
  useEffect(() => {
    let active = true; setShifts([]); setError(''); setLoading(false)
    if (value.clientId > 0) { setLoading(true); void getJsonResult<AttendanceShift[]>(`/api/leave-attendance/shifts?clientId=${value.clientId}`, []).then(r => { if (active) { setLoading(false); if (r.ok) setShifts(r.data); else setError(r.error || 'Unable to load shifts.') } }) }
    return () => { active = false }
  }, [value.clientId])
  const mode = value.attendanceMode ?? 'Legacy'
  const options = shifts.map(s => ({ value: s.id, label: `${s.shiftCode} - ${s.shiftName}${s.isActive ? '' : ' (inactive)'}`, disabled: !s.isActive }))
  return <>
    {error && <Alert type="error" message={error} />}
    <Form.Item label={<AttendanceFieldHelp label="Attendance mode" text="Punch-based uses a shift and IN/OUT records. Monthly uses entered attendance totals without shift calculation. Legacy preserves existing behavior until you explicitly migrate this policy." example="Factory: punch-based. Client supplies 26 payable days: monthly." />} required>
      <Select aria-label="Attendance mode" value={mode} options={[{ value: 'Punch', label: 'Punch-based attendance' }, { value: 'Monthly', label: 'Monthly attendance totals' }, ...(allowLegacy ? [{ value: 'Legacy', label: 'Existing rules (legacy)' }] : [])]} onChange={attendanceMode => onChange({ attendanceMode, ...(attendanceMode === 'Monthly' ? { shiftId: null, allowedShiftIds: [] } : { allowedShiftIds: value.allowedShiftIds?.length ? value.allowedShiftIds : value.shiftId ? [value.shiftId] : [] }) })} />
    </Form.Item>
    {mode !== 'Monthly' && <>
      {mode === 'Punch' && <Form.Item label={<AttendanceFieldHelp label="Allowed shifts" text="Shifts that this policy's employees may work. This list does not assign multiple shifts to an employee. Select a default below or assign employee/date rosters." example="Allow Day, Night and Flexible; assign Night to the night team." />} required>
        <Select aria-label="Allowed shifts" mode="multiple" loading={loading} showSearch optionFilterProp="label" value={value.allowedShiftIds ?? []} options={options} onChange={allowedShiftIds => onChange({ allowedShiftIds, shiftId: value.shiftId && allowedShiftIds.includes(value.shiftId) ? value.shiftId : null })} />
      </Form.Item>}
      <Form.Item label={<AttendanceFieldHelp label="Default shift" text={mode === 'Legacy' ? 'Blank keeps the existing client fallback.' : 'Used when no employee/date roster exists. Blank means a dated roster is required. Missing or expired shifts cause an error, not a guessed workday.'} example="Office employees use Day by default; a dated Night roster overrides it." />}>
        <Select aria-label="Default policy shift" allowClear loading={loading} showSearch optionFilterProp="label" value={value.shiftId ?? undefined} options={options.filter(s => mode === 'Legacy' || value.allowedShiftIds?.includes(s.value))} placeholder={mode === 'Punch' ? 'Roster required if blank' : 'Client default'} onChange={shiftId => onChange({ shiftId: shiftId ?? null })} />
      </Form.Item>
    </>}
  </>
}
