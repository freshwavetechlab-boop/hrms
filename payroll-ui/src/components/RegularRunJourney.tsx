import type { ReactNode } from 'react'
import { CheckOutlined, ClockCircleOutlined, DownOutlined, ExclamationOutlined, LoadingOutlined, PlayCircleOutlined, SafetyCertificateOutlined } from '@ant-design/icons'
import './RegularRunJourney.css'

export type JourneyStep = {
  id: string
  title: string
  detail: string
  state: 'done' | 'active' | 'blocked' | 'warning' | 'pending'
  content?: ReactNode
}

type Props = {
  title: string
  subtitle: string
  status: 'ready' | 'running' | 'blocked' | 'review' | 'approval' | 'approved'
  statusLabel?: string
  preparation?: boolean
  steps: JourneyStep[]
  action?: ReactNode
  notice?: ReactNode
  metrics: Array<{ label: string; value: string; hint?: string }>
  children?: ReactNode
}

const statusLabels: Record<Props['status'], string> = {
  ready: 'Ready to begin',
  running: 'Payroll in progress',
  blocked: 'Action required',
  review: 'Ready for review',
  approval: 'Awaiting approval',
  approved: 'Payroll approved',
}

const stepLabels: Record<JourneyStep['state'], string> = {
  done: 'Complete', active: 'In progress', blocked: 'Blocked', warning: 'Review', pending: 'Not started',
}

function StepIcon({ state, index }: { state: JourneyStep['state']; index: number }) {
  if (state === 'done') return <CheckOutlined />
  if (state === 'active') return <LoadingOutlined spin />
  if (state === 'blocked' || state === 'warning') return <ExclamationOutlined />
  return <span>{index + 1}</span>
}

export default function RegularRunJourney({ title, subtitle, status, statusLabel, preparation = false, steps, action, notice, metrics, children }: Props) {
  const completed = steps.filter(step => step.state === 'done').length
  const Icon = status === 'approved' ? SafetyCertificateOutlined : status === 'blocked' ? ExclamationOutlined : status === 'running' ? LoadingOutlined : status === 'approval' ? ClockCircleOutlined : PlayCircleOutlined

  return <section className={`regular-journey regular-journey--${status}`} aria-label="Payroll run workspace">
    <div className="regular-journey-layout">
      <div className="regular-journey-main">
        <section className="regular-journey-hero" aria-label="Current payroll status">
          <div className="regular-journey-heading">
            <span className="regular-journey-run-icon" aria-hidden="true"><Icon spin={status === 'running'} /></span>
            <div className="regular-journey-heading-copy">
              <span className="regular-journey-status" role="status" aria-live="polite"><span aria-hidden="true" />{statusLabel || statusLabels[status]}</span>
              <h2>{title}</h2>
              <p>{subtitle}</p>
            </div>
          </div>
          {preparation ? <p className="regular-journey-preparation-note">Select the client and inputs. Calculation starts only when you choose <b>Run payroll</b>.</p> : <><div className="regular-journey-progress-copy"><span>Payroll progress</span><span>{completed} of {steps.length} checks complete</span></div>
          <div className="regular-journey-progress" role="img" aria-label={`${completed} of ${steps.length} payroll checks complete`}>
            {steps.map(step => <span key={step.id} className={`regular-journey-segment is-${step.state}`} title={`${step.title}: ${stepLabels[step.state]}`} />)}
          </div></>}
          {(notice || action) && <div className={`regular-journey-acknowledgment is-${status}`}>
            {notice && <div className="regular-journey-notice">{notice}</div>}
            {action && <div className="regular-journey-next-action">{action}</div>}
          </div>}
        </section>

        <section className="regular-journey-checklist" aria-label="Payroll checklist">
          <div className="regular-journey-checklist-heading"><h3>{preparation ? 'Before you run' : 'Run checklist'}</h3><span>{preparation ? 'Saved inputs only' : 'Check. Review. Approve.'}</span></div>
          <div className="regular-journey-steps">
            {steps.map((step, index) => <details key={`${step.id}:${step.state}`} className={`regular-journey-step is-${step.state}`} open={step.state === 'active' || step.state === 'blocked'}>
              <summary>
                <span className="regular-journey-step-icon" aria-hidden="true"><StepIcon state={step.state} index={index} /></span>
                <span className="regular-journey-step-copy"><strong>{step.title}</strong><span>{step.detail}</span></span>
                <span className="regular-journey-step-state">{preparation && step.state === 'done' ? 'Ready' : stepLabels[step.state]}</span>
                <DownOutlined className="regular-journey-step-chevron" aria-hidden="true" />
              </summary>
              <div className="regular-journey-step-content">{step.content || <p>{step.detail}</p>}</div>
            </details>)}
          </div>
        </section>
      </div>

      <aside className="regular-journey-sidebar" aria-label="Payroll summary and runs">
        {metrics.length > 0 && <section className="regular-journey-metrics" aria-label="Payroll totals">
          <h3>At a glance</h3>
          <dl>{metrics.map(metric => <div key={metric.label} className="regular-journey-metric"><dt>{metric.label}</dt><dd>{metric.value}</dd>{metric.hint && <span>{metric.hint}</span>}</div>)}</dl>
        </section>}
        {children && <div className="regular-journey-sidebar-content">{children}</div>}
      </aside>
    </div>
  </section>
}
