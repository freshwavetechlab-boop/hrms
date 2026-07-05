import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Checkbox as AntCheckbox, Drawer, Input, Modal, Space, Tabs, Tag } from 'antd'
import { DownloadOutlined, ImportOutlined, KeyOutlined, PlusOutlined, SafetyCertificateOutlined, TeamOutlined, UploadOutlined } from '@ant-design/icons'
import BulkUploadPreviewModal, { emptyBulkUploadPreview, type BulkUploadPreviewState } from './BulkUploadPreviewModal'
import BulkUploadProgressModal, { type BulkUploadState, type BulkUploadSummary } from './BulkUploadProgressModal'
import { loadEmployeeProvisionPreview, loadSecurityData, provisionEmployeeLogins, saveSecurityRole, saveSecurityUser } from '../services/securityService'
import type { AuditLog, AuthPermission, AuthRole, AuthUser, Client, Employee, EmployeeLoginProvisionPreview, EmployeeLoginProvisionResponse } from '../types/payroll'
import { parseImportPreviewFile, validateImportPreview, type ImportPreviewData, type ImportPreviewRules } from '../utils/importPreview'
import { downloadXlsx } from '../utils/xlsx'
import DataTable from './DataTable'
import SearchSelect from './SearchSelect'
import '../SecurityAccess.css'

const user0 = { id: 0, email: '', displayName: '', password: '', clientId: '', employeeId: '', isActive: true, mustChangePassword: true, roles: ['employee'] }
const role0 = { id: 0, code: '', name: '', description: '', permissions: [] as string[] }
const userImportHeaders = ['Email', 'Display Name', 'Client Id', 'Employee Code', 'Roles', 'Temporary Password', 'Active', 'Must Change Password']
const securityTabs = ['Users', 'Roles', 'Audit'] as const

type SecurityTab = (typeof securityTabs)[number]
type SecurityUpload = { open: boolean; state: BulkUploadState; percent: number; summary: BulkUploadSummary }

const normalizeEmail = (value?: string | null) => (value || '').trim().toLowerCase()
const normalizeKey = (value: string) => value.replace(/[\s_-]/g, '').toLowerCase()
const parseFlag = (value: string | undefined, fallback: boolean) => {
  const clean = (value || '').trim().toLowerCase()
  if (!clean) return fallback
  if (['true', 'yes', 'active', '1'].includes(clean)) return true
  if (['false', 'no', 'inactive', '0'].includes(clean)) return false
  return fallback
}
const rowMap = (data: ImportPreviewData, row: string[]) => Object.fromEntries(data.headers.map((header, index) => [header, row[index]?.trim() ?? '']))
const cell = (map: Record<string, string>, name: string) => map[Object.keys(map).find(header => normalizeKey(header) === normalizeKey(name)) ?? name] || ''
const unique = (items: string[]) => Array.from(new Set(items.map(item => item.trim()).filter(Boolean)))

