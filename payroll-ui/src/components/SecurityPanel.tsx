import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { Alert, Button, Card as AntCard, Checkbox as AntCheckbox, Input, Modal, Space, Tag } from 'antd'
import { DownloadOutlined, ImportOutlined, KeyOutlined, PlusOutlined, UploadOutlined } from '@ant-design/icons'
import BulkUploadPreviewModal, { emptyBulkUploadPreview, type BulkUploadPreviewState } from './BulkUploadPreviewModal'
import BulkUploadProgressModal, { type BulkUploadState, type BulkUploadSummary } from './BulkUploadProgressModal'
import { deleteSecurityRole, deleteSecurityUser, loadEmployeeProvisionPreview, loadSecurityData, provisionEmployeeLogins, saveSecurityRole, saveSecurityUser } from '../services/securityService'
import type { AuditLog, AuthPermission, AuthRole, AuthUser, Client, Employee, EmployeeLoginProvisionPreview, EmployeeLoginProvisionResponse } from '../types/payroll'
import { parseImportPreviewFile, validateImportPreview, type ImportPreviewData, type ImportPreviewRules } from '../utils/importPreview'
import { downloadXlsx } from '../utils/xlsx'
import DataTable from './DataTable'
import SearchSelect from './SearchSelect'
import '../SecurityAccess.css'

const user0 = { id: 0, email: '', displayName: '', mobile: '', password: '', clientId: '', employeeId: '', isActive: true, mustChangePassword: true, roles: ['mss_manager'] }
const role0 = { id: 0, code: '', name: '', description: '', permissions: [] as string[], isSystem: false }
const userImportHeaders = ['Email', 'Display Name', 'Mobile', 'Employee Code', 'Roles', 'Temporary Password', 'Active', 'Must Change Password']
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
const InfoField = ({ label, help, children, className = '' }: { label: string; help?: string; children: ReactNode; className?: string }) => <label className={`info-field ${className}`.trim()}><span>{label}</span>{children}{help && <small>{help}</small>}</label>

