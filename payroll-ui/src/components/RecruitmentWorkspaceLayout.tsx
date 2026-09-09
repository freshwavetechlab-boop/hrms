import { useId, useLayoutEffect, useRef, type CSSProperties, type ReactNode } from 'react'
import { theme } from 'antd'
import './RecruitmentWorkspaceLayout.css'

export type RecruitmentWorkspaceSection = {
  key: string
  label: ReactNode
  icon?: ReactNode
  description?: ReactNode
  badge?: ReactNode
  disabled?: boolean
}

type Props = {
  navigation?: RecruitmentWorkspaceSection[]
  activeKey?: string
  scrollKey?: string | number
  onChange?: (key: string) => void
  sidebar?: ReactNode
  children: ReactNode
  footer?: ReactNode
  className?: string
  ariaLabel?: string
}

/** Shared presentation only: the owning workspace keeps its data and actions. */
export default function RecruitmentWorkspaceLayout({
  navigation = [], activeKey, scrollKey, onChange, sidebar, children, footer,
  className, ariaLabel = 'Recruitment workspace',
}: Props) {
  const contentId = useId()
  const contentRef = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    if (contentRef.current) contentRef.current.scrollTop = 0
  }, [activeKey, scrollKey])
  const { token } = theme.useToken()
  const workspaceStyle = {
    '--workspace-accent': token.colorPrimary,
    '--workspace-background': token.colorBgContainer,
    '--workspace-sidebar-background': token.colorBgLayout,
    '--workspace-border': token.colorBorder,
    '--workspace-text-secondary': token.colorTextSecondary,
    '--workspace-active-background': token.colorPrimaryBg,
  } as CSSProperties
  return <section className={['recruitment-guided-workspace', className].filter(Boolean).join(' ')} aria-label={ariaLabel} style={workspaceStyle}>
    <div className="recruitment-guided-workspace-body">
      <aside className="recruitment-guided-workspace-sidebar" aria-label={`${ariaLabel} navigation`}>
        {navigation.length > 0 && <nav className="recruitment-guided-workspace-navigation">
          {navigation.map(item => <button
            key={item.key}
            type="button"
            className={activeKey === item.key ? 'is-active' : undefined}
            aria-current={activeKey === item.key ? 'step' : undefined}
            aria-controls={contentId}
            disabled={item.disabled}
            onClick={() => onChange?.(item.key)}
          >
            {item.icon && <span className="recruitment-guided-workspace-nav-icon" aria-hidden>{item.icon}</span>}
            <span className="recruitment-guided-workspace-nav-copy"><strong>{item.label}</strong>{item.description && <small>{item.description}</small>}</span>
            {item.badge != null && <span className="recruitment-guided-workspace-nav-badge">{item.badge}</span>}
          </button>)}
        </nav>}
        {sidebar}
      </aside>
      <div ref={contentRef} id={contentId} className="recruitment-guided-workspace-content" tabIndex={-1}>{children}</div>
    </div>
    {footer && <footer className="recruitment-guided-workspace-footer">{footer}</footer>}
  </section>
}