export default function SecurityPanel({ initialTab = 'Users' }: { initialTab?: SecurityTab }) {
  const [users, setUsers] = useState<AuthUser[]>([]), [roles, setRoles] = useState<AuthRole[]>([]), [permissions, setPermissions] = useState<AuthPermission[]>([]), [auditLogs, setAuditLogs] = useState<AuditLog[]>([])
  const [clients, setClients] = useState<Client[]>([]), [employees, setEmployees] = useState<Employee[]>([])
  const [user, setUser] = useState(user0), [role, setRole] = useState(role0), [msg, setMsg] = useState('Create users for payroll, hiring, HR, approvers and employee self-service.'), [directoryClientId, setDirectoryClientId] = useState('')
  const [userDrawerOpen, setUserDrawerOpen] = useState(false), [roleDrawerOpen, setRoleDrawerOpen] = useState(false), [createdCredentials, setCreatedCredentials] = useState<{ email: string; password: string } | null>(null), [saving, setSaving] = useState(false)
  const [accessRole, setAccessRole] = useState<AuthRole | null>(null), [accessPermissions, setAccessPermissions] = useState<string[]>([]), [savingAccess, setSavingAccess] = useState(false)
  const [provisionOpen, setProvisionOpen] = useState(false), [provisionLoading, setProvisionLoading] = useState(false), [provisionRows, setProvisionRows] = useState<EmployeeLoginProvisionPreview[]>([])
  const [selectedProvisionKeys, setSelectedProvisionKeys] = useState<string[]>([]), [provisionPassword, setProvisionPassword] = useState(''), [provisionMustChangePassword, setProvisionMustChangePassword] = useState(true), [provisionRoles, setProvisionRoles] = useState<string[]>(['employee'])
  const [provisionResult, setProvisionResult] = useState<EmployeeLoginProvisionResponse | null>(null)
  const [userTemplateDownloaded, setUserTemplateDownloaded] = useState(false), [userImportData, setUserImportData] = useState<ImportPreviewData | null>(null), [userImportPreview, setUserImportPreview] = useState<BulkUploadPreviewState>(emptyBulkUploadPreview), [userImporting, setUserImporting] = useState(false)
  const [userUpload, setUserUpload] = useState<SecurityUpload>({ open: false, state: 'uploading', percent: 0, summary: { totalRows: 0 } })

  const activeClientIds = useMemo(() => new Set(clients.map(client => client.id)), [clients])
  const usedEmployeeIds = useMemo(() => new Set(users.filter(item => item.id !== user.id && item.employeeId).map(item => item.employeeId as number)), [users, user.id])
  const usedEmails = useMemo(() => new Set(users.filter(item => item.id !== user.id).map(item => normalizeEmail(item.email)).filter(Boolean)), [users, user.id])
  const allUsedEmployeeIds = useMemo(() => new Set(users.filter(item => item.employeeId).map(item => item.employeeId as number)), [users])
  const allUsedEmails = useMemo(() => new Set(users.map(item => normalizeEmail(item.email)).filter(Boolean)), [users])
  const employeeByCode = useMemo(() => new Map(employees.map(employee => [employee.employeeCode.trim().toLowerCase(), employee])), [employees])
  const roleLookup = useMemo(() => new Map(roles.flatMap(role => [[role.code.toLowerCase(), role.code], [normalizeKey(role.name), role.code]])), [roles])
  const groupedPermissions = useMemo(() => {
    const groups = permissions.reduce<Record<string, AuthPermission[]>>((items, permission) => {
      const module = permission.module || 'General'
      return { ...items, [module]: [...(items[module] ?? []), permission] }
    }, {})
    return Object.entries(groups).sort(([left], [right]) => left.localeCompare(right))
  }, [permissions])
  const employeeOptions = employees.filter(employee => (!user.clientId || employee.clientId === Number(user.clientId)) && (String(employee.id) === user.employeeId || (!usedEmployeeIds.has(employee.id) && !usedEmails.has(normalizeEmail(employee.workEmail)))))
  const unlinkedEmployees = employees.filter(employee => employee.isActive && employee.workEmail && !allUsedEmployeeIds.has(employee.id) && !allUsedEmails.has(normalizeEmail(employee.workEmail)) && (!directoryClientId || employee.clientId === Number(directoryClientId)))
  const visibleUsers = users.filter(item => (!item.clientId || activeClientIds.has(item.clientId)) && (!directoryClientId || item.clientId === Number(directoryClientId)))
  const rolePermissions = (item: AuthRole) => (item.permissions || '').split(',').map(value => value.trim()).filter(Boolean)
  const clientName = (clientId?: number | null) => clientId ? clients.find(client => client.id === clientId)?.name || `Client #${clientId}` : 'All clients'
  const roleName = (code: string) => roles.find(item => item.code === code)?.name || code
  const parseRoleCodes = (value: string, fallback: string[] = []) => {
    const selected = unique(value.split(/[;,|]/).map(item => {
      const clean = item.trim()
      return roleLookup.get(clean.toLowerCase()) || roleLookup.get(normalizeKey(clean)) || clean.toLowerCase()
    })).filter(code => roles.some(role => role.code === code))
    return selected.length ? selected : fallback
  }

  useEffect(() => { void load() }, [])
  useEffect(() => { setUserDrawerOpen(false); setRoleDrawerOpen(false); setAccessRole(null) }, [initialTab])

  const load = async () => {
    const data = await loadSecurityData()
    const activeClients = data.clients.filter(client => client.isActive)
    const activeIds = new Set(activeClients.map(client => client.id))
    setUsers(data.users)
    setRoles(data.roles)
    setPermissions(data.permissions)
    setAuditLogs(data.auditLogs)
    setClients(activeClients)
    setEmployees(data.employees.filter(employee => activeIds.has(employee.clientId)))
  }

  const toggle = (list: string[], value: string) => list.includes(value) ? list.filter(item => item !== value) : [...list, value]
  const toggleProvisionRole = (code: string) => setProvisionRoles(current => current.includes(code) ? current.length === 1 ? current : current.filter(item => item !== code) : [...current, code])
  const setAllAccess = (items: AuthPermission[], selected: boolean) => {
    const codes = items.map(permission => permission.code)
    setAccessPermissions(current => selected ? Array.from(new Set([...current, ...codes])) : current.filter(code => !codes.includes(code)))
  }

  const openNewUser = () => { setCreatedCredentials(null); setUser(user0); setUserDrawerOpen(true) }
  const editUser = (selected: AuthUser) => {
    setCreatedCredentials(null)
    setUser({ id: selected.id, email: selected.email, displayName: selected.displayName, password: '', clientId: selected.clientId ? String(selected.clientId) : '', employeeId: selected.employeeId ? String(selected.employeeId) : '', isActive: selected.isActive, mustChangePassword: selected.mustChangePassword, roles: selected.roles })
    setUserDrawerOpen(true)
  }
  const openNewRole = () => { setRole(role0); setRoleDrawerOpen(true) }
  const editRole = (selected: AuthRole) => {
    setRole({ id: selected.isSystem ? 0 : selected.id, code: selected.isSystem ? `${selected.code}_copy` : selected.code, name: selected.isSystem ? `${selected.name} Copy` : selected.name, description: selected.description || '', permissions: rolePermissions(selected) })
    setRoleDrawerOpen(true)
  }
  const openAccess = (selected: AuthRole) => { setAccessRole(selected); setAccessPermissions(rolePermissions(selected)) }

  const saveUser = async () => {
    if (!user.displayName.trim() || !user.email.trim()) { setMsg('Display name and email/login ID are required.'); return }
    if (user.id === 0 && !user.password.trim()) { setMsg('Temporary password is required for a new user.'); return }
    if (user.roles.length === 0) { setMsg('Select at least one role before saving the user.'); return }
    setSaving(true)
    try {
      const body = { ...user, email: user.email.trim(), displayName: user.displayName.trim(), clientId: user.clientId ? Number(user.clientId) : null, employeeId: user.employeeId ? Number(user.employeeId) : null }
      const response = await saveSecurityUser(body)
      setMsg(response.ok ? 'User saved and role assignments updated.' : response.error || 'User save failed.')
      if (response.ok) {
        if (user.id === 0) setCreatedCredentials({ email: body.email, password: body.password })
        setUser(user0)
        setUserDrawerOpen(false)
        await load()
      }
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
      if (response.ok) { setRole(role0); setRoleDrawerOpen(false); await load() }
    } catch {
      setMsg('Unable to reach the server while saving role.')
    } finally {
      setSaving(false)
    }
  }

  const saveAccess = async () => {
    if (!accessRole) return
    setSavingAccess(true)
    try {
      const response = await saveSecurityRole({ id: accessRole.id, code: accessRole.code, name: accessRole.name, description: accessRole.description || '', permissions: accessPermissions })
      setMsg(response.ok ? 'Role access updated.' : response.error || 'Role access update failed.')
      if (response.ok) { setAccessRole(null); await load() }
    } catch {
      setMsg('Unable to reach the server while updating role access.')
    } finally {
      setSavingAccess(false)
    }
  }

  const useEmployee = (employeeId: string) => {
    const selected = employees.find(employee => String(employee.id) === employeeId)
    setUser({ ...user, employeeId, clientId: selected ? String(selected.clientId) : user.clientId, email: selected?.workEmail || user.email, displayName: selected ? `${selected.firstName} ${selected.lastName}`.trim() : user.displayName, roles: user.roles.includes('employee') ? user.roles : [...user.roles, 'employee'] })
  }
  const provisionEmployee = (employee: Employee) => {
    setCreatedCredentials(null)
    setUser({ ...user0, employeeId: String(employee.id), clientId: String(employee.clientId), email: employee.workEmail, displayName: `${employee.firstName} ${employee.lastName}`.trim(), roles: ['employee'] })
    setUserDrawerOpen(true)
    setMsg(`Provisioning login access for ${employee.firstName} ${employee.lastName}.`)
  }

  const openProvisionModal = async () => {
    setProvisionOpen(true)
    setProvisionLoading(true)
    setProvisionResult(null)
    setProvisionPassword('')
    setProvisionRoles(['employee'])
    try {
      const rows = await loadEmployeeProvisionPreview(directoryClientId)
      setProvisionRows(rows)
      setSelectedProvisionKeys(rows.map(row => String(row.employeeId)))
      setMsg(rows.length ? `${rows.length} employees are ready for login provisioning.` : 'No pending employees found for login provisioning.')
    } catch {
      setProvisionRows([])
      setSelectedProvisionKeys([])
      setMsg('Unable to load employee provisioning preview.')
    } finally {
      setProvisionLoading(false)
    }
  }
  const runProvision = async () => {
    if (selectedProvisionKeys.length === 0) { setMsg('Select at least one employee to import.'); return }
    if (provisionRoles.length === 0) { setMsg('Select at least one role for imported employees.'); return }
    setProvisionLoading(true)
    try {
      const response = await provisionEmployeeLogins({ employeeIds: selectedProvisionKeys.map(Number), roles: provisionRoles, temporaryPassword: provisionPassword.trim(), mustChangePassword: provisionMustChangePassword })
      if (response.ok && response.data) {
        setProvisionResult(response.data)
        setProvisionRows([])
        setSelectedProvisionKeys([])
        setMsg(`${response.data.createdCount} employee logins created, ${response.data.skippedCount} skipped.`)
        await load()
      } else {
        setMsg(response.error || 'Employee login import failed.')
      }
    } catch {
      setMsg('Unable to reach the server while importing employees.')
    } finally {
      setProvisionLoading(false)
    }
  }

  const userImportRules = (): ImportPreviewRules => ({
    booleans: ['Active', 'Must Change Password'],
    unique: [['Email'], ['Employee Code']],
    custom: (row, rowNumber) => {
      const issues = []
      const email = normalizeEmail(cell(row, 'Email'))
      const employeeCode = cell(row, 'Employee Code')
      const employee = employeeCode ? employeeByCode.get(employeeCode.toLowerCase()) : null
      const loginEmail = normalizeEmail(email || employee?.workEmail)
      const existingByEmail = loginEmail ? users.find(item => normalizeEmail(item.email) === loginEmail) : null
      const existingByEmployee = employee ? users.find(item => item.employeeId === employee.id) : null
      const tempPassword = cell(row, 'Temporary Password')
      const roleText = cell(row, 'Roles')
      const unknownRoles = unique(roleText.split(/[;,|]/).map(item => item.trim()).filter(Boolean)).filter(item => !roleLookup.has(item.toLowerCase()) && !roleLookup.has(normalizeKey(item)))
      if (!email && !employeeCode) issues.push({ rowNumber, column: 'Email', message: 'Email or Employee Code is required.' })
      if (email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) issues.push({ rowNumber, column: 'Email', message: 'Email must be valid.' })
      if (employeeCode && !employee) issues.push({ rowNumber, column: 'Employee Code', message: 'Employee Code was not found in Employee Master.' })
      if (employeeCode && employee && !employee.workEmail && !email) issues.push({ rowNumber, column: 'Email', message: 'Employee work email is missing; provide Email in file.' })
      if (existingByEmployee && existingByEmail && existingByEmployee.id !== existingByEmail.id) issues.push({ rowNumber, column: 'Employee Code', message: 'Employee and Email are linked to different users.' })
      if (employeeCode && existingByEmployee && (!existingByEmail || existingByEmail.id !== existingByEmployee.id)) issues.push({ rowNumber, column: 'Employee Code', message: 'Employee already has a login with another email.' })
      if (!existingByEmail && !tempPassword) issues.push({ rowNumber, column: 'Temporary Password', message: 'Temporary Password is required for new users.' })
      if (!cell(row, 'Display Name') && !employee && !existingByEmail) issues.push({ rowNumber, column: 'Display Name', message: 'Display Name is required for standalone users.' })
      if (unknownRoles.length) issues.push({ rowNumber, column: 'Roles', message: `Unknown role(s): ${unknownRoles.join(', ')}.` })
      return issues
    }
  })

  const downloadUserTemplate = () => {
    const sampleEmployee = unlinkedEmployees[0] || employees[0]
    downloadXlsx('security-user-import-template.xlsx', [
      { name: 'Users', rows: [userImportHeaders, ['manager@company.com', 'Payroll Manager', '', '', 'payroll_maker,hr_manager', 'Temp@12345', 'TRUE', 'TRUE'], ['', '', sampleEmployee ? String(sampleEmployee.clientId) : '', sampleEmployee?.employeeCode || 'EMP001', 'employee', 'Temp@12345', 'TRUE', 'TRUE']] },
      { name: 'Roles', rows: [['Role Code', 'Role Name'], ...roles.map(item => [item.code, item.name])] }
    ])
    setUserTemplateDownloaded(true)
  }
  const uploadUserImport = async (file: File | null) => {
    if (!file) return
    try {
      const data = await parseImportPreviewFile(file)
      const issues = validateImportPreview(data, userImportRules())
      setUserImportData(data)
      setUserImportPreview({ open: true, title: 'Security user bulk upload preview', fileName: file.name, headers: data.headers, rows: data.rows, issues })
    } catch {
      setUserUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors: ['Unable to read selected file. Use the downloaded XLSX/CSV template.'] } })
    }
  }
  const confirmUserImport = async () => {
    if (!userImportData) return
    const rows = userImportData.rows
    setUserImportPreview(emptyBulkUploadPreview)
    setUserImporting(true)
    setUserUpload({ open: true, state: 'uploading', percent: 1, summary: { totalRows: rows.length, completedRows: 0, inserted: 0, updated: 0, errors: [] } })
    let inserted = 0, updated = 0
    const errors: string[] = []
    for (let index = 0; index < rows.length; index++) {
      const rowNumber = index + 2
      const map = rowMap(userImportData, rows[index])
      const employeeCode = cell(map, 'Employee Code')
      const employee = employeeCode ? employeeByCode.get(employeeCode.toLowerCase()) : null
      const email = normalizeEmail(cell(map, 'Email') || employee?.workEmail)
      const existing = users.find(item => normalizeEmail(item.email) === email)
      const rolesForRow = parseRoleCodes(cell(map, 'Roles'), employee ? ['employee'] : ['payroll_maker'])
      const tempPassword = cell(map, 'Temporary Password')
      const clientText = cell(map, 'Client Id')
      const clientId = employee?.clientId || Number(String(clientText).split(':')[0]) || existing?.clientId || null
      const body = {
        id: existing?.id ?? 0,
        email,
        displayName: cell(map, 'Display Name') || (employee ? `${employee.firstName} ${employee.lastName}`.trim() : existing?.displayName || ''),
        password: existing && !tempPassword ? '' : tempPassword,
        clientId,
        employeeId: employee?.id ?? existing?.employeeId ?? null,
        isActive: parseFlag(cell(map, 'Active'), existing?.isActive ?? true),
        mustChangePassword: parseFlag(cell(map, 'Must Change Password'), existing?.mustChangePassword ?? true),
        roles: rolesForRow
      }
      const response = await saveSecurityUser(body)
      if (response.ok) {
        if (existing) updated++
        else inserted++
      } else {
        errors.push(`Row ${rowNumber}: ${response.error || 'Unable to save user.'}`)
      }
      const completedRows = index + 1
      setUserUpload({ open: true, state: 'uploading', percent: Math.max(5, Math.round((completedRows / rows.length) * 100)), summary: { totalRows: rows.length, completedRows, inserted, updated, errors } })
    }
    setUserUpload({ open: true, state: errors.length ? 'error' : 'success', percent: 100, summary: { totalRows: rows.length, completedRows: rows.length, inserted, updated, errors } })
    setMsg(errors.length ? `${inserted + updated} user rows saved, ${errors.length} failed.` : `${inserted} users created, ${updated} users updated from bulk upload.`)
    setUserImportData(null)
    setUserImporting(false)
    await load()
  }

  const renderUserDrawer = () => <Drawer className="settings-master-drawer security-user-drawer" title={<div className="settings-drawer-title"><span>User access</span><h3>{user.id ? 'Edit user' : 'Add user'}</h3><p>Create an employee-linked login or a standalone business user.</p></div>} open={userDrawerOpen} width={780} onClose={() => { setUserDrawerOpen(false); setUser(user0); setCreatedCredentials(null) }} destroyOnClose>
    <div className="settings-quick-form security-drawer-form">
      {createdCredentials && <Alert type="success" showIcon message="Login created" description={`Temporary password for ${createdCredentials.email}: ${createdCredentials.password}`} />}
      <label><span>User type</span><SearchSelect value={user.roles.includes('employee') && user.employeeId ? 'employee' : 'business'} onChange={value => value === 'employee' ? setUser({ ...user, roles: ['employee'] }) : setUser({ ...user, employeeId: '', roles: ['payroll_maker'] })} options={[{ value: 'business', label: 'Business user' }, { value: 'employee', label: 'Employee / ESS user' }]} /></label>
      <label><span>Client scope</span><SearchSelect value={user.clientId} onChange={value => setUser({ ...user, clientId: value, employeeId: '' })} options={[{ value: '', label: 'All clients' }, ...clients.map(client => ({ value: client.id, label: client.name }))]} /></label>
      <label className="wide"><span>Employee link</span><SearchSelect value={user.employeeId} onChange={useEmployee} options={[{ value: '', label: 'No employee link' }, ...employeeOptions.map(employee => ({ value: employee.id, label: `${employee.firstName} ${employee.lastName} / ${employee.employeeCode} / ${employee.department}` }))]} /></label>
      <label><span>Display name</span><input value={user.displayName} onChange={event => setUser({ ...user, displayName: event.target.value })} /></label>
      <label><span>Email / Login ID</span><input value={user.email} onChange={event => setUser({ ...user, email: event.target.value })} /></label>
      <label><span>{user.id ? 'Reset password' : 'Temporary password'}</span><input value={user.password} onChange={event => setUser({ ...user, password: event.target.value })} placeholder={user.id ? 'Leave blank to keep existing' : 'Enter temporary password'} /></label>
      <label><span>Status</span><SearchSelect value={user.isActive ? 'active' : 'inactive'} onChange={value => setUser({ ...user, isActive: value === 'active' })} options={[{ value: 'active', label: 'Active' }, { value: 'inactive', label: 'Inactive' }]} /></label>
      <label className="security-check-field"><span>Must change password</span><AntCheckbox checked={user.mustChangePassword} onChange={event => setUser({ ...user, mustChangePassword: event.target.checked })}>Required</AntCheckbox></label>
      <div className="security-drawer-section wide"><div className="security-mini-access"><b>Role assignment</b><span>{user.roles.length} selected</span></div><div className="permission-matrix role-picker">{roles.map(item => <label className={user.roles.includes(item.code) ? 'selected' : ''} key={item.code}><input type="checkbox" checked={user.roles.includes(item.code)} onChange={() => setUser({ ...user, roles: toggle(user.roles, item.code) })} /><strong>{item.name}</strong><small>{item.description}</small></label>)}</div></div>
      <div className="actions wide"><span /><Space><Button onClick={() => { setUser(user0); setCreatedCredentials(null) }}>Reset</Button><Button type="primary" loading={saving} onClick={() => void saveUser()}>{user.id ? 'Update user' : 'Create user'}</Button></Space></div>
    </div>
  </Drawer>

  const renderRoleDrawer = () => <Drawer className="settings-master-drawer security-role-drawer" title={<div className="settings-drawer-title"><span>Role management</span><h3>{role.id ? 'Edit custom role' : 'Add role'}</h3><p>Define access bundles, then tune detailed access with Manage Access.</p></div>} open={roleDrawerOpen} width={820} onClose={() => { setRoleDrawerOpen(false); setRole(role0) }} destroyOnClose>
    <div className="settings-quick-form security-drawer-form">
      <label><span>Role code</span><input value={role.code} disabled={role.id > 0} onChange={event => setRole({ ...role, code: event.target.value })} placeholder="payroll_viewer" /></label>
      <label><span>Role name</span><input value={role.name} onChange={event => setRole({ ...role, name: event.target.value })} /></label>
      <label className="wide"><span>Description</span><input value={role.description} onChange={event => setRole({ ...role, description: event.target.value })} /></label>
      <div className="security-drawer-section wide"><div className="security-mini-access"><b>Initial permissions</b><span>{role.permissions.length} selected</span></div><div className="permission-groups compact">{groupedPermissions.map(([module, items]) => <section key={module}><h4>{module}</h4><div className="permission-matrix">{items.map(permission => <label className={role.permissions.includes(permission.code) ? 'selected' : ''} key={permission.code}><input type="checkbox" checked={role.permissions.includes(permission.code)} onChange={() => setRole({ ...role, permissions: toggle(role.permissions, permission.code) })} /><strong>{permission.name}</strong><small>{permission.code}</small></label>)}</div></section>)}</div></div>
      <div className="actions wide"><span /><Space><Button icon={<PlusOutlined />} onClick={() => setRole(role0)}>Reset</Button><Button type="primary" loading={saving} onClick={() => void saveRole()}>{role.id ? 'Update role' : 'Save role'}</Button></Space></div>
    </div>
  </Drawer>

  const renderAccessDrawer = () => <Drawer className="settings-master-drawer role-access-drawer" title={<div className="settings-drawer-title"><span>Access matrix</span><h3>{accessRole ? accessRole.name : 'Manage access'}</h3><p>Users receive combined permissions from every assigned role.</p></div>} open={Boolean(accessRole)} width="min(1080px, 96vw)" onClose={() => setAccessRole(null)} destroyOnClose extra={<Button type="primary" loading={savingAccess} onClick={() => void saveAccess()}>Save Changes</Button>}>
    <Tabs tabPosition="left" items={groupedPermissions.map(([module, items]) => {
      const selectedCount = items.filter(permission => accessPermissions.includes(permission.code)).length
      return {
        key: module,
        label: `${module} (${selectedCount}/${items.length})`,
        children: <div className="security-access-tab"><div className="security-access-tab-head"><b>{module}</b><Space><Button size="small" onClick={() => setAllAccess(items, true)}>Select all</Button><Button size="small" onClick={() => setAllAccess(items, false)}>Clear</Button></Space></div><div className="permission-matrix access-grid">{items.map(permission => <label className={accessPermissions.includes(permission.code) ? 'selected' : ''} key={permission.code}><input type="checkbox" checked={accessPermissions.includes(permission.code)} onChange={() => setAccessPermissions(current => toggle(current, permission.code))} /><strong>{permission.name}</strong><small>{permission.description || permission.code}</small></label>)}</div></div>
      }
    })} />
  </Drawer>

  const renderProvisionModal = () => <Modal className="employee-provision-modal" title="Import Employees as Users" open={provisionOpen} onCancel={() => setProvisionOpen(false)} footer={<Space><Button disabled={provisionLoading} onClick={() => setProvisionOpen(false)}>Close</Button><Button type="primary" icon={<ImportOutlined />} loading={provisionLoading} disabled={!selectedProvisionKeys.length || Boolean(provisionResult)} onClick={() => void runProvision()}>Create selected users</Button></Space>} width="min(1120px, 96vw)">
    <div className="employee-provision-controls">
      <label><span>Assign roles</span><div className="permission-matrix role-picker import-role-picker">{roles.map(item => <label className={provisionRoles.includes(item.code) ? 'selected' : ''} key={item.code}><input type="checkbox" checked={provisionRoles.includes(item.code)} onChange={() => toggleProvisionRole(item.code)} /><strong>{item.name}</strong><small>{item.code}</small></label>)}</div></label>
      <label><span>Temporary password</span><Input.Password value={provisionPassword} onChange={event => setProvisionPassword(event.target.value)} placeholder="Leave blank to auto-generate one password for this batch" /></label>
      <AntCheckbox checked={provisionMustChangePassword} onChange={event => setProvisionMustChangePassword(event.target.checked)}>Require password change on first login</AntCheckbox>
    </div>
    {provisionResult ? <div className="provision-result">
      <div className="access-credentials"><b>Employee import completed</b><span>{provisionResult.createdCount} created, {provisionResult.skippedCount} skipped. Temporary password: <strong>{provisionResult.temporaryPassword || provisionPassword || '-'}</strong></span></div>
      <DataTable rows={provisionResult.results} getRowId={(row, index) => row.userId || `${row.employeeId}-${index}`} exportFileName="employee-login-import-result" columns={[
        { key: 'employeeCode', label: 'Employee Code' },
        { key: 'employeeName', label: 'Employee' },
        { key: 'email', label: 'Login ID' },
        { key: 'status', label: 'Status', render: row => <Tag color={row.status === 'Created' ? 'green' : 'gold'}>{row.status}</Tag> },
        { key: 'message', label: 'Message', width: '240px' }
      ]} />
    </div> : <DataTable rows={provisionRows} getRowId={row => row.employeeId} emptyText={provisionLoading ? 'Loading employees...' : 'No employees are pending login creation.'} exportFileName="employee-login-provision-preview" rowSelection={{ selectedRowKeys: selectedProvisionKeys, onChange: keys => setSelectedProvisionKeys(keys.map(key => String(key))) }} columns={[
      { key: 'clientName', label: 'Client', width: '180px' },
      { key: 'employeeCode', label: 'Employee Code' },
      { key: 'employeeName', label: 'Employee', width: '190px' },
      { key: 'workEmail', label: 'Work Email', width: '220px' },
      { key: 'department', label: 'Department' },
      { key: 'designation', label: 'Designation' }
    ]} />}
  </Modal>

  const renderUsers = () => <section className="security-page-stack">
    <section className="component-table-head security-command-bar"><div><span className="eyebrow purple">User directory</span><b>{directoryClientId ? clientName(Number(directoryClientId)) : 'All clients'}</b><span>{visibleUsers.length} users shown / {unlinkedEmployees.length} employees awaiting login</span></div><Space className="settings-master-actions" size={8} wrap><label className="security-filter-field"><span>Filter by client</span><SearchSelect value={directoryClientId} onChange={setDirectoryClientId} options={[{ value: '', label: 'All clients' }, ...clients.map(client => ({ value: client.id, label: client.name }))]} /></label><Button icon={<DownloadOutlined />} onClick={downloadUserTemplate}>Template</Button><label className={`settings-upload-action ${!userTemplateDownloaded ? 'disabled' : ''}`} title={userTemplateDownloaded ? 'Upload Excel or CSV' : 'Download template first'}><input type="file" disabled={!userTemplateDownloaded} accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" onChange={event => { void uploadUserImport(event.target.files?.[0] ?? null); event.currentTarget.value = '' }} /><UploadOutlined />Bulk upload</label><Button icon={<ImportOutlined />} onClick={() => void openProvisionModal()}>Import Employees</Button><Button type="primary" icon={<PlusOutlined />} onClick={openNewUser}>New user</Button></Space></section>
    {createdCredentials && <Alert type="success" showIcon message="Login created" description={`Temporary password for ${createdCredentials.email}: ${createdCredentials.password}`} closable onClose={() => setCreatedCredentials(null)} />}
    <section className="card security-table-card"><div className="security-list-heading"><b>Users</b><span>Table-first user access register</span></div><DataTable rows={visibleUsers} getRowId={row => row.id} exportFileName="security-users" actions={row => <Button size="small" type="primary" onClick={() => editUser(row)}>Edit</Button>} columns={[
      { key: 'displayName', label: 'User', width: '190px' },
      { key: 'email', label: 'Email / Login ID', width: '220px' },
      { key: 'clientId', label: 'Client', value: row => clientName(row.clientId), width: '180px' },
      { key: 'employeeId', label: 'Employee Link', value: row => row.employeeId ? `Employee #${row.employeeId}` : 'Standalone' },
      { key: 'roles', label: 'Roles', value: row => row.roles.map(roleName).join(', '), render: row => <Space size={4} wrap>{row.roles.map(code => <Tag key={code}>{roleName(code)}</Tag>)}</Space>, width: '240px' },
      { key: 'isActive', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive', render: row => <Tag color={row.isActive ? 'green' : 'red'}>{row.isActive ? 'Active' : 'Inactive'}</Tag> },
      { key: 'mustChangePassword', label: 'Password Change', value: row => row.mustChangePassword ? 'Required' : 'Not required' }
    ]} /></section>
    <section className="card security-table-card"><div className="security-list-heading"><b>Employees awaiting login</b><span>Quick single-user provisioning</span></div><DataTable rows={unlinkedEmployees} getRowId={row => row.id} emptyText="No active employees are pending login creation." exportFileName="security-employees-without-login" actions={row => <Button size="small" onClick={() => provisionEmployee(row)}>Create user</Button>} columns={[
      { key: 'employeeCode', label: 'Employee Code' },
      { key: 'name', label: 'Employee', value: row => `${row.firstName} ${row.lastName}`.trim(), width: '190px' },
      { key: 'clientId', label: 'Client', value: row => clientName(row.clientId), width: '180px' },
      { key: 'workEmail', label: 'Work Email', width: '220px' },
      { key: 'department', label: 'Department' },
      { key: 'designation', label: 'Designation' }
    ]} /></section>
  </section>

  const renderRoles = () => <section className="security-page-stack">
    <section className="component-table-head security-command-bar"><div><span className="eyebrow purple">Role management</span><b>Access bundles</b><span>{roles.length} roles / {permissions.length} permissions</span></div><Button type="primary" icon={<PlusOutlined />} onClick={openNewRole}>New role</Button></section>
    <section className="card security-table-card"><div className="security-list-heading"><b>Roles</b><span>Manage access from row actions</span></div><DataTable rows={roles} getRowId={row => row.id} exportFileName="security-roles" actions={row => <Space size={6}><Button size="small" onClick={() => editRole(row)}>{row.isSystem ? 'Copy' : 'Edit'}</Button><Button size="small" type="primary" icon={<KeyOutlined />} onClick={() => openAccess(row)}>Manage Access</Button></Space>} columns={[
      { key: 'name', label: 'Role', width: '190px' },
      { key: 'code', label: 'Code' },
      { key: 'description', label: 'Description', width: '260px' },
      { key: 'permissions', label: 'Permissions', value: row => `${rolePermissions(row).length}`, render: row => `${rolePermissions(row).length} permissions` },
      { key: 'isSystem', label: 'Type', value: row => row.isSystem ? 'System' : 'Custom', render: row => <Tag color={row.isSystem ? 'blue' : 'purple'}>{row.isSystem ? 'System' : 'Custom'}</Tag> }
    ]} /></section>
  </section>

  const renderAudit = () => <section className="card security-table-card"><header><i className="blue">A</i><div><h3>Audit trail</h3><p>Recent identity and operational activity.</p></div></header><DataTable rows={auditLogs} exportFileName="audit-log" columns={[{ key: 'time', label: 'Time', value: log => new Date(log.createdAt).toLocaleString() }, { key: 'userEmail', label: 'User', value: log => log.userEmail || 'System' }, { key: 'action', label: 'Action' }, { key: 'statusCode', label: 'Status' }, { key: 'path', label: 'Path' }]} /></section>

  return <section className="security-module">
    <div className="security-hero"><div><span className="eyebrow purple">Identity Governance</span><h3>{initialTab}</h3><p>{msg}</p></div><div><strong>{users.length}</strong><span>identities</span></div><div><strong>{roles.length}</strong><span>roles</span></div><div><strong>{permissions.length}</strong><span>permissions</span></div></div>
    {initialTab === 'Users' && renderUsers()}
    {initialTab === 'Roles' && renderRoles()}
    {initialTab === 'Audit' && renderAudit()}
    {renderUserDrawer()}
    {renderRoleDrawer()}
    {renderAccessDrawer()}
    {renderProvisionModal()}
    <BulkUploadPreviewModal preview={userImportPreview} importing={userImporting} onCancel={() => { setUserImportPreview(emptyBulkUploadPreview); setUserImportData(null) }} onConfirm={() => void confirmUserImport()} />
    <BulkUploadProgressModal open={userUpload.open} title="Security user bulk upload" state={userUpload.state} percent={userUpload.percent} summary={userUpload.summary} onClose={() => setUserUpload(current => ({ ...current, open: false }))} />
  </section>
}