export default function SecurityPanel({ initialTab = 'Users' }: { initialTab?: SecurityTab }) {
  const [users, setUsers] = useState<AuthUser[]>([]), [roles, setRoles] = useState<AuthRole[]>([]), [permissions, setPermissions] = useState<AuthPermission[]>([]), [auditLogs, setAuditLogs] = useState<AuditLog[]>([])
  const [clients, setClients] = useState<Client[]>([]), [employees, setEmployees] = useState<Employee[]>([])
  const [user, setUser] = useState(user0), [role, setRole] = useState(role0), [msg, setMsg] = useState(''), [directoryClientId, setDirectoryClientId] = useState('')
  const [userDrawerOpen, setUserDrawerOpen] = useState(false), [roleDrawerOpen, setRoleDrawerOpen] = useState(false), [createdCredentials, setCreatedCredentials] = useState<{ email: string; password: string } | null>(null), [saving, setSaving] = useState(false)
  const [resetMustChangePassword, setResetMustChangePassword] = useState(true)
  const [passwordPolicyOverride, setPasswordPolicyOverride] = useState<boolean | null>(null)
  const [accessRole, setAccessRole] = useState<AuthRole | null>(null), [accessPermissions, setAccessPermissions] = useState<string[]>([]), [savingAccess, setSavingAccess] = useState(false)
  const [accessModule, setAccessModule] = useState('')
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
  useEffect(() => {
    if (!accessRole) return
    if (!groupedPermissions.some(([module]) => module === accessModule)) setAccessModule(groupedPermissions[0]?.[0] ?? '')
  }, [accessRole, accessModule, groupedPermissions])

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

  const openNewUser = () => { setCreatedCredentials(null); setResetMustChangePassword(true); setPasswordPolicyOverride(null); setUser({ ...user0, clientId: directoryClientId }); setUserDrawerOpen(true) }
  const editUser = (selected: AuthUser) => {
    setCreatedCredentials(null)
    setResetMustChangePassword(true)
    setPasswordPolicyOverride(null)
    setUser({ id: selected.id, email: selected.email, displayName: selected.displayName, mobile: selected.mobile || '', password: '', clientId: selected.clientId ? String(selected.clientId) : '', employeeId: selected.employeeId ? String(selected.employeeId) : '', isActive: selected.isActive, mustChangePassword: selected.mustChangePassword, roles: selected.roles })
    setUserDrawerOpen(true)
  }
  const openNewRole = () => { setRole(role0); setRoleDrawerOpen(true) }
  const editRole = (selected: AuthRole) => {
    setRole({ id: selected.id, code: selected.code, name: selected.name, description: selected.description || '', permissions: rolePermissions(selected), isSystem: selected.isSystem })
    setRoleDrawerOpen(true)
  }
  const openAccess = (selected: AuthRole) => { setAccessRole(selected); setAccessPermissions(rolePermissions(selected)); setAccessModule(groupedPermissions[0]?.[0] ?? '') }

  const saveUser = async () => {
    if (!user.displayName.trim() || !user.email.trim()) { setMsg('Display name and email/login ID are required.'); return }
    if (user.id === 0 && !user.password.trim()) { setMsg('Temporary password is required for a new user.'); return }
    if (user.roles.length === 0) { setMsg('Select at least one role before saving the user.'); return }
    setSaving(true)
    try {
      const body = {
        id: user.id,
        email: user.email.trim(),
        displayName: user.displayName.trim(),
        mobile: user.mobile.trim(),
        password: user.password,
        clientId: user.clientId ? Number(user.clientId) : null,
        employeeId: user.employeeId ? Number(user.employeeId) : null,
        isActive: user.isActive,
        roles: user.roles,
        ...(user.id === 0
          ? { mustChangePassword: user.mustChangePassword }
          : user.password.trim()
            ? { mustChangePassword: resetMustChangePassword }
            : passwordPolicyOverride === null ? {} : { mustChangePassword: passwordPolicyOverride }),
      }
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
      const response = await saveSecurityRole({ id: role.id, code: role.code.trim(), name: role.name.trim(), description: role.description.trim(), permissions: role.permissions })
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

  const removeUser = async (selected: AuthUser) => {
    if (!window.confirm(`Delete user ${selected.displayName || selected.email}?`)) return
    const response = await deleteSecurityUser(selected.id)
    if (response.ok) {
      setMsg('User deleted successfully.')
      if (user.id === selected.id) {
        setUser(user0)
        setUserDrawerOpen(false)
      }
      await load()
      return
    }
    setMsg(response.error || 'Unable to delete user.')
  }

  const removeRole = async (selected: AuthRole) => {
    if (!selected.isSystem && !window.confirm(`Delete role ${selected.name}?`)) return
    const response = await deleteSecurityRole(selected.id)
    if (response.ok) {
      setMsg('Role deleted successfully.')
      if (role.id === selected.id) {
        setRole(role0)
        setRoleDrawerOpen(false)
      }
      if (accessRole?.id === selected.id) setAccessRole(null)
      await load()
      return
    }
    setMsg(response.error || 'Unable to delete role.')
  }

  const useEmployee = (employeeId: string) => {
    const selected = employees.find(employee => String(employee.id) === employeeId)
    setUser({ ...user, employeeId, clientId: selected ? String(selected.clientId) : user.clientId, email: selected?.workEmail || user.email, displayName: selected ? `${selected.firstName} ${selected.lastName}`.trim() : user.displayName, roles: user.roles.includes('employee') ? user.roles : [...user.roles, 'employee'] })
  }
  const provisionEmployee = (employee: Employee) => {
    setCreatedCredentials(null)
    setUser({ ...user0, employeeId: String(employee.id), clientId: String(employee.clientId), email: employee.workEmail, displayName: `${employee.firstName} ${employee.lastName}`.trim(), mobile: employee.personalDetails?.mobile || '', roles: ['employee'] })
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
      const selectedClientId = Number(directoryClientId || 0)
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
      if (employee && selectedClientId && employee.clientId !== selectedClientId) issues.push({ rowNumber, column: 'Employee Code', message: 'Employee Code belongs to another client.' })
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
    if (!directoryClientId) { setMsg('Select a client before downloading the user upload template.'); return }
    const sampleEmployee = unlinkedEmployees[0] || employees.find(employee => employee.clientId === Number(directoryClientId))
    downloadXlsx('security-user-import-template.xlsx', [
      { name: 'Users', rows: [userImportHeaders, ['manager@company.com', 'Reporting Manager', '9876543210', '', 'mss_manager', 'Temp@12345', 'TRUE', 'TRUE'], [sampleEmployee?.workEmail || 'employee-login@company.com', `${sampleEmployee?.firstName || 'Employee'} ${sampleEmployee?.lastName || 'User'}`.trim(), sampleEmployee?.personalDetails?.mobile || '', sampleEmployee?.employeeCode || 'EMP001', 'employee', 'Temp@12345', 'TRUE', 'TRUE']] },
      { name: 'Roles', rows: [['Role Code', 'Role Name'], ...roles.map(item => [item.code, item.name])] }
    ])
    setUserTemplateDownloaded(true)
  }
  const uploadUserImport = async (file: File | null) => {
    if (!file) return
    if (!directoryClientId) { setMsg('Select a client before uploading users.'); return }
    try {
      const data = await parseImportPreviewFile(file)
      const issues = validateImportPreview(data, userImportRules())
      setUserImportData(data)
      setUserImportPreview({ open: true, title: 'Security user bulk upload preview', fileName: file.name, headers: data.headers, rows: data.rows, issues })
    } catch {
      setUserUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors: ['Unable to read selected file. Use the downloaded XLSX/CSV template.'] } })
    }
  }
  const confirmUserImport = async (previewDraft?: BulkUploadPreviewState) => {
    const importData = previewDraft ? { headers: previewDraft.headers, rows: previewDraft.rows } : userImportData
    if (!importData) return
    const rows = importData.rows
    setUserImportPreview(emptyBulkUploadPreview)
    setUserImporting(true)
    setUserUpload({ open: true, state: 'uploading', percent: 1, summary: { totalRows: rows.length, completedRows: 0, inserted: 0, updated: 0, errors: [] } })
    let inserted = 0, updated = 0
    const errors: string[] = []
    for (let index = 0; index < rows.length; index++) {
      const rowNumber = index + 2
      const map = rowMap(importData, rows[index])
      const employeeCode = cell(map, 'Employee Code')
      const employee = employeeCode ? employeeByCode.get(employeeCode.toLowerCase()) : null
      const email = normalizeEmail(cell(map, 'Email') || employee?.workEmail)
      const existing = users.find(item => normalizeEmail(item.email) === email)
      const standaloneDefaultRole = roles.some(role => role.code === 'mss_manager') ? 'mss_manager' : 'employee'
      const rolesForRow = parseRoleCodes(cell(map, 'Roles'), employee ? ['employee'] : [standaloneDefaultRole])
      const tempPassword = cell(map, 'Temporary Password')
      const clientId = employee?.clientId || Number(directoryClientId) || existing?.clientId || null
      const body = {
        id: existing?.id ?? 0,
        email,
        displayName: cell(map, 'Display Name') || (employee ? `${employee.firstName} ${employee.lastName}`.trim() : existing?.displayName || ''),
        mobile: cell(map, 'Mobile') || existing?.mobile || '',
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

  const closeUserDrawer = () => { setUserDrawerOpen(false); setUser(user0); setResetMustChangePassword(true); setPasswordPolicyOverride(null); setCreatedCredentials(null) }
  const closeRoleDrawer = () => { setRoleDrawerOpen(false); setRole(role0) }
  const activeAccessGroup = groupedPermissions.find(([module]) => module === accessModule) ?? groupedPermissions[0]

  const renderUserDrawer = () => userDrawerOpen ? <div className="component-drawer-backdrop security-component-backdrop" onClick={closeUserDrawer}>
    <aside className="component-drawer security-component-drawer" role="dialog" aria-modal="true" aria-label={user.id ? 'Edit user' : 'Add user'} onClick={event => event.stopPropagation()}>
      <header><div><span className="eyebrow purple">User access</span><h3>{user.id ? 'Edit user' : 'Add user'}</h3><p>Create an employee-linked login or a standalone business user.</p></div><button type="button" aria-label="Close user drawer" onClick={closeUserDrawer}>x</button></header>
      <div className="component-drawer-form security-component-drawer-form">
        <InfoField label="User type" help="Choose employee-linked ESS access or a standalone business login."><SearchSelect value={user.roles.includes('employee') && user.employeeId ? 'employee' : 'business'} onChange={value => value === 'employee' ? setUser({ ...user, roles: ['employee'] }) : setUser({ ...user, employeeId: '', roles: ['mss_manager'] })} options={[{ value: 'business', label: 'Business user' }, { value: 'employee', label: 'Employee / ESS user' }]} /></InfoField>
        <InfoField label="Client scope" help="Leave blank for cross-client access."><SearchSelect value={user.clientId} onChange={value => setUser({ ...user, clientId: value, employeeId: '' })} options={[{ value: '', label: 'All clients' }, ...clients.map(client => ({ value: client.id, label: client.name }))]} /></InfoField>
        <InfoField label="Employee link" help="Employee Master records not yet linked to another login." className="wide"><SearchSelect value={user.employeeId} onChange={useEmployee} options={[{ value: '', label: 'No employee link' }, ...employeeOptions.map(employee => ({ value: employee.id, label: `${employee.firstName} ${employee.lastName} / ${employee.employeeCode} / ${employee.department}` }))]} /></InfoField>
        <InfoField label="Display name"><Input value={user.displayName} onChange={event => setUser({ ...user, displayName: event.target.value })} /></InfoField>
        <InfoField label="Email / Login ID"><Input value={user.email} onChange={event => setUser({ ...user, email: event.target.value })} /></InfoField>
        <InfoField label="Mobile number"><Input value={user.mobile} onChange={event => setUser({ ...user, mobile: event.target.value })} /></InfoField>
        <InfoField label={user.id ? 'Reset password' : 'Temporary password'} help={user.id ? 'Leave blank to preserve both the current password and its first-login status.' : 'Required for a new login.'}><Input.Password value={user.password} onChange={event => setUser({ ...user, password: event.target.value })} placeholder={user.id ? 'Leave blank to keep existing' : 'Enter temporary password'} /></InfoField>
        <InfoField label="Status"><SearchSelect value={user.isActive ? 'active' : 'inactive'} onChange={value => setUser({ ...user, isActive: value === 'active' })} options={[{ value: 'active', label: 'Active' }, { value: 'inactive', label: 'Inactive' }]} /></InfoField>
        {user.id === 0
          ? <InfoField label="Password policy"><AntCheckbox checked={user.mustChangePassword} onChange={event => setUser({ ...user, mustChangePassword: event.target.checked })}>Require change on first login</AntCheckbox></InfoField>
          : <InfoField label="Password policy" help={user.password.trim() ? 'This setting applies to the explicit password reset above.' : passwordPolicyOverride === null ? 'Role, client and status edits preserve the current password policy.' : 'This password-policy change will be applied when you update the user.'}>
            {user.password.trim()
              ? <AntCheckbox data-testid="require-change-after-reset" checked={resetMustChangePassword} onChange={event => setResetMustChangePassword(event.target.checked)}>Require change after reset</AntCheckbox>
              : user.mustChangePassword
                ? <AntCheckbox data-testid="clear-first-login-requirement" checked={passwordPolicyOverride === false} onChange={event => setPasswordPolicyOverride(event.target.checked ? false : null)}>Clear first-login requirement</AntCheckbox>
                : <AntCheckbox data-testid="require-change-next-login" checked={passwordPolicyOverride === true} onChange={event => setPasswordPolicyOverride(event.target.checked ? true : null)}>Require change on next login</AntCheckbox>}
            <Tag data-testid="password-policy-current" color={user.mustChangePassword ? 'gold' : 'green'}>{user.mustChangePassword ? 'Currently: change required' : 'Currently: password active'}</Tag>
          </InfoField>}
        <div className="security-drawer-section wide"><div className="security-mini-access"><b>Role assignment</b><span>{user.roles.length} selected</span></div><div className="permission-matrix role-picker">{roles.map(item => <label className={user.roles.includes(item.code) ? 'selected' : ''} key={item.code}><input type="checkbox" checked={user.roles.includes(item.code)} onChange={() => setUser({ ...user, roles: toggle(user.roles, item.code) })} /><strong>{item.name}</strong><small>{item.description}</small></label>)}</div></div>
      </div>
      <footer><button type="button" className="secondary" onClick={closeUserDrawer}>Cancel</button><button type="button" disabled={saving} onClick={() => void saveUser()}>{saving ? 'Saving...' : user.id ? 'Update user' : 'Create user'}</button></footer>
    </aside>
  </div> : null

  const renderRoleDrawer = () => roleDrawerOpen ? <div className="component-drawer-backdrop security-component-backdrop" onClick={closeRoleDrawer}>
    <aside className="component-drawer security-component-drawer" role="dialog" aria-modal="true" aria-label={role.id ? 'Edit role' : 'Add role'} onClick={event => event.stopPropagation()}>
      <header><div><span className="eyebrow purple">Role management</span><h3>{role.id ? role.isSystem ? 'Edit system role' : 'Edit role' : 'Add role'}</h3><p>Define access bundles, then tune detailed access with Manage Access.</p></div><button type="button" aria-label="Close role drawer" onClick={closeRoleDrawer}>x</button></header>
      <div className="component-drawer-form security-component-drawer-form">
        <InfoField label="Role code" help="Code is locked after creation."><Input value={role.code} disabled={role.id > 0} onChange={event => setRole({ ...role, code: event.target.value })} placeholder="payroll_viewer" /></InfoField>
        <InfoField label="Role name" help={role.isSystem ? 'System role names are maintained by the catalog.' : undefined}><Input value={role.name} disabled={role.isSystem} onChange={event => setRole({ ...role, name: event.target.value })} /></InfoField>
        <InfoField label="Description" className="wide"><Input value={role.description} disabled={role.isSystem} onChange={event => setRole({ ...role, description: event.target.value })} /></InfoField>
        <div className="security-drawer-section wide"><div className="security-mini-access"><b>Initial permissions</b><span>{role.permissions.length} selected</span></div><div className="permission-groups compact">{groupedPermissions.map(([module, items]) => <section key={module}><h4>{module}</h4><div className="permission-matrix">{items.map(permission => <label className={role.permissions.includes(permission.code) ? 'selected' : ''} key={permission.code}><input type="checkbox" checked={role.permissions.includes(permission.code)} onChange={() => setRole({ ...role, permissions: toggle(role.permissions, permission.code) })} /><strong>{permission.name}</strong><small>{permission.code}</small></label>)}</div></section>)}</div></div>
      </div>
      <footer><button type="button" className="secondary" onClick={closeRoleDrawer}>Cancel</button><button type="button" disabled={saving} onClick={() => void saveRole()}>{saving ? 'Saving...' : role.id ? 'Update role' : 'Save role'}</button></footer>
    </aside>
  </div> : null

  const renderAccessDrawer = () => accessRole ? <div className="component-drawer-backdrop security-component-backdrop" onClick={() => setAccessRole(null)}>
    <aside className="component-drawer security-component-drawer security-access-component-drawer" role="dialog" aria-modal="true" aria-label="Manage role access" onClick={event => event.stopPropagation()}>
      <header><div><span className="eyebrow purple">Access matrix</span><h3>{accessRole.name}</h3><p>Users receive combined permissions from every assigned role.</p></div><button type="button" aria-label="Close access drawer" onClick={() => setAccessRole(null)}>x</button></header>
      <div className="security-access-drawer-body">
        <nav className="security-access-side" aria-label="Permission modules">{groupedPermissions.map(([module, items]) => {
          const selectedCount = items.filter(permission => accessPermissions.includes(permission.code)).length
          return <button type="button" className={module === (activeAccessGroup?.[0] ?? '') ? 'active' : ''} key={module} onClick={() => setAccessModule(module)}><span>{module}</span><small>{selectedCount}/{items.length}</small></button>
        })}</nav>
        <section className="security-access-main">{activeAccessGroup && <><div className="security-access-tab-head"><b>{activeAccessGroup[0]}</b><Space><Button size="small" onClick={() => setAllAccess(activeAccessGroup[1], true)}>Select all</Button><Button size="small" onClick={() => setAllAccess(activeAccessGroup[1], false)}>Clear</Button></Space></div><div className="permission-matrix access-grid">{activeAccessGroup[1].map(permission => <label data-permission-code={permission.code} className={accessPermissions.includes(permission.code) ? 'selected' : ''} key={permission.code}><input type="checkbox" checked={accessPermissions.includes(permission.code)} onChange={() => setAccessPermissions(current => toggle(current, permission.code))} /><strong>{permission.name}</strong><small>{permission.code} · {permission.description}</small></label>)}</div></>}</section>
      </div>
      <footer><button type="button" className="secondary" onClick={() => setAccessRole(null)}>Cancel</button><button type="button" disabled={savingAccess} onClick={() => void saveAccess()}>{savingAccess ? 'Saving...' : 'Save Changes'}</button></footer>
    </aside>
  </div> : null

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
    {msg && <Alert className="security-message-alert" type={/unable|required|failed|cannot/i.test(msg) ? 'warning' : 'info'} showIcon message={msg} closable onClose={() => setMsg('')} />}
    <AntCard title="Users" size="small" className="settings-panel settings-table-panel security-table-panel">
      <div className="component-table-head security-table-head"><div><b>User directory</b><span>{directoryClientId ? clientName(Number(directoryClientId)) : 'Select a client for user upload'} / {visibleUsers.length} users shown / {unlinkedEmployees.length} employees awaiting login</span></div><Space className="settings-master-actions" size={8} wrap><label className="security-filter-field"><span>Filter by client</span><SearchSelect value={directoryClientId} onChange={value => { setDirectoryClientId(value); setUserTemplateDownloaded(false) }} options={[{ value: '', label: 'All clients' }, ...clients.map(client => ({ value: client.id, label: client.name }))]} /></label><Button className="settings-toolbar-secondary" icon={<DownloadOutlined />} disabled={!directoryClientId} title={directoryClientId ? 'Download client-wise template' : 'Select a client first'} onClick={downloadUserTemplate}>Template</Button><label className={`settings-upload-action ${!userTemplateDownloaded || !directoryClientId ? 'disabled' : ''}`} title={!directoryClientId ? 'Select a client first' : userTemplateDownloaded ? 'Upload Excel or CSV' : 'Download template first'}><input type="file" disabled={!userTemplateDownloaded || !directoryClientId} accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" onChange={event => { void uploadUserImport(event.target.files?.[0] ?? null); event.currentTarget.value = '' }} /><UploadOutlined />Bulk upload</label><Button className="settings-toolbar-secondary" icon={<ImportOutlined />} onClick={() => void openProvisionModal()}>Import Employees</Button><Button type="primary" icon={<PlusOutlined />} onClick={openNewUser}>New user</Button></Space></div>
      {createdCredentials && <Alert className="security-message-alert" type="success" showIcon message="Login created" description={`Temporary password for ${createdCredentials.email}: ${createdCredentials.password}`} closable onClose={() => setCreatedCredentials(null)} />}
      <DataTable rows={visibleUsers} getRowId={row => row.id} exportFileName="security-users" actions={row => <Space size={6}><Button size="small" type="primary" onClick={() => editUser(row)}>Edit</Button><Button size="small" danger onClick={() => void removeUser(row)}>Delete</Button></Space>} columns={[
      { key: 'displayName', label: 'User', width: '190px' },
      { key: 'email', label: 'Email / Login ID', width: '220px' },
      { key: 'mobile', label: 'Mobile', value: row => row.mobile || '-' },
      { key: 'clientId', label: 'Client', value: row => clientName(row.clientId), width: '180px' },
      { key: 'employeeId', label: 'Employee Link', value: row => row.employeeId ? `Employee #${row.employeeId}` : 'Standalone' },
      { key: 'roles', label: 'Roles', value: row => row.roles.map(roleName).join(', '), render: row => <Space size={4} wrap>{row.roles.map(code => <Tag key={code}>{roleName(code)}</Tag>)}</Space>, width: '240px' },
      { key: 'isActive', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive', render: row => <Tag color={row.isActive ? 'green' : 'red'}>{row.isActive ? 'Active' : 'Inactive'}</Tag> },
      { key: 'mustChangePassword', label: 'Password Change', value: row => row.mustChangePassword ? 'Required' : 'Not required' }
    ]} />
    </AntCard>
    <AntCard title="Employees awaiting login" size="small" className="settings-panel settings-table-panel security-table-panel">
      <div className="component-table-head security-table-head"><div><b>Employee login queue</b><span>Quick single-user provisioning from Employee Master.</span></div><Button icon={<ImportOutlined />} onClick={() => void openProvisionModal()}>Import Employees</Button></div>
      <DataTable rows={unlinkedEmployees} getRowId={row => row.id} emptyText="No active employees are pending login creation." exportFileName="security-employees-without-login" actions={row => <Button size="small" onClick={() => provisionEmployee(row)}>Create user</Button>} columns={[
      { key: 'employeeCode', label: 'Employee Code' },
      { key: 'name', label: 'Employee', value: row => `${row.firstName} ${row.lastName}`.trim(), width: '190px' },
      { key: 'clientId', label: 'Client', value: row => clientName(row.clientId), width: '180px' },
      { key: 'workEmail', label: 'Work Email', width: '220px' },
      { key: 'department', label: 'Department' },
      { key: 'designation', label: 'Designation' }
    ]} />
    </AntCard>
  </section>

  const renderRoles = () => <section className="security-page-stack">
    {msg && <Alert className="security-message-alert" type={/unable|required|failed|cannot/i.test(msg) ? 'warning' : 'info'} showIcon message={msg} closable onClose={() => setMsg('')} />}
    <AntCard title="Roles" size="small" className="settings-panel settings-table-panel security-table-panel">
      <div className="component-table-head security-table-head"><div><b>Access bundles</b><span>{roles.length} roles / {permissions.length} permissions. Manage access from row actions.</span></div><Button type="primary" icon={<PlusOutlined />} onClick={openNewRole}>New role</Button></div>
      <DataTable rows={roles} getRowId={row => row.id} exportFileName="security-roles" actions={row => <Space size={6}><Button size="small" type="primary" onClick={() => editRole(row)}>Edit</Button><Button size="small" icon={<KeyOutlined />} onClick={() => openAccess(row)}>Manage Access</Button><Button size="small" danger onClick={() => void removeRole(row)}>Delete</Button></Space>} columns={[
      { key: 'name', label: 'Role', width: '190px' },
      { key: 'code', label: 'Code' },
      { key: 'description', label: 'Description', width: '260px' },
      { key: 'permissions', label: 'Permissions', value: row => `${rolePermissions(row).length}`, render: row => `${rolePermissions(row).length} permissions` },
      { key: 'isSystem', label: 'Type', value: row => row.isSystem ? 'System' : 'Custom', render: row => <Tag color={row.isSystem ? 'blue' : 'purple'}>{row.isSystem ? 'System' : 'Custom'}</Tag> }
    ]} />
    </AntCard>
  </section>

  const renderAudit = () => <section className="security-page-stack">
    {msg && <Alert className="security-message-alert" type={/unable|required|failed|cannot/i.test(msg) ? 'warning' : 'info'} showIcon message={msg} closable onClose={() => setMsg('')} />}
    <AntCard title="Audit trail" size="small" className="settings-panel settings-table-panel security-table-panel">
      <div className="component-table-head security-table-head"><div><b>Recent activity</b><span>Identity and operational activity from the audit log.</span></div></div>
      <DataTable rows={auditLogs} exportFileName="audit-log" columns={[{ key: 'time', label: 'Time', value: log => new Date(log.createdAt).toLocaleString() }, { key: 'userEmail', label: 'User', value: log => log.userEmail || 'System' }, { key: 'action', label: 'Action' }, { key: 'statusCode', label: 'Status' }, { key: 'path', label: 'Path' }]} />
    </AntCard>
  </section>

  return <section className="security-module">
    {initialTab === 'Users' && renderUsers()}
    {initialTab === 'Roles' && renderRoles()}
    {initialTab === 'Audit' && renderAudit()}
    {renderUserDrawer()}
    {renderRoleDrawer()}
    {renderAccessDrawer()}
    {renderProvisionModal()}
    <BulkUploadPreviewModal preview={userImportPreview} importing={userImporting} onCancel={() => { setUserImportPreview(emptyBulkUploadPreview); setUserImportData(null) }} onConfirm={preview => void confirmUserImport(preview)} />
    <BulkUploadProgressModal open={userUpload.open} title="Security user bulk upload" state={userUpload.state} percent={userUpload.percent} summary={userUpload.summary} onClose={() => setUserUpload(current => ({ ...current, open: false }))} />
  </section>
}
