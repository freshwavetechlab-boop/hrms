import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Button, Card, Checkbox, Col, Drawer, Form, Input, InputNumber, Modal, Row, Select, Space, Switch, Tabs, Tag } from 'antd'
import DataTable from './DataTable'
import SearchSelect, { selectOptions } from './SearchSelect'
import { useToast } from './ToastProvider'
import { getClients } from '../services/payrollService'
import { configureGoogleDrive, connectGoogleDrive, getAttachmentAttributes, getAttachmentConfigurations, getAttachmentStorageServers, getAttachmentTargets, getGoogleDriveSetup, saveAttachmentAttribute, saveAttachmentConfiguration, saveAttachmentStorageServer, testAttachmentStorageServer } from '../services/attachmentService'
import { apiUrl } from '../services/apiClient'
import type { AttachmentAttribute, AttachmentFieldConfiguration, AttachmentStorageServer, AttachmentStorageType, AttachmentTargetOption, Client, GoogleDriveSetup } from '../types/payroll'
import './AttachmentSettings.css'

type DrawerMode = 'attribute' | 'configuration' | 'storage' | null

const attribute0: AttachmentAttribute = {
  id: 0, clientId: 0, clientName: 'Global', attributeCode: '', attributeName: '', description: '', dataClassification: 'Internal',
  requiresDocumentNumber: false, requiresIssueDate: false, requiresExpiryDate: false, isActive: true
}
const configuration0: AttachmentFieldConfiguration = {
  id: 0, clientId: 0, clientName: 'Global', attachmentAttributeId: 0, attributeCode: '', attributeName: '', dataClassification: 'Internal',
  requiresDocumentNumber: false, requiresIssueDate: false, requiresExpiryDate: false, moduleCode: 'EMPLOYEE',
  formCode: 'EMPLOYEE_CREATE_EDIT', sectionCode: 'DOCUMENTS', fieldKey: '', fieldLabel: '', helpText: '', isRequired: false,
  allowMultiple: false, minimumFileCount: 0, maximumFileCount: 1, allowedExtensionsJson: '["pdf","jpg","jpeg","png"]',
  allowedMimeTypesJson: '["application/pdf","image/jpeg","image/png"]', maximumFileSizeBytes: 5 * 1024 * 1024, maximumTotalSizeBytes: null,
  ownerCanView: true, ownerCanUpload: false, ownerCanReplace: false, ownerCanDelete: false, requiresVerification: false,
  versioningEnabled: true, requirementScope: 'NewEntitiesOnly', displayOrder: 100, effectiveFromUtc: null, effectiveUntilUtc: null, isActive: true
}
const storage0: AttachmentStorageServer = {
  id: 0, serverCode: '', serverName: '', storageType: 'LocalFileSystem', basePath: '', serviceUrl: '', credential: '', hasCredential: false,
  isReadEnabled: true, isWriteEnabled: true, isDefaultWriteServer: false, priority: 100, maximumCapacityBytes: null,
  warningCapacityPercent: 85, isActive: true, lastHealthCheckStatus: 'Not checked', lastHealthCheckMessage: '', linkedAttachmentCount: 0
}
const extensionOptions = ['pdf', 'jpg', 'jpeg', 'png', 'docx']
const mimeByExtension: Record<string, string> = {
  pdf: 'application/pdf',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  png: 'image/png',
  docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
}
const jsonList = (value: string) => {
  try { return JSON.parse(value) as string[] } catch { return value.split(',').map(item => item.trim()).filter(Boolean) }
}
const dateInput = (value?: string | null) => value ? String(value).slice(0, 10) : ''
const mb = (bytes?: number | null) => bytes ? Number((bytes / 1024 / 1024).toFixed(2)) : 0
const bytes = (megabytes?: number | null) => Math.max(0, Number(megabytes || 0) * 1024 * 1024)
const statusColor = (status: string) => status.toLowerCase() === 'healthy' ? 'green' : status.toLowerCase().includes('not') ? 'default' : 'red'
const googleOAuthMessageType = 'frevo:google-drive-oauth'
const googleSetupLinks = {
  accountSecurity: 'https://myaccount.google.com/security',
  authPlatform: 'https://console.cloud.google.com/auth',
  clients: 'https://console.cloud.google.com/auth/clients',
  audience: 'https://console.cloud.google.com/auth/audience',
  driveApi: 'https://console.cloud.google.com/apis/library/drive.googleapis.com'
}
const storageTypeLabels: Record<AttachmentStorageType, string> = {
  LocalFileSystem: 'API server local folder',
  MountedFileSystem: 'Mounted volume / network share',
  HttpFileServer: 'Remote file server API',
  GoogleDrive: 'Google Drive'
}
const isGoogleDriveConnected = (row?: AttachmentStorageServer) => Boolean(
  row?.googleAccountEmail || row?.googleConnectionStatus?.trim().toLowerCase() === 'connected'
)
const isGoogleOAuthConfigured = (row?: AttachmentStorageServer) => Boolean(row?.googleOAuthConfigured || isGoogleDriveConnected(row))
const googleConnectionLabel = (row: AttachmentStorageServer) => row.googleConnectionStatus?.trim()
  || (isGoogleDriveConnected(row) ? 'Connected' : isGoogleOAuthConfigured(row) ? 'Ready to connect' : 'Setup required')
const isActiveWriteTarget = (row: AttachmentStorageServer) => row.isDefaultWriteServer && row.isActive && row.isWriteEnabled
const storageLocationLabel = (row: AttachmentStorageServer) => {
  if (row.storageType === 'HttpFileServer') return row.serviceUrl
  if (row.storageType === 'GoogleDrive') return row.googleFolderName || row.basePath || 'Frevo HRMS Attachments'
  return row.basePath
}
const storageTargetLabel = (row: AttachmentStorageServer) => {
  const values = [row.serverName, storageTypeLabels[row.storageType], storageLocationLabel(row)].filter(Boolean)
  return values.filter((value, index) =>
    values.findIndex(candidate => candidate.toLowerCase() === value.toLowerCase()) === index
  ).join(' / ')
}

