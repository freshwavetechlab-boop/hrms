import { useCallback, useEffect, useRef, useState } from 'react'
import { Alert, Button, Modal, Spin, Tag } from 'antd'
import { DeleteOutlined, LinkOutlined, LogoutOutlined, ReloadOutlined, UserOutlined } from '@ant-design/icons'
import { Link } from 'react-router-dom'
import { useAuthSession } from './AuthGate'
import { getJsonResult } from '../services/apiClient'
import { deleteEmployee, type EmployeeDeletePreview } from '../services/settingsService'
import { deleteSecurityUser } from '../services/securityService'
import type { Employee } from '../types/payroll'
import './EmployeeLifecycleDialog.css'

/** One on-demand dialog for the directory; never fetch the user list per row. */
export default function EmployeeLifecycleDialog({ employee, onClose, onChanged, onResign }: {
  employee: Employee; onClose: () => void; onChanged: () => void | Promise<void>; onResign: (employee: Employee) => void
}) {
  const session = useAuthSession()
  const [preview, setPreview] = useState<EmployeeDeletePreview | null>(null)
  const [loading, setLoading] = useState(true), [busy, setBusy] = useState<number | 'employee' | null>(null)
  const [error, setError] = useState(''), [notice, setNotice] = useState('')
  const generation = useRef(0), changed = useRef(false)
  const canManageLogins = !!session?.user.permissions.includes('security.manage') ||
    (!!session?.user.clientId && !!session?.user.permissions.includes('client.users.manage'))
  const refresh = useCallback(async () => {
    const version = ++generation.current
    setLoading(true); setError('')
    try {
      const result = await getJsonResult<EmployeeDeletePreview | null>(`/api/employees/${employee.id}/delete-preview`, null, { toast: false, loader: false })
      if (version !== generation.current) return
      setPreview(result.ok ? result.data : null)
      if (!result.ok || !result.data) setError(result.error || 'Unable to load employee links. Retry before deleting.')
    } finally { if (version === generation.current) setLoading(false) }
  }, [employee.id])
  useEffect(() => { void refresh(); return () => { generation.current++ } }, [refresh])
  const close = () => {
    if (busy !== null) return
    onClose()
    if (changed.current) void onChanged()
  }
  const removeLogin = async (id: number) => {
    if (busy !== null || loading) return
    setBusy(id); setError(''); setNotice('')
    try {
      const result = await deleteSecurityUser(id)
      if (!result.ok) { setError(result.error || 'Unable to remove this login.'); return }
      changed.current = true
      await refresh()
      setNotice('Linked login removed. You can continue here.')
    } catch { setError('Unable to reach the server. Refresh the linked records before trying again.') }
    finally { setBusy(null) }
  }
  const removeEmployee = async () => {
    if (busy !== null || loading || !preview?.canDelete) return
    setBusy('employee'); setError('')
    try {
      const result = await deleteEmployee(employee.id)
      if (!result.ok) { await refresh(); setError(result.error || 'Unable to delete employee.'); return }
      onClose(); await onChanged()
    } catch { setError('Unable to reach the server. Refresh before trying again.') }
    finally { setBusy(null) }
  }
  const name = `${employee.firstName} ${employee.lastName}`.trim() || employee.employeeCode
  const users = preview?.linkedUsers || []
  const hasHistory = (preview?.dependencies || []).some(row => row.key !== 'logins' && row.count > 0) ||
    (preview?.links || []).some(link => !/login users?$/i.test(link))
  const canResign = employee.isActive && !employee.lastWorkingDate
  return <Modal open title={<span className="employee-lifecycle-title"><DeleteOutlined /> Delete employee</span>}
    className="employee-lifecycle-modal" width={660} onCancel={close} maskClosable={busy === null} closable={busy === null} keyboard={busy === null}
    footer={<div className="employee-lifecycle-footer">
      {canResign && <Button icon={<LogoutOutlined />} disabled={busy !== null} onClick={() => { if (changed.current) void onChanged(); onResign(employee) }}>Resign instead</Button>}
      <span className="employee-lifecycle-footer-end"><Button disabled={busy !== null} onClick={close}>Cancel</Button>
        <Button danger type="primary" icon={<DeleteOutlined />} loading={busy === 'employee'} disabled={loading || busy !== null || !preview?.canDelete} onClick={() => void removeEmployee()}>Delete employee</Button></span>
    </div>}>
    <div className="employee-lifecycle-person"><span className="employee-lifecycle-avatar"><UserOutlined /></span><div><strong>{name}</strong><span>{employee.employeeCode}</span></div><Tag color={employee.isActive ? 'green' : 'default'}>{employee.employmentStatus || (employee.isActive ? 'Active' : 'Inactive')}</Tag></div>
    {error && <Alert showIcon type="error" message={error} action={<Button size="small" icon={<ReloadOutlined />} disabled={busy !== null} onClick={() => void refresh()}>Retry</Button>} />}
    {notice && !error && <Alert showIcon type="success" message={notice} />}
    <Spin spinning={loading}>
      {preview && <>
        <div className="employee-lifecycle-section-title"><LinkOutlined /><strong>Linked logins</strong><Tag>{users.length}</Tag></div>
        {users.length ? <div className="employee-lifecycle-logins">{users.map(user => <div className="employee-lifecycle-login" key={user.id}>
          <div><strong>{user.displayName || user.email}</strong><span>{user.email}</span><Tag color={user.isActive ? 'green' : 'default'}>{user.isActive ? 'Active login' : 'Inactive login'}</Tag></div>
          <Button danger size="small" icon={<DeleteOutlined />} loading={busy === user.id} disabled={loading || busy !== null || !canManageLogins || user.id === session?.user.id} onClick={() => void removeLogin(user.id)}>Remove login</Button>
        </div>)}</div> : <p className="employee-lifecycle-empty">{preview.linkedUsers ? 'No linked login to remove.' : preview.links.some(link => /login user/i.test(link)) ? 'Restart the updated API to manage the linked login here.' : 'No linked login found.'}</p>}
        {!!users.length && !canManageLogins && <p className="employee-lifecycle-hint">User management permission is required to remove a login.</p>}
        {!!users.some(user => user.id === session?.user.id) && <p className="employee-lifecycle-hint">Your signed-in account cannot be removed here.</p>}
        {!!preview.links.length && <details className="employee-lifecycle-dependencies"><summary>Where this employee is used <Tag>{preview.dependencies?.length || preview.links.length}</Tag></summary>
          {preview.dependencies?.length ? preview.dependencies.map(item => <div key={item.key}><span>{item.label}</span><Tag>{item.count}</Tag>{item.route && <Link to={item.route} onClick={close}>View</Link>}</div>) : preview.links.map(link => <div key={link}>{link}</div>)}
        </details>}
        <Alert showIcon type={preview.canDelete ? 'info' : hasHistory ? 'warning' : 'info'}
          message={preview.canDelete ? 'Ready to remove from Employee Master' : hasHistory ? 'Keep history and end employment with Resign' : 'Remove the linked login, then delete the employee here'}
          description={hasHistory ? 'Payroll, attendance and other linked records are kept. Resign stops future active eligibility and linked ESS access after the last working date.' : preview.canDelete ? 'This employee will leave the active employee list.' : 'No need to leave this screen. Linked records refresh after removal.'} />
      </>}
    </Spin>
  </Modal>
}
