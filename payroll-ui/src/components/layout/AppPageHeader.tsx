import { useEffect, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Breadcrumb } from 'antd'
import type { BreadcrumbProps } from 'antd'

export default function AppPageHeader({ title, description, icon, breadcrumbs, actions, recruitment = false }: {
  title: string
  description: string
  icon: ReactNode
  breadcrumbs: BreadcrumbProps['items']
  actions?: ReactNode
  recruitment?: boolean
}) {
  return <header className={`hrms-page-header${recruitment ? ' recruitment-page-header' : ''}`}>
    <div className="hrms-page-heading">
      <span className="hrms-page-icon" aria-hidden="true">{icon}</span>
      <div className="hrms-page-copy">
        <div className="hrms-page-context-row">
          <Breadcrumb className="hrms-page-breadcrumb" items={breadcrumbs} />
          <div className="hrms-page-actions">{actions}{recruitment && <span id="recruitment-client-controls" />}</div>
        </div>
        <h1>{title}</h1>
        {recruitment ? <div className="recruitment-title-controls"><span id="recruitment-view-controls" /><span id="recruitment-page-controls" /></div> : <p>{description}</p>}
      </div>
    </div>
  </header>
}

export function PageHeaderPortal({ slot, children }: { slot: string; children: ReactNode }) {
  const [target, setTarget] = useState<HTMLElement | null>(null)
  useEffect(() => { setTarget(document.getElementById(slot)) }, [slot])
  return target ? createPortal(children, target) : null
}