export default function AttachmentSettings({ mode = 'attachments' }: { mode?: 'attachments' | 'storage' }) {
  const notify = useToast()
  const [clients, setClients] = useState<Client[]>([])
  const [attributes, setAttributes] = useState<AttachmentAttribute[]>([])
  const [configurations, setConfigurations] = useState<AttachmentFieldConfiguration[]>([])
  const [servers, setServers] = useState<AttachmentStorageServer[]>([])
  const [targets, setTargets] = useState<AttachmentTargetOption[]>([])
  const [drawer, setDrawer] = useState<DrawerMode>(null)
  const [attribute, setAttribute] = useState<AttachmentAttribute>(attribute0)
  const [configuration, setConfiguration] = useState<AttachmentFieldConfiguration>(configuration0)
  const [server, setServer] = useState<AttachmentStorageServer>(storage0)
  const [testingServerId, setTestingServerId] = useState(0)
  const [connectingGoogleDrive, setConnectingGoogleDrive] = useState(false)
  const [googleSetupOpen, setGoogleSetupOpen] = useState(false)
  const [googleGuideOpen, setGoogleGuideOpen] = useState(false)
  const [googleSetup, setGoogleSetup] = useState<GoogleDriveSetup | null>(null)
  const [loadingGoogleSetup, setLoadingGoogleSetup] = useState(false)
  const [googleCredentialFile, setGoogleCredentialFile] = useState<File | null>(null)
  const [configuringGoogleDrive, setConfiguringGoogleDrive] = useState(false)
  const [googleFileInputKey, setGoogleFileInputKey] = useState(0)
  const googlePopupRef = useRef<Window | null>(null)
  const googlePopupTimerRef = useRef<number | null>(null)
  const derivedGoogleCallbackUrl = apiUrl('/api/public/attachment-storage-servers/google/callback')
  const googleCallbackUrl = googleSetup?.callbackUrl || derivedGoogleCallbackUrl
  const googleCloudCredentialsUrl = googleSetup?.googleCloudCredentialsUrl || 'https://console.cloud.google.com/apis/credentials'

  const load = useCallback(async () => {
    if (mode === 'storage') {
      setServers(await getAttachmentStorageServers())
      return
    }
    const [clientRows, attributeRows, configurationRows, targetRows] = await Promise.all([
      getClients(), getAttachmentAttributes(), getAttachmentConfigurations(), getAttachmentTargets()
    ])
    setClients(clientRows.filter(item => item.isActive))
    setAttributes(attributeRows)
    setConfigurations(configurationRows)
    setTargets(targetRows)
  }, [mode])
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  const stopGooglePopupTracking = useCallback((closePopup = false) => {
    if (googlePopupTimerRef.current !== null) {
      window.clearInterval(googlePopupTimerRef.current)
      googlePopupTimerRef.current = null
    }
    if (closePopup && googlePopupRef.current && !googlePopupRef.current.closed) googlePopupRef.current.close()
    googlePopupRef.current = null
  }, [])

  useEffect(() => {
    const handleGoogleOAuthMessage = (event: MessageEvent<unknown>) => {
      const popup = googlePopupRef.current
      if (!popup || event.source !== popup || event.origin !== new URL(apiUrl('/')).origin) return
      const data = event.data as { type?: string; success?: boolean; message?: string } | null
      if (!data || data.type !== googleOAuthMessageType) return
      stopGooglePopupTracking(true)
      setConnectingGoogleDrive(false)
      if (data.success) {
        notify(data.message || 'Google Drive connected and selected as the active write target.', 'success')
        void load()
      } else {
        notify(data.message || 'Google Drive connection was not completed.', 'error')
      }
    }
    window.addEventListener('message', handleGoogleOAuthMessage)
    return () => {
      window.removeEventListener('message', handleGoogleOAuthMessage)
      stopGooglePopupTracking(true)
    }
  }, [load, notify, stopGooglePopupTracking])

  const clientOptions = [{ value: 0, label: 'Global / all clients' }, ...clients.map(item => ({ value: item.id, label: item.name }))]
  const targetOptions = targets.map(item => ({ value: `${item.moduleCode}|${item.formCode}`, label: `${item.moduleName} / ${item.formName}` }))
  const availableAttributes = attributes.filter(item => item.isActive && (item.clientId === 0 || item.clientId === configuration.clientId))
  const selectedTarget = `${configuration.moduleCode}|${configuration.formCode}`
  const selectedExtensions = jsonList(configuration.allowedExtensionsJson)
  const availableStorageTypes = [
    { value: 'LocalFileSystem', label: 'API server local folder' },
    { value: 'MountedFileSystem', label: 'Mounted volume / network share' },
    { value: 'HttpFileServer', label: 'Remote file server API' },
    { value: 'GoogleDrive', label: 'Google Drive (one-click connect)' }
  ]

  const openAttribute = (row?: AttachmentAttribute) => { setAttribute(row ? { ...row } : { ...attribute0 }); setDrawer('attribute') }
  const openConfiguration = (row?: AttachmentFieldConfiguration) => { setConfiguration(row ? { ...row } : { ...configuration0 }); setDrawer('configuration') }
  const openStorage = (row?: AttachmentStorageServer) => { setServer(row ? { ...row, credential: '' } : { ...storage0 }); setDrawer('storage') }
  const closeDrawer = () => setDrawer(null)
  const clearGoogleCredentialFile = () => {
    setGoogleCredentialFile(null)
    setGoogleFileInputKey(value => value + 1)
  }
  const loadGoogleSetup = async () => {
    setLoadingGoogleSetup(true)
    const setup = await getGoogleDriveSetup()
    setGoogleSetup(setup)
    setLoadingGoogleSetup(false)
  }
  const openGoogleSetup = () => {
    closeDrawer()
    clearGoogleCredentialFile()
    setGoogleSetup(null)
    setGoogleGuideOpen(false)
    setGoogleSetupOpen(true)
    void loadGoogleSetup()
  }
  const closeGoogleSetup = () => {
    if (configuringGoogleDrive) return
    setGoogleGuideOpen(false)
    setGoogleSetupOpen(false)
    clearGoogleCredentialFile()
  }
  const selectGoogleCredentialFile = (file?: File) => {
    if (!file) {
      clearGoogleCredentialFile()
      return
    }
    if (!file.name.toLowerCase().endsWith('.json')) {
      clearGoogleCredentialFile()
      notify('Choose the OAuth credential JSON downloaded from Google Cloud Credentials.', 'error')
      return
    }
    if (file.size > 64 * 1024) {
      clearGoogleCredentialFile()
      notify('The OAuth credential JSON must be 64 KB or smaller.', 'error')
      return
    }
    setGoogleCredentialFile(file)
  }
  const saveGoogleSetup = async () => {
    if (!googleCredentialFile) {
      notify('Choose the downloaded client_secret.json file first.', 'error')
      return
    }
    setConfiguringGoogleDrive(true)
    const configuredServerId = googleSetup?.storageServerId
      || servers.find(item => item.storageType === 'GoogleDrive')?.id
      || null
    const response = await configureGoogleDrive(googleCredentialFile, configuredServerId)
    setConfiguringGoogleDrive(false)
    if (!response.ok) {
      notify(response.error || 'Google OAuth setup could not be saved.', 'error')
      return
    }
    setGoogleSetup(response.data)
    setGoogleGuideOpen(false)
    setGoogleSetupOpen(false)
    clearGoogleCredentialFile()
    await load()
    notify('Google OAuth setup saved. Click Connect Google Drive and choose the account to use.', 'success')
  }
  const copyGoogleCallbackUrl = async () => {
    try {
      await navigator.clipboard.writeText(googleCallbackUrl)
      notify('Google OAuth callback URL copied.', 'success')
    } catch {
      notify('Select and copy the callback URL shown in the field.', 'info')
    }
  }
  const changeStorageType = (storageType: AttachmentStorageType) => setServer(current => ({
    ...current,
    storageType,
    ...(storageType === 'GoogleDrive' ? {
      serverCode: current.serverCode || 'GOOGLE_DRIVE',
      serverName: current.serverName || 'Google Drive',
      basePath: '',
      serviceUrl: '',
      credential: ''
    } : {})
  }))

  const startGoogleDriveConnect = async () => {
    const configuredServer = servers.find(item => item.storageType === 'GoogleDrive')
    if (!isGoogleOAuthConfigured(configuredServer)) {
      openGoogleSetup()
      return
    }
    if (googlePopupRef.current && !googlePopupRef.current.closed) {
      googlePopupRef.current.focus()
      return
    }

    const popupWidth = 520
    const popupHeight = 680
    const popupLeft = Math.max(0, Math.round(window.screenX + (window.outerWidth - popupWidth) / 2))
    const popupTop = Math.max(0, Math.round(window.screenY + (window.outerHeight - popupHeight) / 2))
    const popup = window.open(
      'about:blank',
      'frevo-google-drive-oauth',
      `popup=yes,width=${popupWidth},height=${popupHeight},left=${popupLeft},top=${popupTop},resizable=yes,scrollbars=yes`
    )
    if (!popup) {
      notify('Please allow popups for this portal, then click Connect Google Drive again.', 'error')
      return
    }

    closeDrawer()
    googlePopupRef.current = popup
    setConnectingGoogleDrive(true)
    const response = await connectGoogleDrive()
    if (!response.ok || !response.data.authorizationUrl) {
      stopGooglePopupTracking(true)
      setConnectingGoogleDrive(false)
      notify(response.error || 'Google Drive connection could not be started.', 'error')
      return
    }

    try {
      const authorizationUrl = new URL(response.data.authorizationUrl, apiUrl('/'))
      if (authorizationUrl.protocol !== 'https:' && authorizationUrl.protocol !== 'http:') throw new Error('Unsupported authorization URL.')
      popup.location.replace(authorizationUrl.toString())
      popup.focus()
    } catch (error) {
      stopGooglePopupTracking(true)
      setConnectingGoogleDrive(false)
      notify(error instanceof Error ? error.message : 'Invalid Google authorization URL.', 'error')
      return
    }

    googlePopupTimerRef.current = window.setInterval(() => {
      if (!popup.closed) return
      stopGooglePopupTracking()
      setConnectingGoogleDrive(false)
      window.setTimeout(() => void load(), 250)
    }, 600)
  }

  const saveAttribute = async () => {
    const response = await saveAttachmentAttribute(attribute)
    if (!response.ok) return
    closeDrawer()
    await load()
  }
  const saveConfiguration = async () => {
    const response = await saveAttachmentConfiguration(configuration)
    if (!response.ok) return
    closeDrawer()
    await load()
  }
  const saveServer = async () => {
    const response = await saveAttachmentStorageServer(server)
    if (!response.ok) return
    closeDrawer()
    await load()
  }
  const testServer = async (row: AttachmentStorageServer) => {
    setTestingServerId(row.id)
    const result = await testAttachmentStorageServer(row.id)
    setTestingServerId(0)
    if (result.ok) notify(result.data.message || 'Storage server is healthy.', 'success')
    else notify(result.error || result.data.message || 'Storage server test failed.', 'error')
    await load()
  }

  const setExtensions = (values: string[]) => setConfiguration(current => ({
    ...current,
    allowedExtensionsJson: JSON.stringify(values),
    allowedMimeTypesJson: JSON.stringify(Array.from(new Set(values.map(value => mimeByExtension[value]).filter(Boolean))))
  }))
  const changeTarget = (value: string) => {
    const [moduleCode, formCode] = value.split('|')
    setConfiguration(current => ({ ...current, moduleCode, formCode }))
  }
  const storageSummary = useMemo(() => {
    const active = servers.filter(item => item.isActive)
    return `${active.length} active / ${servers.filter(item => item.isReadEnabled).length} readable / ${servers.filter(item => item.isDefaultWriteServer).length} write target`
  }, [servers])
  const activeWriteServer = useMemo(
    () => servers.find(isActiveWriteTarget),
    [servers]
  )
  const googleDriveServer = useMemo(() => servers.find(item => item.storageType === 'GoogleDrive'), [servers])

  return <section className="attachment-settings">
    <Card size="small" className="settings-panel settings-table-panel" title={mode === 'storage' ? 'Storage Servers' : 'Global Attachment Configuration'}>
      <Tabs items={[
        {
          key: 'attributes', label: 'Attachment Attributes', children: <>
            <Header title="Attachment attribute master" text="Define reusable document types. Client-specific attributes can coexist with global attributes." action="Add attribute" onClick={() => openAttribute()} />
            <DataTable rows={attributes} exportFileName="attachment-attributes" actions={row => <Button size="small" type="primary" onClick={() => openAttribute(row)}>Edit</Button>} columns={[
              { key: 'clientName', label: 'Scope' },
              { key: 'attributeCode', label: 'Code' },
              { key: 'attributeName', label: 'Attribute' },
              { key: 'dataClassification', label: 'Classification', render: row => <Tag color={row.dataClassification === 'Restricted' ? 'red' : row.dataClassification === 'Confidential' ? 'orange' : 'blue'}>{row.dataClassification}</Tag> },
              { key: 'metadata', label: 'Required metadata', value: row => [row.requiresDocumentNumber ? 'Number' : '', row.requiresIssueDate ? 'Issue date' : '', row.requiresExpiryDate ? 'Expiry' : ''].filter(Boolean).join(', ') || 'None' },
              { key: 'isActive', label: 'Status', render: row => <Tag color={row.isActive ? 'green' : 'default'}>{row.isActive ? 'Active' : 'Inactive'}</Tag> }
            ]} />
          </>
        },
        {
          key: 'forms', label: 'Form Fields', children: <>
            <Header title="Module and form attachment fields" text="Control where a document appears, allowed formats, size, multiplicity and employee permissions." action="Add form field" onClick={() => openConfiguration()} />
            <DataTable rows={configurations} exportFileName="attachment-form-configurations" actions={row => <Button size="small" type="primary" onClick={() => openConfiguration(row)}>Edit</Button>} columns={[
              { key: 'clientName', label: 'Scope' },
              { key: 'target', label: 'Module / form', value: row => `${row.moduleCode} / ${row.formCode}` },
              { key: 'fieldLabel', label: 'Field' },
              { key: 'attributeName', label: 'Attribute' },
              { key: 'isRequired', label: 'Required', value: row => row.isRequired ? 'Yes' : 'No' },
              { key: 'files', label: 'Files', value: row => row.allowMultiple ? `Multiple / ${row.maximumFileCount}` : 'Single' },
              { key: 'formats', label: 'Formats', value: row => jsonList(row.allowedExtensionsJson).join(', ').toUpperCase() },
              { key: 'maximumFileSizeBytes', label: 'Max size', value: row => `${mb(row.maximumFileSizeBytes)} MB` },
              { key: 'isActive', label: 'Status', render: row => <Tag color={row.isActive ? 'green' : 'default'}>{row.isActive ? 'Active' : 'Inactive'}</Tag> }
            ]} />
          </>
        },
        {
          key: 'storage', label: 'Storage Servers', children: <>
            <Header
              title="Attachment storage servers"
              text={`${storageSummary}. Existing files remain linked to their original server; new files use the default write target.`}
              actions={<Button type="primary" onClick={() => openStorage()}>Add storage server</Button>}
            />
            {activeWriteServer && <div className="attachment-active-write-target" role="status">
              <span className="attachment-active-write-dot" aria-hidden="true" />
              <div>
                <strong>Active attachment write target</strong>
                <span>{storageTargetLabel(activeWriteServer)}</span>
              </div>
              <Tag color="green">Active</Tag>
            </div>}
            <DataTable
              rows={servers}
              rowClassName={row => isActiveWriteTarget(row) ? 'attachment-storage-active-row' : ''}
              exportFileName="attachment-storage-servers"
              actions={row => <Space size={4} className="attachment-storage-row-actions">
                <Button size="small" onClick={() => void testServer(row)} loading={testingServerId === row.id}>Test</Button>
                {row.storageType === 'GoogleDrive' && <Button
                  size="small"
                  type="primary"
                  data-testid="google-drive-row-action"
                  loading={connectingGoogleDrive}
                  onClick={() => isGoogleOAuthConfigured(row) ? void startGoogleDriveConnect() : openGoogleSetup()}
                >
                  {!isGoogleOAuthConfigured(row) ? 'Set up Drive' : isGoogleDriveConnected(row) ? 'Reconnect' : 'Connect'}
                </Button>}
                <Button size="small" type={row.storageType === 'GoogleDrive' ? 'default' : 'primary'} onClick={() => openStorage(row)}>Edit</Button>
              </Space>}
              columns={[
              {
                key: 'serverName', label: 'Server', width: '210px', render: row => <div className="attachment-storage-server-cell">
                  <div className="attachment-storage-server-heading">
                    <strong>{row.serverName}</strong>
                    {isActiveWriteTarget(row)
                      ? <Tag color="green">Active write target</Tag>
                      : row.isDefaultWriteServer && <Tag color="orange">Write target unavailable</Tag>}
                  </div>
                  {row.storageType === 'GoogleDrive' && <span>{row.googleAccountEmail || googleConnectionLabel(row)}</span>}
                </div>
              },
              { key: 'storageType', label: 'Type', value: row => storageTypeLabels[row.storageType] },
              {
                key: 'location',
                label: 'Location',
                value: storageLocationLabel,
                render: row => row.storageType === 'GoogleDrive' && row.googleFolderUrl
                  ? <a href={row.googleFolderUrl} target="_blank" rel="noopener noreferrer">{storageLocationLabel(row)}</a>
                  : storageLocationLabel(row)
              },
              { key: 'access', label: 'Access', value: row => `${row.isReadEnabled ? 'Read' : '-'} / ${row.isWriteEnabled ? 'Write' : '-'}` },
              { key: 'linkedAttachmentCount', label: 'Files' },
              {
                key: 'lastHealthCheckStatus',
                label: 'Health',
                render: row => <div className="attachment-storage-health">
                  <Tag color={statusColor(row.lastHealthCheckStatus)} title={row.lastHealthCheckMessage}>{row.lastHealthCheckStatus}</Tag>
                  {row.storageType === 'GoogleDrive' && <span>{googleConnectionLabel(row)}</span>}
                </div>
              },
              { key: 'isActive', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
            ]} />
          </>
        }
      ].filter(item => mode === 'storage' ? item.key === 'storage' : item.key !== 'storage')} />
    </Card>

    <Drawer className="settings-master-drawer attachment-settings-drawer" width="min(900px, 96vw)" destroyOnClose open={drawer !== null} title={drawer === 'attribute' ? `${attribute.id ? 'Edit' : 'Add'} attachment attribute` : drawer === 'configuration' ? `${configuration.id ? 'Edit' : 'Add'} attachment field` : `${server.id ? 'Edit' : 'Add'} storage server`} onClose={closeDrawer} footer={<Space><Button onClick={closeDrawer}>Cancel</Button><Button type="primary" onClick={() => drawer === 'attribute' ? void saveAttribute() : drawer === 'configuration' ? void saveConfiguration() : drawer === 'storage' && server.storageType === 'GoogleDrive' && !server.id ? openGoogleSetup() : void saveServer()}>{drawer === 'storage' && server.storageType === 'GoogleDrive' && !server.id ? 'Set up Google Drive' : 'Save'}</Button></Space>}>
      {drawer === 'attribute' && <Form layout="vertical" requiredMark={false}><Row gutter={12}>
        <Col xs={24} md={12}><Form.Item label="Scope"><SearchSelect value={attribute.clientId} onChange={value => setAttribute({ ...attribute, clientId: Number(value) })} options={clientOptions} /></Form.Item></Col>
        <Col xs={24} md={12}><Form.Item label="Classification"><Select value={attribute.dataClassification} onChange={value => setAttribute({ ...attribute, dataClassification: value })} options={['Public', 'Internal', 'Confidential', 'Restricted'].map(value => ({ value, label: value }))} /></Form.Item></Col>
        <Col xs={24} md={10}><Form.Item label="Attribute code" required><Input value={attribute.attributeCode} onChange={event => setAttribute({ ...attribute, attributeCode: event.target.value.toUpperCase().replace(/[^A-Z0-9_]/g, '_') })} placeholder="AADHAAR" /></Form.Item></Col>
        <Col xs={24} md={14}><Form.Item label="Attribute name" required><Input value={attribute.attributeName} onChange={event => setAttribute({ ...attribute, attributeName: event.target.value })} placeholder="Aadhaar Card" /></Form.Item></Col>
        <Col span={24}><Form.Item label="Description"><Input.TextArea rows={3} value={attribute.description} onChange={event => setAttribute({ ...attribute, description: event.target.value })} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Document number"><Switch checked={attribute.requiresDocumentNumber} onChange={value => setAttribute({ ...attribute, requiresDocumentNumber: value })} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Issue date"><Switch checked={attribute.requiresIssueDate} onChange={value => setAttribute({ ...attribute, requiresIssueDate: value })} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Expiry date"><Switch checked={attribute.requiresExpiryDate} onChange={value => setAttribute({ ...attribute, requiresExpiryDate: value })} /></Form.Item></Col>
        <Col span={24}><Form.Item label="Active"><Switch checked={attribute.isActive} onChange={value => setAttribute({ ...attribute, isActive: value })} /></Form.Item></Col>
      </Row></Form>}

      {drawer === 'configuration' && <Form layout="vertical" requiredMark={false}><Row gutter={12}>
        <Col xs={24} md={10}><Form.Item label="Scope"><SearchSelect value={configuration.clientId} onChange={value => setConfiguration({ ...configuration, clientId: Number(value), attachmentAttributeId: 0 })} options={clientOptions} /></Form.Item></Col>
        <Col xs={24} md={14}><Form.Item label="Module / form" required><SearchSelect value={selectedTarget} onChange={value => changeTarget(String(value))} options={targetOptions} /></Form.Item></Col>
        <Col xs={24} md={12}><Form.Item label="Attachment attribute" required><SearchSelect value={configuration.attachmentAttributeId} onChange={value => setConfiguration({ ...configuration, attachmentAttributeId: Number(value) })} options={selectOptions(availableAttributes.map(item => ({ value: item.id, label: `${item.attributeName} (${item.attributeCode})${item.clientId ? '' : ' / Global'}` })), 'Select attribute', 0)} /></Form.Item></Col>
        <Col xs={24} md={12}><Form.Item label="Section code"><Input value={configuration.sectionCode} onChange={event => setConfiguration({ ...configuration, sectionCode: event.target.value.toUpperCase() })} /></Form.Item></Col>
        <Col xs={24} md={10}><Form.Item label="Field key" required><Input value={configuration.fieldKey} onChange={event => setConfiguration({ ...configuration, fieldKey: event.target.value.toUpperCase().replace(/[^A-Z0-9_]/g, '_') })} placeholder="AADHAAR_DOCUMENT" /></Form.Item></Col>
        <Col xs={24} md={14}><Form.Item label="Field label" required><Input value={configuration.fieldLabel} onChange={event => setConfiguration({ ...configuration, fieldLabel: event.target.value })} /></Form.Item></Col>
        <Col span={24}><Form.Item label="Help text"><Input value={configuration.helpText} onChange={event => setConfiguration({ ...configuration, helpText: event.target.value })} /></Form.Item></Col>
        <Col span={24}><Form.Item label="Allowed formats"><Checkbox.Group options={extensionOptions.map(value => ({ label: value.toUpperCase(), value }))} value={selectedExtensions} onChange={values => setExtensions(values as string[])} /></Form.Item></Col>
        <Col xs={12} md={6}><Form.Item label="Maximum size (MB)"><InputNumber min={0.1} max={25} step={0.5} value={mb(configuration.maximumFileSizeBytes)} onChange={value => setConfiguration({ ...configuration, maximumFileSizeBytes: bytes(value) })} /></Form.Item></Col>
        <Col xs={12} md={6}><Form.Item label="Multiple files"><Switch checked={configuration.allowMultiple} onChange={value => setConfiguration({ ...configuration, allowMultiple: value, maximumFileCount: value ? Math.max(2, configuration.maximumFileCount) : 1 })} /></Form.Item></Col>
        <Col xs={12} md={6}><Form.Item label="Maximum files"><InputNumber min={1} max={100} disabled={!configuration.allowMultiple} value={configuration.maximumFileCount} onChange={value => setConfiguration({ ...configuration, maximumFileCount: Number(value || 1) })} /></Form.Item></Col>
        <Col xs={12} md={6}><Form.Item label="Display order"><InputNumber min={1} value={configuration.displayOrder} onChange={value => setConfiguration({ ...configuration, displayOrder: Number(value || 100) })} /></Form.Item></Col>
        <Col xs={12} md={6}><Form.Item label="Combined size (MB)"><InputNumber min={0} disabled={!configuration.allowMultiple} value={mb(configuration.maximumTotalSizeBytes)} onChange={value => setConfiguration({ ...configuration, maximumTotalSizeBytes: value ? bytes(value) : null })} /></Form.Item></Col>
        <Col xs={12} md={6}><Form.Item label="Required for"><Select value={configuration.requirementScope} onChange={value => setConfiguration({ ...configuration, requirementScope: value })} options={[{ value: 'NewEntitiesOnly', label: 'New records' }, { value: 'AllEntities', label: 'All records' }]} /></Form.Item></Col>
        <Col xs={12} md={6}><Form.Item label="Required"><Switch checked={configuration.isRequired} onChange={value => setConfiguration({ ...configuration, isRequired: value, minimumFileCount: value ? Math.max(1, configuration.minimumFileCount) : 0 })} /></Form.Item></Col>
        <Col xs={12} md={6}><Form.Item label="Version history"><Switch checked={configuration.versioningEnabled} onChange={value => setConfiguration({ ...configuration, versioningEnabled: value })} /></Form.Item></Col>
        <Col xs={12} md={6}><Form.Item label="Verification"><Switch checked={configuration.requiresVerification} onChange={value => setConfiguration({ ...configuration, requiresVerification: value })} /></Form.Item></Col>
        <Col xs={12} md={6}><Form.Item label="Active"><Switch checked={configuration.isActive} onChange={value => setConfiguration({ ...configuration, isActive: value })} /></Form.Item></Col>
        <Col span={24}><div className="attachment-permission-box"><strong>Employee self-service permissions</strong><Space wrap>
          <Checkbox checked={configuration.ownerCanView} onChange={event => setConfiguration({ ...configuration, ownerCanView: event.target.checked })}>View</Checkbox>
          <Checkbox checked={configuration.ownerCanUpload} onChange={event => setConfiguration({ ...configuration, ownerCanUpload: event.target.checked })}>Upload</Checkbox>
          <Checkbox checked={configuration.ownerCanReplace} onChange={event => setConfiguration({ ...configuration, ownerCanReplace: event.target.checked })}>Replace</Checkbox>
          <Checkbox checked={configuration.ownerCanDelete} onChange={event => setConfiguration({ ...configuration, ownerCanDelete: event.target.checked })}>Delete</Checkbox>
        </Space></div></Col>
        <Col xs={24} md={12}><Form.Item label="Effective from"><Input type="date" value={dateInput(configuration.effectiveFromUtc)} onChange={event => setConfiguration({ ...configuration, effectiveFromUtc: event.target.value || null })} /></Form.Item></Col>
        <Col xs={24} md={12}><Form.Item label="Effective to"><Input type="date" value={dateInput(configuration.effectiveUntilUtc)} onChange={event => setConfiguration({ ...configuration, effectiveUntilUtc: event.target.value || null })} /></Form.Item></Col>
      </Row></Form>}

      {drawer === 'storage' && <Form layout="vertical" requiredMark={false}><Row gutter={12}>
        <Col xs={24} md={10}><Form.Item label="Server code" required><Input value={server.serverCode} onChange={event => setServer({ ...server, serverCode: event.target.value.toUpperCase().replace(/[^A-Z0-9_]/g, '_') })} /></Form.Item></Col>
        <Col xs={24} md={14}><Form.Item label="Server name" required><Input value={server.serverName} onChange={event => setServer({ ...server, serverName: event.target.value })} /></Form.Item></Col>
        <Col span={24}><Form.Item label="Storage type"><Select value={server.storageType} onChange={changeStorageType} options={availableStorageTypes} /></Form.Item></Col>
        {server.storageType === 'HttpFileServer' ? <>
          <Col span={24}><Form.Item label="File server URL" required><Input value={server.serviceUrl} onChange={event => setServer({ ...server, serviceUrl: event.target.value })} placeholder="https://files.example.com" /></Form.Item></Col>
          <Col span={24}><Form.Item label={server.hasCredential ? 'API token (leave blank to keep existing)' : 'API token'}><Input.Password value={server.credential} onChange={event => setServer({ ...server, credential: event.target.value })} /></Form.Item></Col>
        </> : server.storageType === 'GoogleDrive' ? <Col span={24}><div className="google-drive-connection-panel">
          <div>
            <strong>Google Drive personal account</strong>
            <p>The OAuth credential JSON is handled only by the secure setup flow and is never displayed here. After setup, choose the Google account in the popup and Frevo HRMS prepares the attachment folder automatically.</p>
            {server.id > 0 && <Space wrap>
              <Tag color={isGoogleDriveConnected(server) ? 'green' : isGoogleOAuthConfigured(server) ? 'blue' : 'default'}>{googleConnectionLabel(server)}</Tag>
              {server.googleAccountEmail && <span>{server.googleAccountEmail}</span>}
              {server.googleFolderUrl
                ? <a href={server.googleFolderUrl} target="_blank" rel="noopener noreferrer">{server.googleFolderName || 'Open Drive folder'}</a>
                : server.googleFolderName && <span>{server.googleFolderName}</span>}
            </Space>}
          </div>
        </div></Col> : <Col span={24}><Form.Item label="Private storage folder / mounted path" required><Input value={server.basePath} onChange={event => setServer({ ...server, basePath: event.target.value })} placeholder="/app/data/attachments or mounted network path" /></Form.Item></Col>}
        <Col xs={12} md={6}><Form.Item label="Read enabled"><Switch checked={server.isReadEnabled} onChange={value => setServer({ ...server, isReadEnabled: value })} /></Form.Item></Col>
        <Col xs={12} md={6}><Form.Item label="Write enabled"><Switch checked={server.isWriteEnabled} onChange={value => setServer({ ...server, isWriteEnabled: value })} /></Form.Item></Col>
        <Col xs={12} md={6}><Form.Item label="Default write target"><Switch checked={server.isDefaultWriteServer} onChange={value => setServer({ ...server, isDefaultWriteServer: value, isReadEnabled: value || server.isReadEnabled, isWriteEnabled: value || server.isWriteEnabled, isActive: value || server.isActive })} /></Form.Item></Col>
        <Col xs={12} md={6}><Form.Item label="Active"><Switch checked={server.isActive} onChange={value => setServer({ ...server, isActive: value })} /></Form.Item></Col>
        <Col xs={12} md={8}><Form.Item label="Priority"><InputNumber min={1} value={server.priority} onChange={value => setServer({ ...server, priority: Number(value || 100) })} /></Form.Item></Col>
        <Col xs={12} md={8}><Form.Item label="Capacity warning %"><InputNumber min={1} max={100} value={server.warningCapacityPercent} onChange={value => setServer({ ...server, warningCapacityPercent: Number(value || 85) })} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Maximum capacity (GB, optional)"><InputNumber min={0} value={server.maximumCapacityBytes ? Number((server.maximumCapacityBytes / 1024 / 1024 / 1024).toFixed(2)) : 0} onChange={value => setServer({ ...server, maximumCapacityBytes: value ? Number(value) * 1024 * 1024 * 1024 : null })} /></Form.Item></Col>
        {server.id > 0 && <Col span={24}><div className="attachment-storage-note"><b>{server.linkedAttachmentCount}</b> active file(s) are linked to this server. A linked server cannot be disabled for reading.</div></Col>}
      </Row></Form>}
    </Drawer>

    <Modal
      className="google-drive-setup-modal"
      title="Set up Google Drive"
      open={googleSetupOpen}
      okText="Save OAuth setup"
      cancelText="Cancel"
      confirmLoading={configuringGoogleDrive}
      okButtonProps={{ disabled: !googleCredentialFile }}
      closable={!configuringGoogleDrive}
      maskClosable={!configuringGoogleDrive}
      destroyOnClose
      onCancel={closeGoogleSetup}
      onOk={() => void saveGoogleSetup()}
    >
      <div className="google-drive-setup-content">
        <div className="google-drive-guide-launch">
          <div>
            <Tag color="purple">First-time setup</Tag>
            <strong>Need help with Google Cloud?</strong>
            <p>Open the complete English guide for MFA, consent screen, test-user access, Drive API and production deployment.</p>
          </div>
          <Button data-testid="google-drive-open-guide" onClick={() => setGoogleGuideOpen(true)}>Complete setup guide</Button>
        </div>
        <p className="google-drive-setup-intro">This one-time setup keeps Google OAuth credentials on the API only. No client ID, client secret or refresh token is stored in the browser.</p>
        <div className="google-drive-setup-step">
          <span>1</span>
          <div>
            <strong>Create a Web application OAuth client</strong>
            <p>Open Google Cloud Credentials, create an OAuth client ID for a Web application, and keep that page open.</p>
            <Button href={googleCloudCredentialsUrl} target="_blank" rel="noopener noreferrer">Open Google Cloud Credentials</Button>
          </div>
        </div>
        <div className="google-drive-setup-step">
          <span>2</span>
          <div>
            <strong>Add this exact Authorized redirect URI</strong>
            <p>Paste the URL below into the OAuth client’s Authorized redirect URIs list.</p>
            <div className="google-drive-callback-field">
              <Input data-testid="google-oauth-callback-url" readOnly value={googleCallbackUrl} onFocus={event => event.target.select()} />
              <Button onClick={() => void copyGoogleCallbackUrl()}>Copy</Button>
            </div>
            {loadingGoogleSetup && <small>Verifying setup details with the API...</small>}
          </div>
        </div>
        <div className="google-drive-setup-step">
          <span>3</span>
          <div>
            <strong>Download and upload the credential JSON</strong>
            <p>Download the JSON from that OAuth client and upload that downloaded .json file here.</p>
            <label className="google-drive-file-picker" htmlFor="google-oauth-credential-file">
              <input
                key={googleFileInputKey}
                id="google-oauth-credential-file"
                data-testid="google-oauth-credential-file"
                type="file"
                accept=".json,application/json"
                onChange={event => selectGoogleCredentialFile(event.target.files?.[0])}
              />
              <span>{googleCredentialFile ? googleCredentialFile.name : 'Choose downloaded .json file'}</span>
            </label>
          </div>
        </div>
        <div className="google-drive-security-note">The file is sent directly to the authenticated API as multipart data. The API returns only safe setup status; credential contents are never rendered back to this portal.</div>
      </div>
    </Modal>

    <Modal
      className="google-drive-guide-modal"
      title="Google Drive integration guide"
      open={googleGuideOpen}
      width="min(920px, calc(100vw - 24px))"
      destroyOnClose
      onCancel={() => setGoogleGuideOpen(false)}
      footer={<Button type="primary" onClick={() => setGoogleGuideOpen(false)}>Done</Button>}
    >
      <div className="google-drive-guide">
        <header className="google-drive-guide-hero">
          <div>
            <span>FREVO HR · ADMIN SETUP</span>
            <h3>Connect private Google Drive attachment storage</h3>
            <p>Complete the Google Cloud steps once. Frevo HR then handles the account popup, private folder, token refresh, uploads and reads.</p>
          </div>
          <Tag color="green">One-time configuration</Tag>
        </header>

        <section className="google-drive-guide-reference" aria-label="Setup reference">
          <div><span>App name</span><strong>Frevo HR / Frevo One HR</strong></div>
          <div><span>OAuth client name</span><strong>Frevo HR Storage</strong></div>
          <div><span>Test account</span><strong>{googleDriveServer?.googleAccountEmail || 'Google account you plan to connect'}</strong></div>
          <div className="google-drive-guide-callback">
            <span>Current authorised redirect URI</span>
            <code data-testid="google-drive-guide-callback-url">{googleCallbackUrl}</code>
            <Button size="small" onClick={() => void copyGoogleCallbackUrl()}>Copy URI</Button>
          </div>
        </section>

        <div className="google-drive-guide-timeline">
          <GoogleDriveGuideStep number="1" title="Remove the Google Cloud security block">
            <p>If Google Cloud asks for 2-Step Verification, open <a href={googleSetupLinks.accountSecurity} target="_blank" rel="noopener noreferrer">Google Account Security</a>, enable MFA/2SV, and refresh the Cloud Console page.</p>
          </GoogleDriveGuideStep>

          <GoogleDriveGuideStep number="2" title="Configure the OAuth consent screen">
            <p>Open <a href={googleSetupLinks.authPlatform} target="_blank" rel="noopener noreferrer">Google Auth Platform</a> and select the Google Cloud project that will own this integration.</p>
            <ul>
              <li>App name: <code>Frevo HR</code></li>
              <li>Select your Google account as the user support email.</li>
              <li>Choose <strong>External</strong> as the audience, then save the setup.</li>
            </ul>
          </GoogleDriveGuideStep>

          <GoogleDriveGuideStep number="3" title="Create the Web OAuth client and download JSON">
            <p>Open <a href={googleSetupLinks.clients} target="_blank" rel="noopener noreferrer">Google Auth Platform · Clients</a>, click <strong>Create Client</strong>, and select <strong>Web application</strong>.</p>
            <ul>
              <li>Name the client <code>Frevo HR Storage</code>.</li>
              <li>Under Authorised redirect URIs, click <strong>Add URI</strong> and paste the exact current URI shown above.</li>
              <li>Click <strong>Create</strong> and immediately download the credential JSON.</li>
            </ul>
          </GoogleDriveGuideStep>

          <GoogleDriveGuideStep number="4" title="Allow the test account if Google returns Error 403">
            <p>While the app is in Testing, open <a href={googleSetupLinks.audience} target="_blank" rel="noopener noreferrer">Google Auth Platform · Audience</a>. Under <strong>Test users</strong>, click <strong>Add users</strong>, add the Google account you plan to connect, and save.</p>
            <div className="google-drive-guide-error"><strong>Error:</strong> access_denied — Frevo HR has not completed the Google verification process.</div>
          </GoogleDriveGuideStep>

          <GoogleDriveGuideStep number="5" title="Enable the Google Drive API">
            <p>Open the <a href={googleSetupLinks.driveApi} target="_blank" rel="noopener noreferrer">Google Drive API page</a> in the same Cloud project and click <strong>Enable</strong>.</p>
            <div className="google-drive-guide-error"><strong>Error:</strong> Google Drive API has not been used in this project before, or it is disabled.</div>
          </GoogleDriveGuideStep>

          <GoogleDriveGuideStep number="6" title="Upload, connect and verify in Frevo HR">
            <ol>
              <li>Return to this setup window and choose the downloaded <code>.json</code> file.</li>
              <li>Click <strong>Save OAuth setup</strong>.</li>
              <li>Click <strong>Connect Google Drive</strong>, select the approved account and grant access.</li>
              <li>Click <strong>Test</strong> on the Google Drive row. It should display <strong>Healthy / Connected</strong>.</li>
            </ol>
          </GoogleDriveGuideStep>
        </div>

        <section className="google-drive-production-guide">
          <div>
            <span>PRODUCTION CHECKLIST</span>
            <h4>Use the same OAuth client when the application goes live</h4>
          </div>
          <ul>
            <li>Edit <code>Frevo HR Storage</code> on the <a href={googleSetupLinks.clients} target="_blank" rel="noopener noreferrer">Clients page</a>.</li>
            <li>Add the exact callback displayed by the production Frevo HR portal, for example <code>https://api.your-domain.com/api/public/attachment-storage-servers/google/callback</code>.</li>
            <li>Production callbacks must use HTTPS. The localhost URI may remain alongside the production URI.</li>
            <li>Move the OAuth app from Testing to In production for stable long-term refresh-token access.</li>
          </ul>
        </section>

        <div className="google-drive-guide-security">
          <strong>Keep the downloaded JSON private.</strong>
          <span>Upload it only through this authenticated setup screen. Do not email it, commit it to source control or place it in a public folder.</span>
        </div>
      </div>
    </Modal>
  </section>
}

function Header({ title, text, action, onClick, actions }: { title: string; text: string; action?: string; onClick?: () => void; actions?: ReactNode }) {
  return <div className="attachment-settings-head"><div><h3>{title}</h3><p>{text}</p></div>{actions ?? (action && <Button type="primary" onClick={onClick}>{action}</Button>)}</div>
}

function GoogleDriveGuideStep({ number, title, children }: { number: string; title: string; children: ReactNode }) {
  return <section className="google-drive-guide-step">
    <span>{number}</span>
    <div>
      <h4>{title}</h4>
      {children}
    </div>
  </section>
}
