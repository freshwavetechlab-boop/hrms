import { useEffect, useMemo, useState } from 'react'
import { loadSecurityData, saveSecurityRole, saveSecurityUser } from '../services/securityService'
import type { AuditLog, AuthPermission, AuthRole, AuthUser, Client, Employee } from '../types/payroll'
const user0 = { id: 0, email: '', displayName: '', password: 'Welcome@12345', clientId: '', employeeId: '', isActive: true, mustChangePassword: true, roles: ['employee'] }
const role0 = { id: 0, code: '', name: '', description: '', permissions: [] as string[] }

export default function SecurityPanel({ initialTab = 'Users' }: { initialTab?: 'Users' | 'Roles' | 'Audit' }) {
  const [users, setUsers] = useState<AuthUser[]>([]), [roles, setRoles] = useState<AuthRole[]>([]), [permissions, setPermissions] = useState<AuthPermission[]>([]), [auditLogs, setAuditLogs] = useState<AuditLog[]>([])
  const [clients, setClients] = useState<Client[]>([]), [employees, setEmployees] = useState<Employee[]>([]), [tab, setTab] = useState<'Users' | 'Roles' | 'Audit'>(initialTab)
  const [user, setUser] = useState(user0), [role, setRole] = useState(role0), [msg, setMsg] = useState('Create users for payroll, hiring, HR, approvers and employee self-service.')
  const [saving, setSaving] = useState(false)
  const groupedPermissions = useMemo(() => permissions.reduce<Record<string, AuthPermission[]>>((groups, permission) => ({ ...groups, [permission.module]: [...(groups[permission.module] ?? []), permission] }), {}), [permissions])
  const employeeOptions = employees.filter(employee => !user.clientId || employee.clientId === Number(user.clientId))

  useEffect(() => { void load() }, [])
  useEffect(() => { setTab(initialTab) }, [initialTab])
  const load = async () => {
    const data = await loadSecurityData()
    setUsers(data.users); setRoles(data.roles); setPermissions(data.permissions); setAuditLogs(data.auditLogs); setClients(data.clients); setEmployees(data.employees)
  }

  const saveUser = async () => {
    if (!user.displayName.trim() || !user.email.trim()) { setMsg('Display name and email/login ID are required.'); return }
    if (user.roles.length === 0) { setMsg('Select at least one role before saving the user.'); return }
    setSaving(true)
    try {
      const body = { ...user, email: user.email.trim(), displayName: user.displayName.trim(), clientId: user.clientId ? Number(user.clientId) : null, employeeId: user.employeeId ? Number(user.employeeId) : null }
      const response = await saveSecurityUser(body)
      setMsg(response.ok ? 'User saved and role assignments updated.' : response.error || 'User save failed.')
      if (response.ok) { setUser(user0); await load() }
    } catch {
      setMsg('Unable to reach the server while saving user.')
    } finally {
      setSaving(false)
    }
  }

  const saveRole = async () => {
    if (!role.code.trim() || !role.name.trim()) { setMsg('Role code and role name are required.'); return }
    setSaving(true)
    try {
      const response = await saveSecurityRole({ ...role, code: role.code.trim(), name: role.name.trim(), description: role.description.trim() })
      setMsg(response.ok ? 'Role saved with selected permissions.' : response.error || 'Role save failed.')
      if (response.ok) { setRole(role0); await load() }
    } catch {
      setMsg('Unable to reach the server while saving role.')
    } finally {
      setSaving(false)
    }
  }

  const editUser = (selected: AuthUser) => setUser({ id: selected.id, email: selected.email, displayName: selected.displayName, password: '', clientId: selected.clientId ? String(selected.clientId) : '', employeeId: selected.employeeId ? String(selected.employeeId) : '', isActive: selected.isActive, mustChangePassword: selected.mustChangePassword, roles: selected.roles })
  const editRole = (selected: AuthRole) => setRole({ id: selected.isSystem ? 0 : selected.id, code: selected.isSystem ? `${selected.code}_copy` : selected.code, name: selected.isSystem ? `${selected.name} Copy` : selected.name, description: selected.description || '', permissions: selected.permissions ? selected.permissions.split(',') : [] })
  const toggle = (list: string[], value: string) => list.includes(value) ? list.filter(item => item !== value) : [...list, value]
  const useEmployee = (employeeId: string) => {
    const selected = employees.find(employee => String(employee.id) === employeeId)
    setUser({ ...user, employeeId, clientId: selected ? String(selected.clientId) : user.clientId, email: selected?.workEmail || user.email, displayName: selected ? `${selected.firstName} ${selected.lastName}`.trim() : user.displayName, roles: user.roles.includes('employee') ? user.roles : [...user.roles, 'employee'] })
  }

  return <section className="security-module"><div className="security-hero"><div><span className="eyebrow purple">Identity Governance</span><h3>Users, roles and audit control center</h3><p>{msg}</p></div><div><strong>{users.length}</strong><span>active identities</span></div><div><strong>{roles.length}</strong><span>roles</span></div><div><strong>{permissions.length}</strong><span>permissions</span></div></div><div className="tabs">{(['Users', 'Roles', 'Audit'] as const).map(item => <button type="button" className={tab === item ? 'on' : ''} onClick={() => setTab(item)} key={item}>{item}</button>)}</div>
    {tab === 'Users' && <div className="security-layout"><section className="card"><header><i className="blue">U</i><div><h3>{user.id ? 'Edit user' : 'Create user'}</h3><p>Use employee link for future ESS users or standalone access for payroll/hiring/admin users.</p></div></header><div className="grid"><label><span>User type</span><select value={user.roles.includes('employee') && user.employeeId ? 'employee' : 'business'} onChange={event => event.target.value === 'employee' ? setUser({ ...user, roles: ['employee'] }) : setUser({ ...user, employeeId: '', roles: ['payroll_maker'] })}><option value="business">Business user</option><option value="employee">Employee / ESS user</option></select></label><label><span>Client scope</span><select value={user.clientId} onChange={event => setUser({ ...user, clientId: event.target.value, employeeId: '' })}><option value="">All clients</option>{clients.map(client => <option value={client.id} key={client.id}>{client.name}</option>)}</select></label><label className="wide"><span>Link employee</span><select value={user.employeeId} onChange={event => useEmployee(event.target.value)}><option value="">No employee link</option>{employeeOptions.map(employee => <option value={employee.id} key={employee.id}>{employee.firstName} {employee.lastName} / {employee.employeeCode} / {employee.department}</option>)}</select></label><label><span>Display name</span><input value={user.displayName} onChange={event => setUser({ ...user, displayName: event.target.value })} /></label><label><span>Email / Login ID</span><input value={user.email} onChange={event => setUser({ ...user, email: event.target.value })} /></label><label><span>{user.id ? 'Reset password' : 'Temporary password'}</span><input value={user.password} onChange={event => setUser({ ...user, password: event.target.value })} placeholder={user.id ? 'Leave blank to keep existing' : 'Welcome@12345'} /></label><label><span>Status</span><select value={user.isActive ? 'active' : 'inactive'} onChange={event => setUser({ ...user, isActive: event.target.value === 'active' })}><option value="active">Active</option><option value="inactive">Inactive</option></select></label><label><span>Must change password</span><input type="checkbox" checked={user.mustChangePassword} onChange={event => setUser({ ...user, mustChangePassword: event.target.checked })} /></label></div><h3>Role assignment</h3><div className="permission-matrix role-picker">{roles.map(item => <label className={user.roles.includes(item.code) ? 'selected' : ''} key={item.code}><input type="checkbox" checked={user.roles.includes(item.code)} onChange={() => setUser({ ...user, roles: toggle(user.roles, item.code) })} /><strong>{item.name}</strong><small>{item.description}</small></label>)}</div><div className="actions"><p>Client and employee scope will be used by ESS and future data policies.</p><button type="button" disabled={saving} onClick={() => void saveUser()}>{saving ? 'Saving...' : 'Save user'}</button></div></section><section className="security-list">{users.map(item => <article onClick={() => editUser(item)} key={item.id}><strong>{item.displayName}</strong><span>{item.email}</span><small>{item.roles.join(', ') || 'No roles'}{item.employeeId ? ` / Employee #${item.employeeId}` : ''}</small><b>{item.isActive ? 'Active' : 'Inactive'}</b></article>)}</section></div>}
    {tab === 'Roles' && <div className="security-layout"><section className="card"><header><i className="blue">R</i><div><h3>{role.id ? 'Edit custom role' : 'Create custom role'}</h3><p>Compose permissions for payroll, hiring, HR, security and future modules.</p></div></header><div className="grid"><label><span>Role code</span><input value={role.code} disabled={role.id > 0} onChange={event => setRole({ ...role, code: event.target.value })} placeholder="payroll_viewer" /></label><label><span>Role name</span><input value={role.name} onChange={event => setRole({ ...role, name: event.target.value })} /></label><label className="wide"><span>Description</span><input value={role.description} onChange={event => setRole({ ...role, description: event.target.value })} /></label></div><div className="permission-groups">{Object.entries(groupedPermissions).map(([module, items]) => <section key={module}><h4>{module}</h4><div className="permission-matrix">{items.map(permission => <label className={role.permissions.includes(permission.code) ? 'selected' : ''} key={permission.code}><input type="checkbox" checked={role.permissions.includes(permission.code)} onChange={() => setRole({ ...role, permissions: toggle(role.permissions, permission.code) })} /><strong>{permission.name}</strong><small>{permission.code}</small></label>)}</div></section>)}</div><div className="actions"><p>Clicking a system role creates a safe copy for customization.</p><button type="button" disabled={saving} onClick={() => void saveRole()}>{saving ? 'Saving...' : 'Save role'}</button></div></section><section className="security-list">{roles.map(item => <article onClick={() => editRole(item)} key={item.id}><strong>{item.name}</strong><span>{item.code}</span><small>{item.permissions || 'No permissions'}</small><b>{item.isSystem ? 'System' : 'Custom'}</b></article>)}</section></div>}
    {tab === 'Audit' && <section className="card"><header><i className="blue">A</i><div><h3>Audit trail</h3><p>Recent identity and operational activity.</p></div></header><div className="audit-list"><table><thead><tr><th>Time</th><th>User</th><th>Action</th><th>Status</th><th>Path</th></tr></thead><tbody>{auditLogs.map(log => <tr key={log.id}><td>{new Date(log.createdAt).toLocaleString()}</td><td>{log.userEmail || 'System'}</td><td>{log.action}</td><td>{log.statusCode}</td><td>{log.path}</td></tr>)}</tbody></table></div></section>}
  </section>
}
