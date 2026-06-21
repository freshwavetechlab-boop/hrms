import { useEffect, useMemo, useState } from 'react'
import { demoComponents, setup0 } from '../data/payrollDefaults'
import { getSetup } from '../services/settingsService'
import { getLeaveAttendancePreferences, saveLeaveAttendancePreferences } from '../services/leaveAttendanceService'
import type { Component, LeaveAttendancePreferences } from '../types/payroll'

const dayOptions = Array.from({ length: 31 }, (_, index) => index + 1)
const bufferDays = (endDay: number, reportDay: number) => reportDay >= endDay ? reportDay - endDay : reportDay + 31 - endDay

export default function LeaveAttendancePreferencesForm({ onSaved }: { onSaved: (message: string) => void }) {
  const [preferences, setPreferences] = useState<LeaveAttendancePreferences>({ id: 0, attendanceCycleStartDay: 1, attendanceCycleEndDay: 25, payrollReportGenerationDay: 28, includeLeaveEncashmentInPayRun: false, leaveEncashmentSalaryComponentId: null })
  const [components, setComponents] = useState<Component[]>([]), [errors, setErrors] = useState<string[]>([]), [saving, setSaving] = useState(false)
  const selectedComponent = components.find(component => component.id === Number(preferences.leaveEncashmentSalaryComponentId || 0))
  const isFormulaBased = selectedComponent?.calculationType === 'Formula'
  const buffer = useMemo(() => bufferDays(preferences.attendanceCycleEndDay, preferences.payrollReportGenerationDay), [preferences.attendanceCycleEndDay, preferences.payrollReportGenerationDay])
  const warning = preferences.includeLeaveEncashmentInPayRun && selectedComponent && !isFormulaBased ? 'Leave encashment can only be enabled for formula-based salary components.' : ''

  useEffect(() => {
    void Promise.all([getLeaveAttendancePreferences(), getSetup(setup0)]).then(([saved, setup]) => {
      setPreferences(saved)
      setComponents(setup.salaryComponents?.length ? setup.salaryComponents : demoComponents)
    })
  }, [])

  const validate = () => {
    const nextErrors: string[] = []
    if (buffer < 3 || buffer > 7) nextErrors.push('Payroll report generation day must be 3 to 7 days after attendance cycle end day.')
    if (preferences.includeLeaveEncashmentInPayRun && !preferences.leaveEncashmentSalaryComponentId) nextErrors.push('Select a salary component for leave encashment.')
    if (preferences.includeLeaveEncashmentInPayRun && selectedComponent && !isFormulaBased) nextErrors.push('Selected salary component must be formula-based.')
    setErrors(nextErrors)
    return nextErrors.length === 0
  }

  const save = async () => {
    if (!validate()) return
    setSaving(true)
    const response = await saveLeaveAttendancePreferences({
      attendanceCycleStartDay: preferences.attendanceCycleStartDay,
      attendanceCycleEndDay: preferences.attendanceCycleEndDay,
      payrollReportGenerationDay: preferences.payrollReportGenerationDay,
      includeLeaveEncashmentInPayRun: preferences.includeLeaveEncashmentInPayRun,
      leaveEncashmentSalaryComponentId: preferences.leaveEncashmentSalaryComponentId ? Number(preferences.leaveEncashmentSalaryComponentId) : null
    })
    if (response.ok) { setPreferences(response.data); setErrors([]); onSaved('Leave & Attendance preferences saved.') } else setErrors([response.error || 'Unable to save preferences.'])
    setSaving(false)
  }

  return <section className="card preferences-form"><header><i className="blue">P</i><div><h3>General Settings / Preferences</h3><p>Mandatory settings for attendance cycle, payroll reporting and leave encashment.</p></div></header><div className="grid"><label><span>Attendance cycle start day</span><select value={preferences.attendanceCycleStartDay} onChange={event => setPreferences({ ...preferences, attendanceCycleStartDay: Number(event.target.value) })}>{dayOptions.map(day => <option key={day}>{day}</option>)}</select></label><label><span>Attendance cycle end day</span><select value={preferences.attendanceCycleEndDay} onChange={event => setPreferences({ ...preferences, attendanceCycleEndDay: Number(event.target.value) })}>{dayOptions.map(day => <option key={day}>{day}</option>)}</select></label><label><span>Payroll report generation day</span><select value={preferences.payrollReportGenerationDay} onChange={event => setPreferences({ ...preferences, payrollReportGenerationDay: Number(event.target.value) })}>{dayOptions.map(day => <option key={day}>{day}</option>)}</select><small>Current buffer: {buffer} day(s)</small></label><label><span>Leave encashment salary component</span><select value={preferences.leaveEncashmentSalaryComponentId ?? ''} onChange={event => setPreferences({ ...preferences, leaveEncashmentSalaryComponentId: event.target.value ? Number(event.target.value) : null })}><option value="">Select salary component</option>{components.map(component => <option value={component.id} key={component.id}>{component.name} / {component.calculationType}</option>)}</select></label><label className="wide preference-check"><input type="checkbox" checked={preferences.includeLeaveEncashmentInPayRun} disabled={!!selectedComponent && !isFormulaBased} onChange={event => setPreferences({ ...preferences, includeLeaveEncashmentInPayRun: event.target.checked })} /><span>Include leave encashment details in pay run</span></label></div>{warning && <p className="form-warning">{warning}</p>}{errors.length > 0 && <div className="form-errors">{errors.map(error => <p key={error}>{error}</p>)}</div>}<div className="actions"><p>General Settings are mandatory and cannot be disabled.</p><button type="button" disabled={saving} onClick={() => void save()}>{saving ? 'Saving...' : 'Save preferences'}</button></div></section>
}
