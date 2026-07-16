import { useCallback, useEffect, useMemo, useState } from 'react'
import { Button, Card, Checkbox, Col, Drawer, Form, Input, InputNumber, Row, Select, Space, Switch, Tabs, Tag } from 'antd'
import DataTable from './DataTable'
import SearchSelect, { selectOptions } from './SearchSelect'
import { useToast } from './ToastProvider'
import { getClients } from '../services/payrollService'
import { getAttachmentAttributes, getAttachmentConfigurations, getAttachmentStorageServers, getAttachmentTargets, saveAttachmentAttribute, saveAttachmentConfiguration, saveAttachmentStorageServer, testAttachmentStorageServer } from '../services/attachmentService'
import type { AttachmentAttribute, AttachmentFieldConfiguration, AttachmentStorageServer, AttachmentTargetOption, Client } from '../types/payroll'
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

export default function AttachmentSettings() {
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

  const load = useCallback(async () => {
    const [clientRows, attributeRows, configurationRows, serverRows, targetRows] = await Promise.all([
      getClients(), getAttachmentAttributes(), getAttachmentConfigurations(), getAttachmentStorageServers(), getAttachmentTargets()
    ])
    setClients(clientRows.filter(item => item.isActive))
    setAttributes(attributeRows)
    setConfigurations(configurationRows)
    setServers(serverRows)
    setTargets(targetRows)
  }, [])
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  const clientOptions = [{ value: 0, label: 'Global / all clients' }, ...clients.map(item => ({ value: item.id, label: item.name }))]
  const targetOptions = targets.map(item => ({ value: `${item.moduleCode}|${item.formCode}`, label: `${item.moduleName} / ${item.formName}` }))
  const availableAttributes = attributes.filter(item => item.isActive && (item.clientId === 0 || item.clientId === configuration.clientId))
  const selectedTarget = `${configuration.moduleCode}|${configuration.formCode}`
  const selectedExtensions = jsonList(configuration.allowedExtensionsJson)
  const availableStorageTypes = [
    { value: 'LocalFileSystem', label: 'API server local folder' },
    { value: 'MountedFileSystem', label: 'Mounted volume / network share' },
    { value: 'HttpFileServer', label: 'Remote file server API' }
  ]

  const openAttribute = (row?: AttachmentAttribute) => { setAttribute(row ? { ...row } : { ...attribute0 }); setDrawer('attribute') }
  const openConfiguration = (row?: AttachmentFieldConfiguration) => { setConfiguration(row ? { ...row } : { ...configuration0 }); setDrawer('configuration') }
  const openStorage = (row?: AttachmentStorageServer) => { setServer(row ? { ...row, credential: '' } : { ...storage0 }); setDrawer('storage') }
  const closeDrawer = () => setDrawer(null)

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

  return <section className="attachment-settings">
    <Card size="small" className="settings-panel settings-table-panel" title="Global Attachment Configuration">
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
            <Header title="Attachment storage servers" text={`${storageSummary}. Existing files remain linked to their original server; new files use the default write target.`} action="Add storage server" onClick={() => openStorage()} />
            <DataTable rows={servers} exportFileName="attachment-storage-servers" actions={row => <Space size={6}><Button size="small" onClick={() => void testServer(row)} loading={testingServerId === row.id}>Test</Button><Button size="small" type="primary" onClick={() => openStorage(row)}>Edit</Button></Space>} columns={[
              { key: 'serverName', label: 'Server', render: row => <Space size={5}><strong>{row.serverName}</strong>{row.isDefaultWriteServer && <Tag color="purple">Write target</Tag>}</Space> },
              { key: 'storageType', label: 'Type' },
              { key: 'location', label: 'Location', value: row => row.storageType === 'HttpFileServer' ? row.serviceUrl : row.basePath },
              { key: 'access', label: 'Access', value: row => `${row.isReadEnabled ? 'Read' : '-'} / ${row.isWriteEnabled ? 'Write' : '-'}` },
              { key: 'linkedAttachmentCount', label: 'Files' },
              { key: 'lastHealthCheckStatus', label: 'Health', render: row => <Tag color={statusColor(row.lastHealthCheckStatus)} title={row.lastHealthCheckMessage}>{row.lastHealthCheckStatus}</Tag> },
              { key: 'isActive', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
            ]} />
          </>
        }
      ]} />
    </Card>

    <Drawer className="settings-master-drawer attachment-settings-drawer" width="min(900px, 96vw)" destroyOnClose open={drawer !== null} title={drawer === 'attribute' ? `${attribute.id ? 'Edit' : 'Add'} attachment attribute` : drawer === 'configuration' ? `${configuration.id ? 'Edit' : 'Add'} attachment field` : `${server.id ? 'Edit' : 'Add'} storage server`} onClose={closeDrawer} footer={<Space><Button onClick={closeDrawer}>Cancel</Button><Button type="primary" onClick={() => drawer === 'attribute' ? void saveAttribute() : drawer === 'configuration' ? void saveConfiguration() : void saveServer()}>Save</Button></Space>}>
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
        <Col span={24}><Form.Item label="Storage type"><Select value={server.storageType} onChange={value => setServer({ ...server, storageType: value })} options={availableStorageTypes} /></Form.Item></Col>
        {server.storageType === 'HttpFileServer' ? <>
          <Col span={24}><Form.Item label="File server URL" required><Input value={server.serviceUrl} onChange={event => setServer({ ...server, serviceUrl: event.target.value })} placeholder="https://files.example.com" /></Form.Item></Col>
          <Col span={24}><Form.Item label={server.hasCredential ? 'API token (leave blank to keep existing)' : 'API token'}><Input.Password value={server.credential} onChange={event => setServer({ ...server, credential: event.target.value })} /></Form.Item></Col>
        </> : <Col span={24}><Form.Item label="Private storage folder / mounted path" required><Input value={server.basePath} onChange={event => setServer({ ...server, basePath: event.target.value })} placeholder="/app/data/attachments or mounted network path" /></Form.Item></Col>}
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
  </section>
}

function Header({ title, text, action, onClick }: { title: string; text: string; action: string; onClick: () => void }) {
  return <div className="attachment-settings-head"><div><h3>{title}</h3><p>{text}</p></div><Button type="primary" onClick={onClick}>{action}</Button></div>
}
