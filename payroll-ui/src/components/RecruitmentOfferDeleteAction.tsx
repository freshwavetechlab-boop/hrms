import { useRef, useState } from 'react'
import { Button, Popconfirm } from 'antd'
import { DeleteOutlined } from '@ant-design/icons'
import { useAuthSession } from './AuthGate'
import { useToast } from './ToastProvider'
import { deleteJson } from '../services/apiClient'
import { canHardDeleteRecruitmentOffers, offerDeletionPath, type OfferDeletionTarget } from '../utils/recruitmentOfferDeletion'

export default function RecruitmentOfferDeleteAction({ target, name, reference, disabled = false, onDeleted, onBusyChange }: {
  target: OfferDeletionTarget; name: string; reference: string; disabled?: boolean; onDeleted: () => Promise<void>; onBusyChange?: (busy: boolean) => void
}) {
  const session = useAuthSession(), toast = useToast()
  const [busy, setBusy] = useState(false), pending = useRef(false)
  if (!canHardDeleteRecruitmentOffers(session?.user)) return null
  const remove = async () => {
    if (pending.current || disabled) return
    pending.current = true; setBusy(true); onBusyChange?.(true)
    try {
      const response = await deleteJson(offerDeletionPath(target), null, { toast: false, loader: false })
      if (!response.ok) { toast(response.error || 'Unable to permanently delete this offer.', 'error'); return }
      toast('Offer permanently deleted.', 'success')
      window.dispatchEvent(new Event('recruitment:roster-changed'))
      await onDeleted()
    } catch (error) { toast(error instanceof Error ? error.message : 'Unable to permanently delete this offer.', 'error') }
    finally { pending.current = false; setBusy(false); onBusyChange?.(false) }
  }
  return <Popconfirm title="Permanently delete this offer?" description={<span>{name} · {reference}<br />{target.sourceType === 'Roster' ? 'Its employee-list entry and released PDFs will be permanently removed.' : 'Its generated offer letter will also be removed. Joined applications are protected.'}<br />Employee Master and payroll records are retained. This cannot be undone.</span>}
    okText="Hard delete" cancelText="Cancel" okButtonProps={{ danger: true, loading: busy }} disabled={disabled || busy} onConfirm={remove}>
    <Button size="small" danger icon={<DeleteOutlined />} disabled={disabled} loading={busy} aria-label={`Hard delete ${reference}`}>Hard delete</Button>
  </Popconfirm>
}
