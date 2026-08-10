import { Alert, Input, Modal, Radio } from 'antd'
import { useEffect, useState } from 'react'

export type AdminMaintenanceAction = 'revert' | 'delete'

type Props = {
  open: boolean
  recordType: string
  recordLabel: string
  status: string
  onClose: () => void
  onConfirm: (action: AdminMaintenanceAction, reason: string) => Promise<void>
}

export function AdminRecordMaintenanceModal({ open, recordType, recordLabel, status, onClose, onConfirm }: Props) {
  const [action, setAction] = useState<AdminMaintenanceAction>(status === 'Draft' ? 'delete' : 'revert')
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!open) return
    setAction(status === 'Draft' ? 'delete' : 'revert')
    setReason('')
    setBusy(false)
  }, [open, status])

  const confirm = async () => {
    if (!reason.trim()) return
    setBusy(true)
    try { await onConfirm(action, reason.trim()) }
    finally { setBusy(false) }
  }

  return <Modal
    open={open}
    title={`Administrative cleanup · ${recordType}`}
    okText={action === 'delete' ? 'Permanently delete' : 'Revert to draft'}
    okButtonProps={{ danger: action === 'delete', disabled: !reason.trim() }}
    confirmLoading={busy}
    onOk={() => void confirm()}
    onCancel={onClose}
    destroyOnClose
    className="admin-maintenance-modal"
  >
    <div className="admin-maintenance-record"><span>Selected record</span><strong>{recordLabel}</strong><em>{status}</em></div>
    <Alert
      type="warning"
      showIcon
      message="Financially consumed records stay protected"
      description="If payroll has consumed this expense, or a travel advance is paid or settled, cleanup will be blocked with the exact dependency to reverse first."
    />
    <Radio.Group value={action} onChange={event => setAction(event.target.value)} className="admin-maintenance-actions">
      {status !== 'Draft' && <Radio.Button value="revert">Revert to Draft</Radio.Button>}
      <Radio.Button value="delete">Permanent Delete</Radio.Button>
    </Radio.Group>
    <p className="admin-maintenance-explanation">{action === 'revert'
      ? 'Closes the old approval trail, removes only unconsumed payroll dependencies, and makes the record editable again.'
      : 'Deletes this record, its approval trail, and only those generated dependencies which have not been consumed financially.'}</p>
    <label className="admin-maintenance-reason"><span>Cleanup reason <b>*</b></span><Input.TextArea rows={3} value={reason} onChange={event => setReason(event.target.value)} placeholder="Example: Removing approved test data after validation" maxLength={500} showCount /></label>
  </Modal>
}
