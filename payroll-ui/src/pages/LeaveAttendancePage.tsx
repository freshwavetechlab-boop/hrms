import { useEffect, useMemo, useState } from 'react'
import AttendanceSettingsForm from '../components/AttendanceSettingsForm'
import HolidayManager from '../components/HolidayManager'
import LeaveAttendancePreferencesForm from '../components/LeaveAttendancePreferencesForm'
import LeaveBalanceImportManager from '../components/LeaveBalanceImportManager'
import LeaveTypesManager from '../components/LeaveTypesManager'
import { leaveAttendanceMenus } from '../data/payrollDefaults'
import { getLeaveAttendanceSetup, setLeaveAttendanceEnabled, updateLeaveAttendanceStep } from '../services/leaveAttendanceService'
import type { LeaveAttendanceSetup, LeaveAttendanceStep, SetupStatus } from '../types/payroll'

const menuStepMap: Record<(typeof leaveAttendanceMenus)[number], string> = {
  Preferences: 'preferences',
  'Leave Types': 'leave_types',
  Holiday: 'holiday',
  Attendance: 'attendance',
  'Import Balance': 'import_balance'
}

export type LeaveAttendanceMenu = (typeof leaveAttendanceMenus)[number]

export default function LeaveAttendancePage({ activeMenu }: { activeMenu: LeaveAttendanceMenu; onSelectMenu: (menu: LeaveAttendanceMenu) => void }) {
  const [setup, setSetup] = useState<LeaveAttendanceSetup>({ isEnabled: false, steps: [] })
  const [message, setMessage] = useState('Enable the module to start Leave & Attendance setup.')
  const [busy, setBusy] = useState(false)
  const activeStepCode = menuStepMap[activeMenu]
  const activeStep = useMemo(() => setup.steps.find(step => step.code === activeStepCode), [activeStepCode, setup.steps])
  const completed = setup.steps.filter(step => step.status === 'Completed').length

  const load = async () => setSetup(await getLeaveAttendanceSetup())
  useEffect(() => { void load() }, [])

  const enable = async () => {
    setBusy(true)
    const response = await setLeaveAttendanceEnabled(true)
    if (response.ok) { setSetup(response.data); setMessage('Leave & Attendance enabled.') }
    setBusy(false)
  }

  const disableModule = async () => {
    if (!window.confirm('Disable Leave & Attendance module? General Settings will remain protected.')) return
    setBusy(true)
    const response = await setLeaveAttendanceEnabled(false)
    if (response.ok) { setSetup(response.data); setMessage('Leave & Attendance module disabled.') }
    setBusy(false)
  }

  const updateStep = async (stepCode: string, status: SetupStatus) => {
    setBusy(true)
    const response = await updateLeaveAttendanceStep(stepCode, status)
    if (response.ok) {
      setSetup(response.data)
      setMessage(status === 'Disabled' ? 'Setup step disabled.' : `${status} saved.`)
    } else setMessage('Unable to update setup status.')
    setBusy(false)
  }

  if (!setup.isEnabled) return <section className="leave-attendance empty-state"><div><span className="eyebrow purple">Leave & Attendance</span><h3>Setup is not enabled</h3><p>Enable this module to configure preferences, leave types, holidays, attendance rules and opening balances.</p><button type="button" disabled={busy} onClick={() => void enable()}>{busy ? 'Enabling...' : 'Enable Leave and Attendance'}</button></div></section>

  return <section className="leave-attendance"><div className="pay-run-intro leave-page-head"><div><span className="eyebrow purple">Leave & Attendance / {activeMenu}</span><h3>{activeStep?.title || activeMenu}</h3><p>{message || activeStep?.description}</p></div><StepActions step={activeStep} completed={completed} total={setup.steps.length} busy={busy} onComplete={() => activeStep && void updateStep(activeStep.code, 'Completed')} onDisable={() => activeStep && void updateStep(activeStep.code, 'Disabled')} onDisableModule={disableModule} /></div>{activeStep?.code === 'preferences' ? <LeaveAttendancePreferencesForm onSaved={setMessage} /> : activeStep?.code === 'leave_types' ? <LeaveTypesManager onMessage={setMessage} /> : activeStep?.code === 'holiday' ? <HolidayManager onMessage={setMessage} /> : activeStep?.code === 'attendance' ? <AttendanceSettingsForm onSaved={setMessage} /> : activeStep?.code === 'import_balance' ? <LeaveBalanceImportManager onMessage={setMessage} /> : null}</section>
}

function StepActions(p: { step?: LeaveAttendanceStep; completed: number; total: number; busy: boolean; onComplete: () => void; onDisable: () => void; onDisableModule: () => void }) {
  const tone = (p.step?.status || 'Not Started').toLowerCase().replace(/\s+/g, '-')
  return <div className="setup-module-actions compact-step-actions"><span className="status-chip">{p.completed}/{p.total} completed</span>{p.step && <span className={`setup-status ${tone}`}>{p.step.status}</span>}<button type="button" className="secondary" disabled={p.busy || !p.step || p.step.status === 'Completed' || p.step.status === 'Disabled'} onClick={p.onComplete}>Mark completed</button>{p.step?.canDisable && <button type="button" className="secondary danger" disabled={p.busy || p.step.status === 'Disabled'} onClick={p.onDisable}>Disable page</button>}<button type="button" disabled={p.busy} onClick={() => void p.onDisableModule()}>Disable module</button></div>
}
