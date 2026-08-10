import type { ReactNode } from 'react'
import { Button, Drawer, Space } from 'antd'

type RecruitmentEditorDrawerProps = {
  open: boolean
  title: ReactNode
  eyebrow?: ReactNode
  kicker?: ReactNode
  description?: ReactNode
  width?: number | string
  onClose: () => void
  onSubmit?: () => void
  submitText?: ReactNode
  submitLoading?: boolean
  submitDisabled?: boolean
  closeText?: ReactNode
  children: ReactNode
  destroyOnClose?: boolean
  className?: string
  extra?: ReactNode
  footer?: ReactNode | false
}

export default function RecruitmentEditorDrawer({
  open,
  title,
  eyebrow,
  kicker,
  description,
  width = 'min(900px, 96vw)',
  onClose,
  onSubmit,
  submitText = 'Save',
  submitLoading = false,
  submitDisabled = false,
  closeText = 'Cancel',
  children,
  destroyOnClose = true,
  className,
  extra,
  footer,
}: RecruitmentEditorDrawerProps) {
  return <Drawer
    className={['recruitment-editor-drawer', className].filter(Boolean).join(' ')}
    open={open}
    width={width}
    destroyOnClose={destroyOnClose}
    onClose={onClose}
    title={<div className="recruitment-editor-title">
      {(eyebrow || kicker) && <span>{eyebrow || kicker}</span>}
      <strong>{title}</strong>
      {description && <small>{description}</small>}
    </div>}
    extra={extra}
    footer={footer === false ? null : footer ?? <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
      <Space size={8}>
        <Button onClick={onClose}>{closeText}</Button>
        {onSubmit && <Button type="primary" loading={submitLoading} disabled={submitDisabled} onClick={onSubmit}>{submitText}</Button>}
      </Space>
    </div>}
  >
    {children}
  </Drawer>
}
