import { useEffect, useMemo, useState } from 'react'
import SetupCard from '../components/SetupCard'
import { leaveAttendanceMenus } from '../data/payrollDefaults'
import { getLeaveAttendanceSetup, setLeaveAttendanceEnabled, updateLeaveAttendanceStep } from '../services/leaveAttendanceService'
import type { LeaveAttendanceSetup, SetupStatus } from '../types/payroll'

const menuStepMap: Record<(typeof leaveAttendanceMenus)[number], string> = {
  Preferences: 'preferences',
  'Leave Types': 'leave_types',
  Holiday: 'holiday',
  Attendance: 'attendance',
  'Import Balance': 'import_balance'
}

export type LeaveAttendanceMenu = (typeof leaveAttendanceMenus)[number]

export default function LeaveAttendancePage({ activeMenu, onSelectMenu }: { activeMenu: LeaveAttendanceMenu; onSelectMenu: (menu: LeaveAttendanceMenu) => void }) {
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
    if (response.ok) { setSetup(response.data); setMessage('Leave & Attendance enabled. Complete General Settings first.') }
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

  const configure = (stepCode: string) => {
    const target = Object.entries(menuStepMap).find(([, code]) => code === stepCode)?.[0] as LeaveAttendanceMenu | undefined
    if (target) onSelectMenu(target)
    void updateStep(stepCode, 'In Progress')
  }

  if (!setup.isEnabled) return <section className="leave-attendance empty-state"><div><span className="eyebrow purple">Leave & Attendance</span><h3>Setup is not enabled</h3><p>Enable this module to configure preferences, leave types, holidays, attendance rules and opening balances.</p><button type="button" disabled={busy} onClick={() => void enable()}>{busy ? 'Enabling...' : 'Enable Leave and Attendance'}</button></div></section>

  return <section className="leave-attendance"><div className="pay-run-intro"><div><span className="eyebrow purple">Leave & Attendance Setup</span><h3>Setup dashboard</h3><p>{message}</p></div><div className="setup-module-actions"><span className="status-chip">{completed}/{setup.steps.length} completed</span><button type="button" disabled={busy} onClick={() => void disableModule()}>Disable module</button></div></div><section className="setup-dashboard">{setup.steps.map(step => <SetupCard key={step.code} step={step} active={activeStep?.code === step.code} onConfigure={() => configure(step.code)} onComplete={() => void updateStep(step.code, 'Completed')} onDisable={() => void updateStep(step.code, 'Disabled')} />)}</section>{activeStep && <section className="card setup-detail"><header><i className="blue">✓</i><div><h3>{activeStep.title}</h3><p>{activeStep.description}</p></div></header><div className="grid"><label><span>Current status</span><select value={activeStep.status} onChange={event => void updateStep(activeStep.code, event.target.value as SetupStatus)}>{(['Not Started', 'In Progress', 'Completed', 'Disabled'] as SetupStatus[]).filter(status => activeStep.canDisable || status !== 'Disabled').map(status => <option key={status}>{status}</option>)}</select></label><label><span>Mandatory</span><input readOnly value={activeStep.isMandatory ? 'Yes - cannot be disabled' : 'No'} /></label><label className="wide"><span>Configuration workspace</span><input readOnly value="Detailed forms for this setup area will be added in the next iteration." /></label></div></section>}</section>
}
