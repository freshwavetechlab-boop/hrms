import type { LeaveAttendanceStep, SetupStatus } from '../types/payroll'

const tone: Record<SetupStatus, string> = {
  'Not Started': 'not-started',
  'In Progress': 'in-progress',
  Completed: 'completed',
  Disabled: 'disabled'
}

export default function SetupCard({ step, active, onConfigure, onComplete, onDisable }: { step: LeaveAttendanceStep; active: boolean; onConfigure: () => void; onComplete: () => void; onDisable: () => void }) {
  return <article className={`setup-card ${active ? 'active' : ''} ${tone[step.status]}`} title={step.description}><h3>{step.title}</h3><div><span className={`setup-status ${tone[step.status]}`}>{step.status}</span>{step.isMandatory && <span className="setup-required">Mandatory</span>}</div><footer><button type="button" onClick={onConfigure} disabled={step.status === 'Disabled'}>{step.status === 'In Progress' ? 'Continue' : 'Configure'}</button><button type="button" className="secondary" onClick={onComplete} disabled={step.status === 'Disabled' || step.status === 'Completed'}>Complete</button>{step.canDisable && <button type="button" className="ghost danger" onClick={onDisable} disabled={step.status === 'Disabled'}>Disable</button>}</footer></article>
}
