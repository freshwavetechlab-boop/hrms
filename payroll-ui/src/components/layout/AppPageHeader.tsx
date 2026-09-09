import type { ReactNode } from 'react'
import { Breadcrumb } from 'antd'
import type { BreadcrumbProps } from 'antd'

export default function AppPageHeader({ title, description, icon, breadcrumbs, actions }: {
  title: string
  description: string
  icon: ReactNode
  breadcrumbs: BreadcrumbProps['items']
  actions?: ReactNode
}) {
  return <header className="hrms-page-header">
    <div className="hrms-page-heading">
      <span className="hrms-page-icon" aria-hidden="true">{icon}</span>
      <div className="hrms-page-copy">
        <Breadcrumb className="hrms-page-breadcrumb" items={breadcrumbs} />
        <h1>{title}</h1>
        <p>{description}</p>
      </div>
    </div>
    {actions && <div className="hrms-page-actions">{actions}</div>}
  </header>
}
