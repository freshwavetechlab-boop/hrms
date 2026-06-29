import { useEffect, useMemo, useState } from 'react'
import { setup0 } from '../data/payrollDefaults'
import { getSetup } from '../services/settingsService'
import { getLeaveAttendancePreferences, saveLeaveAttendancePreferences } from '../services/leaveAttendanceService'
import type { Component, LeaveAttendancePreferences } from '../types/payroll'
import SearchSelect, { selectOptions } from './SearchSelect'

const dayOptions = Array.from({ length: 31 }, (_, index) => index + 1)
const bufferDays = (endDay: number, reportDay: number) => reportDay >= endDay ? reportDay - endDay : reportDay + 31 - endDay

export default function LeaveAttendancePreferencesForm({ clientId, onSaved }: { clientId: number; onSaved: (message: string) => void }) {
  const [preferences, setPreferences] = useState<LeaveAttendancePreferences>({ id: 0, clientId, attendanceCycleStartDay: 1, attendanceCycleEndDay: 25, payrollReportGenerationDay: 28, includeLeaveEncashmentInPayRun: false, leaveEncashmentSalaryComponentId: null })
  const [components, setComponents] = useState<Component[]>([]), [errors, setErrors] = useState<string[]>([]), [saving, setSaving] = useState(false)
  const selectedComponent = components.find(component => component.id === Number(preferences.leaveEncashmentSalaryComponentId || 0))
  const isFormulaBased = selectedComponent?.calculationType === 'Formula'
  const buffer = useMemo(() => bufferDays(preferences.attendanceCycleEndDay, preferences.payrollReportGenerationDay), [preferences.attendanceCycleEndDay, preferences.payrollReportGenerationDay])
  const warning = preferences.includeLeaveEncashmentInPayRun && selectedComponent && !isFormulaBased ? 'Leave encashment can only be enabled for formula-based salary components.' : ''

  useEffect(() => {
    void Promise.all([getLeaveAttendancePreferences(clientId), getSetup(setup0)]).then(([saved, setup]) => {
      setPreferences(saved)
      setComponents(setup.salaryComponents ?? [])
    })
  }, [clientId])

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
    const response = await saveLeaveAttendancePreferences({ clientId,
      attendanceCycleStartDay: preferences.attendanceCycleStartDay,
      attendanceCycleEndDay: preferences.attendanceCycleEndDay,
      payrollReportGenerationDay: preferences.payrollReportGenerationDay,
      includeLeaveEncashmentInPayRun: preferences.includeLeaveEncashmentInPayRun,
      leaveEncashmentSalaryComponentId: preferences.leaveEncashmentSalaryComponentId ? Number(preferences.leaveEncashmentSalaryComponentId) : null
    })
    if (response.ok) { setPreferences(response.data); setErrors([]); onSaved('Leave & Attendance preferences saved.') } else setErrors([response.error || 'Unable to save preferences.'])
    setSaving(false)
  }

  return <section className="card preferences-form"><header><i className="blue">P</i><div><h3>General Settings / Preferences</h3><p>Mandatory settings for attendance cycle, payroll reporting and leave encashment.</p></div></header><div className="grid"><label><span>Attendance cycle start day</span><SearchSelect value={preferences.attendanceCycleStartDay} onChange={value => setPreferences({ ...preferences, attendanceCycleStartDay: Number(value) })} options={selectOptions(dayOptions)} /></label><label><span>Attendance cycle end day</span><SearchSelect value={preferences.attendanceCycleEndDay} onChange={value => setPreferences({ ...preferences, attendanceCycleEndDay: Number(value) })} options={selectOptions(dayOptions)} /></label><label><span>Payroll report generation day</span><SearchSelect value={preferences.payrollReportGenerationDay} onChange={value => setPreferences({ ...preferences, payrollReportGenerationDay: Number(value) })} options={selectOptions(dayOptions)} /><small>Current buffer: {buffer} day(s)</small></label><label><span>Leave encashment salary component</span><SearchSelect value={preferences.leaveEncashmentSalaryComponentId ?? ''} onChange={value => setPreferences({ ...preferences, leaveEncashmentSalaryComponentId: value ? Number(value) : null })} options={selectOptions(components.map(component => ({ value: component.id, label: `${component.name} / ${component.calculationType}` })), 'Select salary component')} /></label><label className="wide preference-check"><input type="checkbox" checked={preferences.includeLeaveEncashmentInPayRun} disabled={!!selectedComponent && !isFormulaBased} onChange={event => setPreferences({ ...preferences, includeLeaveEncashmentInPayRun: event.target.checked })} /><span>Include leave encashment details in pay run</span></label></div>{warning && <p className="form-warning">{warning}</p>}{errors.length > 0 && <div className="form-errors">{errors.map(error => <p key={error}>{error}</p>)}</div>}<div className="actions"><p>General Settings are mandatory and cannot be disabled.</p><button type="button" disabled={saving} onClick={() => void save()}>{saving ? 'Saving...' : 'Save preferences'}</button></div></section>
}
