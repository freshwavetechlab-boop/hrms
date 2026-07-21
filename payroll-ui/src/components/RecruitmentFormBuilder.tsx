import { useEffect, useState } from 'react'
import dayjs from 'dayjs'
import {
  ArrowDownOutlined, ArrowUpOutlined, DeleteOutlined, EditOutlined, FileAddOutlined, PlusOutlined,
} from '@ant-design/icons'
import { Alert, Button, Card, DatePicker, Drawer, Empty, Form, Input, InputNumber, List, Modal, Select, Space, Switch, Tag, Tooltip, message } from 'antd'
import { getClients } from '../services/payrollService'
import {
  getRecruitmentForm, getRecruitmentForms, getRecruitmentOrchestrationLookups, publishRecruitmentFormVersion,
  saveRecruitmentFormDefinition, saveRecruitmentFormVersion,
} from '../services/recruitmentOrchestrationService'
import type { Client } from '../types/payroll'
import type {
  DynamicFormDefinition, DynamicFormField, DynamicFormFieldOption, DynamicFormFieldTypeCode, DynamicFormSection,
  DynamicFormValidationRule, DynamicFormValidationRuleType, DynamicFormVersion, RecruitmentOrchestrationLookups,
} from '../types/recruitmentOrchestration'
import './RecruitmentOrchestration.css'

type Props = { initialClientId?: number; onSaved?: (form: DynamicFormDefinition) => void }

const fieldTypes: Array<{ value: DynamicFormFieldTypeCode; label: string }> = [
  { value: 'TEXT', label: 'Single-line text' }, { value: 'TEXTAREA', label: 'Long text' },
  { value: 'NUMBER', label: 'Number' }, { value: 'DATE', label: 'Date' },
  { value: 'DATETIME', label: 'Date & time' }, { value: 'EMAIL', label: 'Email' },
  { value: 'PHONE', label: 'Phone' }, { value: 'SEARCH_SELECT', label: 'Searchable dropdown' },
  { value: 'MULTI_SELECT', label: 'Searchable multi-select' }, { value: 'RADIO', label: 'Radio group' },
  { value: 'CHECKBOX', label: 'Checkbox' }, { value: 'UPLOAD', label: 'Secure upload' },
]
const semanticOptions = ['FIRST_NAME', 'LAST_NAME', 'FATHERS_NAME', 'EMAIL', 'PHONE', 'RESUME', 'CONSENT', 'CURRENT_LOCATION', 'EXPECTED_CTC']
const emptyLookups: RecruitmentOrchestrationLookups = { lookupSources: [], attachmentConfigurations: [], attachmentFieldConfigurations: [], workflows: [], forms: [], positions: [], atsProfiles: [] }
const localId = () => -Math.floor(Date.now() + Math.random() * 100000)
const code = (value: string) => value.toUpperCase().trim().replace(/[^A-Z0-9]+/g, '_').replace(/^_+|_+$/g, '')
const isChoice = (type: DynamicFormFieldTypeCode) => ['SEARCH_SELECT', 'MULTI_SELECT', 'RADIO'].includes(type)
const isText = (type: DynamicFormFieldTypeCode) => ['TEXT', 'TEXTAREA', 'EMAIL', 'PHONE'].includes(type)
const isDate = (type: DynamicFormFieldTypeCode) => ['DATE', 'DATETIME'].includes(type)
const isScalar = (type: DynamicFormFieldTypeCode) => isText(type) || type === 'NUMBER' || isDate(type) || type === 'CHECKBOX'
const scalarTypesCompatible = (left: DynamicFormFieldTypeCode, right: DynamicFormFieldTypeCode) =>
  (isText(left) && isText(right)) || (left === 'NUMBER' && right === 'NUMBER') || (isDate(left) && isDate(right)) || (left === 'CHECKBOX' && right === 'CHECKBOX')

const validationRuleLabels: Record<DynamicFormValidationRuleType, string> = {
  REQUIRED: 'Required value',
  REGEX: 'Regular-expression pattern',
  EMAIL: 'Valid email address',
  PHONE: 'Valid phone number',
  DATE: 'Valid date',
  MIN_LENGTH: 'Minimum text length',
  MAX_LENGTH: 'Maximum text length',
  MIN_NUMBER: 'Minimum number',
  MAX_NUMBER: 'Maximum number',
  MIN_DATE: 'Earliest date',
  MAX_DATE: 'Latest date',
  BOOLEAN_TRUE: 'Must be checked',
  COMPARE_VALUE: 'Compare with a fixed value',
  COMPARE_FIELD: 'Compare with another field',
}

const validationRuleOptions = (fieldType: DynamicFormFieldTypeCode) => {
  const types: DynamicFormValidationRuleType[] = ['REQUIRED']
  if (isText(fieldType)) types.push('REGEX', 'EMAIL', 'PHONE', 'MIN_LENGTH', 'MAX_LENGTH')
  if (fieldType === 'NUMBER') types.push('MIN_NUMBER', 'MAX_NUMBER')
  if (isDate(fieldType)) types.push('DATE', 'MIN_DATE', 'MAX_DATE')
  if (fieldType === 'CHECKBOX') types.push('BOOLEAN_TRUE')
  if (isScalar(fieldType)) types.push('COMPARE_VALUE', 'COMPARE_FIELD')
  return types.map(value => ({ value, label: validationRuleLabels[value] }))
}

export default function RecruitmentFormBuilder({ initialClientId = 0, onSaved }: Props) {
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(initialClientId)
  const [forms, setForms] = useState<DynamicFormDefinition[]>([])
  const [lookups, setLookups] = useState(emptyLookups)
  const [definition, setDefinition] = useState<DynamicFormDefinition | null>(null)
  const [version, setVersion] = useState<DynamicFormVersion | null>(null)
  const [selectedSectionId, setSelectedSectionId] = useState<number | null>(null)
  const [selectedFieldId, setSelectedFieldId] = useState<number | null>(null)
  const [fieldDrawer, setFieldDrawer] = useState(false)
  const [sectionEditor, setSectionEditor] = useState<DynamicFormSection | null>(null)
  const [saving, setSaving] = useState(false)

  const load = async (scope: number) => {
    if (!scope) return
    const [rows, options] = await Promise.all([getRecruitmentForms(scope), getRecruitmentOrchestrationLookups(scope)])
    setForms(rows); setLookups(options)
  }

  useEffect(() => {
    void getClients().then(rows => {
      setClients(rows)
      if (!clientId && rows.length) setClientId(rows[0].id)
    })
  }, [])
  useEffect(() => { if (clientId) void load(clientId); setDefinition(null); setVersion(null) }, [clientId])

  const selectedSection = version?.sections.find(row => row.id === selectedSectionId) ?? null
  const selectedField = selectedSection?.fields.find(row => row.id === selectedFieldId)
    ?? version?.sections.flatMap(row => row.fields).find(row => row.id === selectedFieldId)
    ?? null
  const readOnly = version?.status === 'Published' || version?.status === 'Retired'

  const chooseForm = async (id: number) => {
    const row = await getRecruitmentForm(id)
    if (!row) return message.error('Unable to load the selected form.')
    const ordered = [...(row.versions || [])].sort((a, b) => b.versionNumber - a.versionNumber)
    const selected = ordered.find(item => item.status === 'Draft')
      ?? ordered.find(item => item.id === row.currentPublishedVersionId)
      ?? ordered[0]
      ?? blankVersion(row.id)
    const normalized = normalizeVersionValidationRules(selected)
    setDefinition(row); setVersion(normalized)
    setSelectedSectionId(normalized.sections[0]?.id ?? null); setSelectedFieldId(null)
  }

  const startNew = () => {
    if (!clientId) return message.warning('Select a client first.')
    const next = blankDefinition(clientId, clients.find(row => row.id === clientId)?.name ?? '')
    const draft = blankVersion(0)
    setDefinition(next); setVersion(draft); setSelectedSectionId(draft.sections[0].id); setSelectedFieldId(null)
  }

  const beginRevision = () => {
    if (!definition || !version) return
    const copy = cloneVersion(version, definition.id)
    setVersion(copy); setSelectedSectionId(copy.sections[0]?.id ?? null); setSelectedFieldId(null)
  }

  const patchDefinition = (patch: Partial<DynamicFormDefinition>) => setDefinition(current => current ? { ...current, ...patch } : current)
  const chooseModule = (moduleCode: string) => patchDefinition(moduleCode === 'EMPLOYEE'
    ? { moduleCode, purposeCode: 'EMPLOYEE_INFOTYPE_0002', entityType: 'EMPLOYEE' }
    : { moduleCode: 'RECRUITMENT', purposeCode: 'CANDIDATE_APPLICATION', entityType: 'CANDIDATE' })
  const employeeInfotype = definition?.purposeCode.match(/EMPLOYEE_INFOTYPE_(.+)$/)?.[1] || '0002'
  const patchVersion = (patch: Partial<DynamicFormVersion>) => setVersion(current => current ? { ...current, ...patch } : current)
  const patchSection = (sectionId: number, patch: Partial<DynamicFormSection>) => patchVersion({
    sections: version!.sections.map(row => row.id === sectionId ? { ...row, ...patch } : row),
  })
  const patchField = (fieldId: number, patch: Partial<DynamicFormField>) => patchVersion({
    sections: version!.sections.map(section => ({
      ...section, fields: section.fields.map(field => field.id === fieldId ? { ...field, ...patch } : field),
    })),
  })

  const addSection = () => {
    if (!version || readOnly) return
    const id = localId()
    const next: DynamicFormSection = {
      id, formVersionId: version.id, sectionCode: `SECTION_${version.sections.length + 1}`,
      sectionLabel: `Section ${version.sections.length + 1}`, description: '', displayOrder: version.sections.length + 1, fields: [],
    }
    patchVersion({ sections: [...version.sections, next] }); setSelectedSectionId(id); setSectionEditor(next)
  }

  const saveSectionEditor = () => {
    if (!sectionEditor || !sectionEditor.sectionLabel.trim()) return message.warning('Section label is required.')
    patchSection(sectionEditor.id, { ...sectionEditor, sectionCode: code(sectionEditor.sectionCode || sectionEditor.sectionLabel) })
    setSectionEditor(null)
  }

  const removeSection = (sectionId: number) => Modal.confirm({
    title: 'Delete this section?', content: 'All draft fields inside this section will also be removed.', okText: 'Delete', okButtonProps: { danger: true },
    onOk: () => {
      const sections = version!.sections.filter(row => row.id !== sectionId).map((row, index) => ({ ...row, displayOrder: index + 1 }))
      patchVersion({ sections }); setSelectedSectionId(sections[0]?.id ?? null); setSelectedFieldId(null)
    },
  })

  const moveSection = (sectionId: number, delta: number) => {
    const rows = [...version!.sections]; const index = rows.findIndex(row => row.id === sectionId); const target = index + delta
    if (index < 0 || target < 0 || target >= rows.length) return
    ;[rows[index], rows[target]] = [rows[target], rows[index]]
    patchVersion({ sections: rows.map((row, rowIndex) => ({ ...row, displayOrder: rowIndex + 1 })) })
  }

  const addField = (fieldTypeCode: DynamicFormFieldTypeCode) => {
    if (!selectedSection || readOnly) return message.info('Select an editable section first.')
    const id = localId(); const label = fieldTypes.find(row => row.value === fieldTypeCode)?.label ?? fieldTypeCode
    const field: DynamicFormField = {
      id, formVersionId: version!.id, sectionId: selectedSection.id, fieldTypeCode,
      stableFieldCode: `FIELD_${selectedSection.fields.length + 1}`, label, placeholder: '', helpText: '', isRequired: false,
      displayOrder: selectedSection.fields.length + 1, widthColumns: 6, minimumLength: null, maximumLength: null,
      minimumNumber: null, maximumNumber: null, minimumDate: null, maximumDate: null,
      attachmentFieldConfigurationId: null, lookupSourceCode: '', isActive: true, options: [], semanticCodes: [], validationRules: [],
    }
    patchSection(selectedSection.id, { fields: [...selectedSection.fields, field] }); setSelectedFieldId(id); setFieldDrawer(true)
  }

  const removeField = (fieldId: number) => {
    if (!selectedSection || readOnly) return
    patchSection(selectedSection.id, { fields: selectedSection.fields.filter(row => row.id !== fieldId).map((row, index) => ({ ...row, displayOrder: index + 1 })) })
    setSelectedFieldId(null); setFieldDrawer(false)
  }

  const moveField = (fieldId: number, delta: number) => {
    if (!selectedSection || readOnly) return
    const rows = [...selectedSection.fields]; const index = rows.findIndex(row => row.id === fieldId); const target = index + delta
    if (index < 0 || target < 0 || target >= rows.length) return
    ;[rows[index], rows[target]] = [rows[target], rows[index]]
    patchSection(selectedSection.id, { fields: rows.map((row, rowIndex) => ({ ...row, displayOrder: rowIndex + 1 })) })
  }

  const save = async () => {
    if (!definition || !version || readOnly) return
    const preparedVersion = withStableValidationReferences(version)
    const error = validate(definition, preparedVersion); if (error) return message.warning(error)
    setSaving(true)
    const definitionResponse = await saveRecruitmentFormDefinition({
      id: definition.id, clientId: definition.clientId, moduleCode: code(definition.moduleCode), formCode: code(definition.formCode || definition.formName),
      formName: definition.formName.trim(), purposeCode: code(definition.purposeCode), entityType: code(definition.entityType), status: definition.status,
    })
    if (!definitionResponse.ok || !definitionResponse.data) { setSaving(false); return }
    const versionResponse = await saveRecruitmentFormVersion(definitionResponse.data.id, { ...preparedVersion, formDefinitionId: definitionResponse.data.id })
    setSaving(false)
    if (!versionResponse.ok || !versionResponse.data) return
    onSaved?.(definitionResponse.data); await load(definition.clientId); await chooseForm(definitionResponse.data.id)
  }

  const publish = () => {
    if (!definition || !version?.id || readOnly) return message.info('Save the draft before publishing.')
    const error = validate(definition, version); if (error) return message.warning(error)
    Modal.confirm({
      title: 'Publish this form version?',
      content: 'Published versions are immutable. Existing application links keep their assigned version.',
      okText: 'Publish version',
      onOk: async () => { const response = await publishRecruitmentFormVersion(version.id); if (response.ok) { await load(definition.clientId); await chooseForm(definition.id) } },
    })
  }

  const clientOptions = clients.map(row => ({ value: row.id, label: row.name }))
  return <section className="orchestration-shell">
    <div className="orchestration-toolbar"><div><span className="orchestration-kicker">Global configuration</span><h2 className="orchestration-title">Application Form Builder</h2><p className="orchestration-subtitle">Normalized, versioned fields with no raw JSON or application-specific columns.</p></div><div><Select value={clientId || undefined} placeholder="Select client" options={clientOptions} onChange={setClientId} showSearch optionFilterProp="label" /><Button icon={<PlusOutlined />} type="primary" onClick={startNew}>New form</Button></div></div>
    <div className="form-builder-layout">
      <Card size="small" className="form-builder-library" title={`Forms (${forms.length})`}><List dataSource={forms} locale={{ emptyText: 'No forms for this client.' }} renderItem={row => <List.Item className={definition?.id === row.id ? 'active' : ''} onClick={() => void chooseForm(row.id)}><List.Item.Meta title={row.formName} description={<><span>{row.formCode}</span><br /><Tag color={row.currentPublishedVersionId ? 'green' : 'orange'}>{row.currentPublishedVersionId ? 'Published' : 'Draft only'}</Tag></>} /></List.Item>} /></Card>
      {!definition || !version ? <Card><Empty description="Select a form or start a new one." /></Card> : <div className="form-builder-canvas">
        <Card size="small"><div className="orchestration-toolbar"><Space wrap><Tag color={version.status === 'Published' ? 'green' : version.status === 'Retired' ? 'default' : 'gold'}>v{version.versionNumber} · {version.status}</Tag><Switch disabled={readOnly} checked={definition.status === 'Active'} onChange={active => patchDefinition({ status: active ? 'Active' : 'Inactive' })} checkedChildren="Active" unCheckedChildren="Inactive" /></Space><Space>{readOnly && <Button onClick={beginRevision}>Create next version</Button>}<Button loading={saving} disabled={readOnly} onClick={() => void save()}>Save draft</Button><Button type="primary" disabled={readOnly} onClick={publish}>Publish</Button></Space></div>
          <div className="form-builder-meta"><Form.Item label="Form name" required><Input disabled={readOnly} value={definition.formName} onChange={event => patchDefinition({ formName: event.target.value })} /></Form.Item><Form.Item label="Form code" required><Input disabled={readOnly} value={definition.formCode} onChange={event => patchDefinition({ formCode: code(event.target.value) })} /></Form.Item><Form.Item label="Use this form for" extra="Employee forms appear automatically in the matching Employee infotype after publishing."><Select data-testid="form-builder-module" disabled={readOnly} value={definition.moduleCode || 'RECRUITMENT'} onChange={chooseModule} options={[{ value: 'RECRUITMENT', label: 'Recruitment / Candidate' }, { value: 'EMPLOYEE', label: 'Employee additional fields' }]} /></Form.Item>{definition.moduleCode === 'EMPLOYEE' && <Form.Item label="Employee infotype"><Select data-testid="form-builder-employee-infotype" disabled={readOnly} value={employeeInfotype} onChange={value => patchDefinition({ purposeCode: `EMPLOYEE_INFOTYPE_${value}`, entityType: 'EMPLOYEE' })} options={[{ value: '0001', label: '0001 - Organizational Assignment' }, { value: '0002', label: '0002 - Personal Data' }, { value: '0006', label: '0006 - Addresses' }, { value: '0008', label: '0008 - Basic Pay' }, { value: '0009', label: '0009 - Bank Details' }]} /></Form.Item>}<Form.Item label="Purpose code"><Input data-testid="form-builder-purpose" disabled={readOnly} value={definition.purposeCode} onChange={event => patchDefinition({ purposeCode: code(event.target.value) })} /></Form.Item><Form.Item label="Entity type"><Input data-testid="form-builder-entity" disabled={readOnly} value={definition.entityType} onChange={event => patchDefinition({ entityType: code(event.target.value) })} /></Form.Item></div>
        </Card>
        <Card size="small" title="Field palette" extra={<Button icon={<PlusOutlined />} disabled={readOnly} onClick={addSection}>Add section</Button>}><div className="field-palette">{fieldTypes.map(item => <Button disabled={readOnly} key={item.value} onClick={() => addField(item.value)} icon={item.value === 'UPLOAD' ? <FileAddOutlined /> : <PlusOutlined />}>{item.label}</Button>)}</div></Card>
        {!version.sections.length && <div className="form-builder-empty"><Empty description="Add a section to begin designing the form." /></div>}
        {version.sections.map((section, sectionIndex) => <Card key={section.id} size="small" className={`form-builder-section ${selectedSectionId === section.id ? 'active' : ''}`} onClick={() => setSelectedSectionId(section.id)}><div className="form-builder-section-head"><div><h4>{section.sectionLabel}</h4><p>{section.description || section.sectionCode}</p></div><Space onClick={event => event.stopPropagation()}><Tooltip title="Move up"><Button size="small" icon={<ArrowUpOutlined />} disabled={readOnly || !sectionIndex} onClick={() => moveSection(section.id, -1)} /></Tooltip><Tooltip title="Move down"><Button size="small" icon={<ArrowDownOutlined />} disabled={readOnly || sectionIndex === version.sections.length - 1} onClick={() => moveSection(section.id, 1)} /></Tooltip><Button size="small" icon={<EditOutlined />} disabled={readOnly} onClick={() => setSectionEditor({ ...section })}>Edit</Button><Button size="small" danger icon={<DeleteOutlined />} disabled={readOnly} onClick={() => removeSection(section.id)} /></Space></div>
          {!section.fields.length ? <div className="form-builder-empty">Select this section, then choose a field from the palette.</div> : <div className="form-builder-grid">{section.fields.map(field => <div key={field.id} style={{ gridColumn: `span ${field.widthColumns}` }} className={`form-builder-field ${selectedFieldId === field.id ? 'active' : ''}`} onClick={event => { event.stopPropagation(); setSelectedSectionId(section.id); setSelectedFieldId(field.id); setFieldDrawer(true) }}><strong>{field.label}{field.isRequired ? ' *' : ''}</strong><Tag className="field-type">{fieldTypes.find(item => item.value === field.fieldTypeCode)?.label}</Tag><small>{field.helpText || field.placeholder || field.stableFieldCode}</small></div>)}</div>}
        </Card>)}
      </div>}
    </div>
    <Modal title="Section properties" open={!!sectionEditor} onCancel={() => setSectionEditor(null)} onOk={saveSectionEditor}>{sectionEditor && <Form layout="vertical"><Form.Item label="Section label" required><Input value={sectionEditor.sectionLabel} onChange={event => setSectionEditor({ ...sectionEditor, sectionLabel: event.target.value })} /></Form.Item><Form.Item label="Section code"><Input value={sectionEditor.sectionCode} onChange={event => setSectionEditor({ ...sectionEditor, sectionCode: code(event.target.value) })} /></Form.Item><Form.Item label="Description"><Input.TextArea value={sectionEditor.description} onChange={event => setSectionEditor({ ...sectionEditor, description: event.target.value })} /></Form.Item></Form>}</Modal>
    <Drawer width={760} title="Field properties" open={fieldDrawer && !!selectedField} onClose={() => setFieldDrawer(false)} extra={selectedField && <Space><Button icon={<ArrowUpOutlined />} disabled={readOnly} onClick={() => moveField(selectedField.id, -1)} /><Button icon={<ArrowDownOutlined />} disabled={readOnly} onClick={() => moveField(selectedField.id, 1)} /><Button danger icon={<DeleteOutlined />} disabled={readOnly} onClick={() => removeField(selectedField.id)}>Delete</Button></Space>}>{selectedField && <FieldProperties field={selectedField} allFields={version?.sections.flatMap(section => section.fields) ?? []} lookups={lookups} clientId={definition?.clientId ?? 0} readOnly={readOnly} patch={value => patchField(selectedField.id, value)} />}</Drawer>
  </section>
}

function FieldProperties({ field, allFields, lookups, clientId, readOnly, patch }: { field: DynamicFormField; allFields: DynamicFormField[]; lookups: RecruitmentOrchestrationLookups; clientId: number; readOnly: boolean; patch: (value: Partial<DynamicFormField>) => void }) {
  const addOption = () => patch({ options: [...field.options, { id: localId(), fieldId: field.id, optionCode: '', optionLabel: '', displayOrder: field.options.length + 1, isActive: true }] })
  const patchOption = (id: number, value: Partial<DynamicFormFieldOption>) => patch({ options: field.options.map(row => row.id === id ? { ...row, ...value } : row) })
  const removeOption = (id: number) => patch({ options: field.options.filter(row => row.id !== id).map((row, index) => ({ ...row, displayOrder: index + 1 })) })
  const moveOption = (id: number, delta: number) => { const rows = [...field.options]; const index = rows.findIndex(row => row.id === id); const target = index + delta; if (index < 0 || target < 0 || target >= rows.length) return; [rows[index], rows[target]] = [rows[target], rows[index]]; patch({ options: rows.map((row, rowIndex) => ({ ...row, displayOrder: rowIndex + 1 })) }) }
  const addRule = () => patch({ validationRules: [...field.validationRules, createValidationRule(field, field.validationRules.length + 1)] })
  const patchRule = (id: number, value: Partial<DynamicFormValidationRule>) => patch({ validationRules: field.validationRules.map(row => row.id === id ? { ...row, ...value } : row) })
  const changeRuleType = (rule: DynamicFormValidationRule, ruleType: DynamicFormValidationRuleType) => patchRule(rule.id, resetValidationRule(ruleType))
  const removeRule = (id: number) => patch({ validationRules: field.validationRules.filter(row => row.id !== id).map((row, index) => ({ ...row, displayOrder: index + 1 })) })
  const comparableFields = allFields.filter(row => row.id !== field.id && row.isActive && scalarTypesCompatible(field.fieldTypeCode, row.fieldTypeCode))
  const selectedAttachment = lookups.attachmentConfigurations.find(row => row.id === field.attachmentFieldConfigurationId)
  return <Form layout="vertical"><div className="field-property-grid">
    <Form.Item label="Field label" required><Input disabled={readOnly} value={field.label} onChange={event => patch({ label: event.target.value })} /></Form.Item><Form.Item label="Stable field code" required><Input disabled={readOnly} value={field.stableFieldCode} onChange={event => patch({ stableFieldCode: code(event.target.value) })} /></Form.Item>
    <Form.Item label="Field type"><Select disabled={readOnly} value={field.fieldTypeCode} options={fieldTypes} onChange={fieldTypeCode => patch({ fieldTypeCode, options: isChoice(fieldTypeCode) ? field.options : [], lookupSourceCode: isChoice(fieldTypeCode) ? field.lookupSourceCode : '', attachmentFieldConfigurationId: fieldTypeCode === 'UPLOAD' ? field.attachmentFieldConfigurationId : null })} /></Form.Item><Form.Item label="Width"><Select disabled={readOnly} value={field.widthColumns} onChange={widthColumns => patch({ widthColumns })} options={[{ value: 3, label: 'Quarter' }, { value: 4, label: 'One third' }, { value: 6, label: 'Half' }, { value: 12, label: 'Full' }]} /></Form.Item>
    <Form.Item className="wide" label="Placeholder"><Input disabled={readOnly} value={field.placeholder} onChange={event => patch({ placeholder: event.target.value })} /></Form.Item><Form.Item className="wide" label="Help text"><Input disabled={readOnly} value={field.helpText} onChange={event => patch({ helpText: event.target.value })} /></Form.Item>
    <Form.Item label="Required"><Switch disabled={readOnly} checked={field.isRequired} onChange={isRequired => patch({ isRequired })} /></Form.Item><Form.Item label="Active"><Switch disabled={readOnly} checked={field.isActive} onChange={isActive => patch({ isActive })} /></Form.Item>
    {['TEXT', 'TEXTAREA', 'EMAIL', 'PHONE'].includes(field.fieldTypeCode) && <><Form.Item label="Minimum characters"><InputNumber disabled={readOnly} min={0} value={field.minimumLength} onChange={value => patch({ minimumLength: value == null ? null : Number(value) })} /></Form.Item><Form.Item label="Maximum characters"><InputNumber disabled={readOnly} min={1} value={field.maximumLength} onChange={value => patch({ maximumLength: value == null ? null : Number(value) })} /></Form.Item></>}
    {field.fieldTypeCode === 'NUMBER' && <><Form.Item label="Minimum value"><InputNumber disabled={readOnly} value={field.minimumNumber} onChange={value => patch({ minimumNumber: value == null ? null : Number(value) })} /></Form.Item><Form.Item label="Maximum value"><InputNumber disabled={readOnly} value={field.maximumNumber} onChange={value => patch({ maximumNumber: value == null ? null : Number(value) })} /></Form.Item></>}
    {isChoice(field.fieldTypeCode) && <Form.Item className="wide" label="Registered lookup source" extra="Leave blank to maintain normalized static options below."><Select disabled={readOnly} allowClear value={field.lookupSourceCode || undefined} placeholder="Static options" onChange={value => patch({ lookupSourceCode: value || '' })} options={lookups.lookupSources.filter(row => row.isActive).map(row => ({ value: row.sourceCode, label: row.sourceName }))} /></Form.Item>}
    {field.fieldTypeCode === 'UPLOAD' && <Form.Item className="wide" label="Global attachment field configuration" required extra={selectedAttachment ? `${selectedAttachment.allowMultiple ? `Up to ${selectedAttachment.maximumFileCount} files` : 'Single file'} · ${formatBytes(selectedAttachment.maximumFileSizeBytes)} each · ${extensions(selectedAttachment.allowedExtensionsJson)}` : 'File type, size, permissions and versioning are enforced by the global attachment system.'}><Select disabled={readOnly} showSearch optionFilterProp="label" value={field.attachmentFieldConfigurationId || undefined} onChange={attachmentFieldConfigurationId => patch({ attachmentFieldConfigurationId })} options={lookups.attachmentConfigurations.filter(row => row.isActive && (row.clientId === 0 || row.clientId === clientId)).map(row => ({ value: row.id, label: `${row.fieldLabel || row.attributeName} (${row.attributeCode})` }))} /></Form.Item>}
    <Form.Item className="wide" label="Semantic mappings" extra="Use stable meanings such as EMAIL or RESUME so submission conversion does not depend on labels."><Select disabled={readOnly} mode="tags" value={field.semanticCodes} onChange={values => patch({ semanticCodes: values.map(code) })} options={semanticOptions.map(value => ({ value, label: value.replaceAll('_', ' ') }))} /></Form.Item>
  </div>
    {isChoice(field.fieldTypeCode) && !field.lookupSourceCode && <Card size="small" title="Static options" extra={<Button disabled={readOnly} size="small" icon={<PlusOutlined />} onClick={addOption}>Add option</Button>}>{!field.options.length && <Alert type="info" showIcon message="Add at least one option, or select a registered lookup source." />}{field.options.map((option, index) => <div className="field-option-row" key={option.id}><Input disabled={readOnly} value={option.optionLabel} placeholder="Label" onChange={event => patchOption(option.id, { optionLabel: event.target.value })} /><Input disabled={readOnly} value={option.optionCode} placeholder="Stored code" onChange={event => patchOption(option.id, { optionCode: code(event.target.value) })} /><Button disabled={readOnly || !index} className="order-action" icon={<ArrowUpOutlined />} onClick={() => moveOption(option.id, -1)} /><Button disabled={readOnly || index === field.options.length - 1} className="order-action" icon={<ArrowDownOutlined />} onClick={() => moveOption(option.id, 1)} /><Button disabled={readOnly} danger icon={<DeleteOutlined />} onClick={() => removeOption(option.id)} /></div>)}</Card>}
    <Card
      size="small"
      title="Additional validation rules"
      extra={<Button disabled={readOnly} size="small" icon={<PlusOutlined />} onClick={addRule}>Add rule</Button>}
    >
      {!field.validationRules.length && <Alert type="info" showIcon message="Add typed validation only when the standard field constraints are not enough." description="Rules and cross-field references are stored as normalized rows using stable field codes." />}
      <div className="validation-rule-list">
        {field.validationRules.map((rule, index) => <Card
          key={rule.id}
          size="small"
          type="inner"
          className="validation-rule-card"
          title={<Space><Tag color="purple">Rule {index + 1}</Tag><span>{validationRuleLabels[rule.ruleType as DynamicFormValidationRuleType] ?? rule.ruleType}</span></Space>}
          extra={<Button disabled={readOnly} danger type="text" icon={<DeleteOutlined />} onClick={() => removeRule(rule.id)}>Remove</Button>}
        >
          <div className="validation-rule-grid">
            <Form.Item label="Validation type" required>
              <Select disabled={readOnly} value={rule.ruleType as DynamicFormValidationRuleType} onChange={(value: DynamicFormValidationRuleType) => changeRuleType(rule, value)} options={validationRuleOptions(field.fieldTypeCode)} />
            </Form.Item>
            {needsComparisonOperator(rule.ruleType) && <Form.Item label="Comparison" required>
              <Select disabled={readOnly} value={rule.comparisonOperator || 'EQ'} onChange={comparisonOperator => patchRule(rule.id, { comparisonOperator })} options={comparisonOperatorOptions(field.fieldTypeCode)} />
            </Form.Item>}
            <ValidationRuleOperand
              field={field}
              rule={rule}
              comparableFields={comparableFields}
              readOnly={readOnly}
              patchRule={value => patchRule(rule.id, value)}
            />
            <Form.Item className="wide" label="User-facing error message" extra="Optional. A clear default message is used when this is blank.">
              <Input disabled={readOnly} maxLength={500} value={rule.errorMessage ?? ''} placeholder="Example: End date must be after start date." onChange={event => patchRule(rule.id, { errorMessage: event.target.value })} />
            </Form.Item>
          </div>
        </Card>)}
      </div>
    </Card>
  </Form>
}

function ValidationRuleOperand({ field, rule, comparableFields, readOnly, patchRule }: {
  field: DynamicFormField
  rule: DynamicFormValidationRule
  comparableFields: DynamicFormField[]
  readOnly: boolean
  patchRule: (value: Partial<DynamicFormValidationRule>) => void
}) {
  const ruleType = canonicalValidationRuleType(rule.ruleType, field.fieldTypeCode, !!(rule.compareFieldCode || rule.compareFieldId))
  const selectedCompareField = comparableFields.find(row => code(row.stableFieldCode) === code(rule.compareFieldCode ?? ''))
    ?? comparableFields.find(row => row.id === rule.compareFieldId)
  if (['REQUIRED', 'EMAIL', 'PHONE', 'DATE'].includes(ruleType)) return null
  if (ruleType === 'REGEX') return <Form.Item className="wide" label="Regular-expression pattern" required extra="The server validates this expression with a bounded execution timeout."><Input disabled={readOnly} maxLength={500} value={rule.textValue ?? ''} placeholder="Example: ^[A-Z]{3}[0-9]{4}$" onChange={event => patchRule({ textValue: event.target.value })} /></Form.Item>
  if (ruleType === 'MIN_LENGTH' || ruleType === 'MAX_LENGTH') return <Form.Item label={ruleType === 'MIN_LENGTH' ? 'Minimum characters' : 'Maximum characters'} required><InputNumber disabled={readOnly} min={0} precision={0} style={{ width: '100%' }} value={rule.integerValue ?? null} onChange={value => patchRule({ integerValue: value == null ? null : Number(value) })} /></Form.Item>
  if (ruleType === 'MIN_NUMBER' || ruleType === 'MAX_NUMBER') return <Form.Item label={ruleType === 'MIN_NUMBER' ? 'Minimum number' : 'Maximum number'} required><InputNumber disabled={readOnly} style={{ width: '100%' }} value={rule.decimalValue ?? null} onChange={value => patchRule({ decimalValue: value == null ? null : Number(value) })} /></Form.Item>
  if (ruleType === 'MIN_DATE' || ruleType === 'MAX_DATE') return <Form.Item label={ruleType === 'MIN_DATE' ? 'Earliest allowed date' : 'Latest allowed date'} required><DatePicker disabled={readOnly} showTime={field.fieldTypeCode === 'DATETIME'} style={{ width: '100%' }} value={validDayjs(rule.dateValue)} onChange={value => patchRule({ dateValue: value ? (field.fieldTypeCode === 'DATE' ? value.format('YYYY-MM-DD') : value.toISOString()) : null })} /></Form.Item>
  if (ruleType === 'BOOLEAN_TRUE') return <Form.Item label="Required checkbox value"><Switch checked disabled checkedChildren="Checked" /></Form.Item>
  if (ruleType === 'COMPARE_VALUE') {
    if (isText(field.fieldTypeCode)) return <Form.Item label="Comparison value" required><Input disabled={readOnly} maxLength={500} value={rule.textValue ?? ''} onChange={event => patchRule({ textValue: event.target.value })} /></Form.Item>
    if (field.fieldTypeCode === 'NUMBER') return <Form.Item label="Comparison value" required><InputNumber disabled={readOnly} style={{ width: '100%' }} value={rule.decimalValue ?? null} onChange={value => patchRule({ decimalValue: value == null ? null : Number(value) })} /></Form.Item>
    if (isDate(field.fieldTypeCode)) return <Form.Item label="Comparison value" required><DatePicker disabled={readOnly} showTime={field.fieldTypeCode === 'DATETIME'} style={{ width: '100%' }} value={validDayjs(rule.dateValue)} onChange={value => patchRule({ dateValue: value ? (field.fieldTypeCode === 'DATE' ? value.format('YYYY-MM-DD') : value.toISOString()) : null })} /></Form.Item>
    if (field.fieldTypeCode === 'CHECKBOX') return <Form.Item label="Comparison value" required><Select disabled={readOnly} value={rule.booleanValue == null ? undefined : String(rule.booleanValue)} onChange={value => patchRule({ booleanValue: value === 'true' })} options={[{ value: 'true', label: 'Checked / true' }, { value: 'false', label: 'Not checked / false' }]} /></Form.Item>
  }
  if (ruleType === 'COMPARE_FIELD') return <>
    <Form.Item className="wide" label="Comparison field" required extra="The stable field code is stored, so reordering or cloning the form does not break this rule.">
      <Select
        disabled={readOnly}
        showSearch
        optionFilterProp="label"
        value={code(selectedCompareField?.stableFieldCode ?? rule.compareFieldCode ?? '') || undefined}
        placeholder="Select another compatible field"
        onChange={(compareFieldCode: string) => {
          const selected = comparableFields.find(row => code(row.stableFieldCode) === code(compareFieldCode))
          patchRule({ compareFieldCode: selected ? code(selected.stableFieldCode) : code(compareFieldCode), compareFieldId: selected?.id ?? null })
        }}
        options={comparableFields.map(row => ({ value: code(row.stableFieldCode), label: `${row.label} · ${code(row.stableFieldCode)} · ${fieldTypes.find(type => type.value === row.fieldTypeCode)?.label ?? row.fieldTypeCode}` }))}
      />
    </Form.Item>
    {!comparableFields.length && <Alert className="wide" type="warning" showIcon message="No compatible comparison field is available." description="Add another active field with the same scalar data type, then return to this rule." />}
  </>
  return null
}

function createValidationRule(field: DynamicFormField, displayOrder: number): DynamicFormValidationRule {
  const ruleType: DynamicFormValidationRuleType = field.fieldTypeCode === 'EMAIL' ? 'EMAIL' : field.fieldTypeCode === 'PHONE' ? 'PHONE' : isDate(field.fieldTypeCode) ? 'DATE' : 'REQUIRED'
  return {
    id: localId(), fieldId: field.id, ruleType, comparisonOperator: defaultComparisonOperator(ruleType),
    compareFieldId: null, compareFieldCode: '', textValue: null, integerValue: null, decimalValue: null,
    dateValue: null, booleanValue: null, errorMessage: '', displayOrder,
  }
}

function resetValidationRule(ruleType: DynamicFormValidationRuleType): Partial<DynamicFormValidationRule> {
  return {
    ruleType, comparisonOperator: defaultComparisonOperator(ruleType), compareFieldId: null, compareFieldCode: '',
    textValue: null, integerValue: null, decimalValue: null, dateValue: null,
    booleanValue: ruleType === 'BOOLEAN_TRUE' ? true : null,
  }
}

function defaultComparisonOperator(ruleType: DynamicFormValidationRuleType) {
  if (ruleType === 'REGEX') return 'MATCHES'
  if (['MIN_LENGTH', 'MIN_NUMBER', 'MIN_DATE'].includes(ruleType)) return 'GTE'
  if (['MAX_LENGTH', 'MAX_NUMBER', 'MAX_DATE'].includes(ruleType)) return 'LTE'
  if (['BOOLEAN_TRUE', 'COMPARE_VALUE', 'COMPARE_FIELD'].includes(ruleType)) return 'EQ'
  return ''
}

function needsComparisonOperator(ruleType: string) {
  return ruleType === 'COMPARE_VALUE' || ruleType === 'COMPARE_FIELD' || ruleType === 'EQUALS' || ruleType === 'NOT_EQUALS'
}

function comparisonOperatorOptions(fieldType: DynamicFormFieldTypeCode) {
  const options = [{ value: 'EQ', label: 'Equals' }, { value: 'NE', label: 'Does not equal' }]
  if (fieldType === 'NUMBER' || isDate(fieldType)) options.push(
    { value: 'GT', label: 'Greater than / after' }, { value: 'GTE', label: 'Greater than or equal / on or after' },
    { value: 'LT', label: 'Less than / before' }, { value: 'LTE', label: 'Less than or equal / on or before' },
  )
  return options
}

function validDayjs(value?: string | null) {
  if (!value) return null
  const parsed = dayjs(value)
  return parsed.isValid() ? parsed : null
}

function canonicalValidationRuleType(ruleType: string, fieldType: DynamicFormFieldTypeCode, hasCompareField: boolean): string {
  const value = code(ruleType)
  if (value === 'PATTERN') return 'REGEX'
  if (value === 'MIN' || value === 'MINIMUM') return isText(fieldType) ? 'MIN_LENGTH' : fieldType === 'NUMBER' ? 'MIN_NUMBER' : isDate(fieldType) ? 'MIN_DATE' : value
  if (value === 'MAX' || value === 'MAXIMUM') return isText(fieldType) ? 'MAX_LENGTH' : fieldType === 'NUMBER' ? 'MAX_NUMBER' : isDate(fieldType) ? 'MAX_DATE' : value
  if (value === 'EQUALS' || value === 'NOT_EQUALS') return hasCompareField ? 'COMPARE_FIELD' : 'COMPARE_VALUE'
  if (value === 'SCALAR_COMPARE') return 'COMPARE_VALUE'
  if (value === 'CROSS_FIELD_COMPARE') return 'COMPARE_FIELD'
  return value
}

function normalizeVersionValidationRules(version: DynamicFormVersion): DynamicFormVersion {
  const fields = version.sections.flatMap(section => section.fields)
  const fieldById = new Map(fields.map(field => [field.id, field]))
  const fieldByCode = new Map(fields.map(field => [code(field.stableFieldCode), field]))
  return {
    ...version,
    sections: version.sections.map(section => ({
      ...section,
      fields: section.fields.map(field => ({
        ...field,
        validationRules: (field.validationRules ?? []).map((rule, index) => {
          const compareCode = code(rule.compareFieldCode ?? '')
          const compareField = (compareCode ? fieldByCode.get(compareCode) : undefined)
            ?? (rule.compareFieldId != null ? fieldById.get(rule.compareFieldId) : undefined)
          const ruleType = canonicalValidationRuleType(rule.ruleType, field.fieldTypeCode, !!(compareCode || rule.compareFieldId))
          const usesCompareField = ruleType === 'COMPARE_FIELD'
          return {
            ...rule,
            fieldId: field.id,
            ruleType,
            compareFieldId: usesCompareField ? compareField?.id ?? null : null,
            compareFieldCode: usesCompareField ? code(compareField?.stableFieldCode ?? compareCode) : '',
            comparisonOperator: code(rule.ruleType) === 'EQUALS' ? 'EQ' : code(rule.ruleType) === 'NOT_EQUALS' ? 'NE' : rule.comparisonOperator || defaultComparisonOperator(ruleType as DynamicFormValidationRuleType),
            displayOrder: index + 1,
          }
        }),
      })),
    })),
  }
}

function withStableValidationReferences(version: DynamicFormVersion) {
  return normalizeVersionValidationRules(version)
}

function blankDefinition(clientId: number, clientName: string): DynamicFormDefinition {
  return { id: 0, clientId, clientName, moduleCode: 'RECRUITMENT', formCode: '', formName: '', purposeCode: 'CANDIDATE_APPLICATION', entityType: 'CANDIDATE', status: 'Active', currentPublishedVersionId: null, versions: [] }
}
function blankVersion(formDefinitionId: number): DynamicFormVersion {
  const sectionId = localId()
  return { id: 0, formDefinitionId, versionNumber: 1, status: 'Draft', sections: [{ id: sectionId, formVersionId: 0, sectionCode: 'PERSONAL_DETAILS', sectionLabel: 'Personal details', description: '', displayOrder: 1, fields: [] }] }
}
function cloneVersion(source: DynamicFormVersion, formDefinitionId: number): DynamicFormVersion {
  const sourceFields = source.sections.flatMap(section => section.fields)
  const newIdByField = new Map(sourceFields.map(field => [field, localId()]))
  const sourceFieldById = new Map(sourceFields.map(field => [field.id, field]))
  const sourceFieldByCode = new Map(sourceFields.map(field => [code(field.stableFieldCode), field]))
  const sections = source.sections.map((section, sectionIndex) => {
    const sectionId = localId()
    return {
      ...section,
      id: sectionId,
      formVersionId: 0,
      displayOrder: sectionIndex + 1,
      fields: section.fields.map((field, fieldIndex) => {
        const fieldId = newIdByField.get(field)!
        return {
          ...field,
          id: fieldId,
          formVersionId: 0,
          sectionId,
          displayOrder: fieldIndex + 1,
          options: field.options.map((option, index) => ({ ...option, id: localId(), fieldId, displayOrder: index + 1 })),
          validationRules: (field.validationRules ?? []).map((rule, index) => {
            const compareCode = code(rule.compareFieldCode ?? '')
            const comparedSourceField = (compareCode ? sourceFieldByCode.get(compareCode) : undefined)
              ?? (rule.compareFieldId != null ? sourceFieldById.get(rule.compareFieldId) : undefined)
            const compareFieldId = comparedSourceField ? newIdByField.get(comparedSourceField) ?? null : null
            return {
              ...rule,
              id: localId(),
              fieldId,
              compareFieldId,
              compareFieldCode: code(comparedSourceField?.stableFieldCode ?? compareCode),
              displayOrder: index + 1,
            }
          }),
        }
      }),
    }
  })
  return normalizeVersionValidationRules({ id: 0, formDefinitionId, versionNumber: source.versionNumber + 1, status: 'Draft', sections })
}
function validate(definition: DynamicFormDefinition, version: DynamicFormVersion) {
  if (!definition.clientId || !definition.formName.trim() || !code(definition.formCode || definition.formName)) return 'Client, form name and form code are required.'
  if (!version.sections.length) return 'Add at least one section.'
  const fields = version.sections.flatMap(section => section.fields)
  if (!fields.length) return 'Add at least one field.'
  if (fields.some(field => !field.label.trim() || !code(field.stableFieldCode))) return 'Every field needs a label and stable field code.'
  if (new Set(fields.map(field => code(field.stableFieldCode))).size !== fields.length) return 'Stable field codes must be unique within a form version.'
  if (fields.some(field => isChoice(field.fieldTypeCode) && !field.lookupSourceCode && !field.options.length)) return 'Every choice field needs static options or a registered lookup source.'
  if (fields.some(field => field.fieldTypeCode === 'UPLOAD' && !field.attachmentFieldConfigurationId)) return 'Every upload field must use a global attachment field configuration.'
  if (fields.some(field => field.maximumLength && field.minimumLength && field.maximumLength < field.minimumLength)) return 'A field maximum length cannot be below its minimum.'
  if (fields.some(field => field.maximumNumber != null && field.minimumNumber != null && field.maximumNumber < field.minimumNumber)) return 'A field maximum value cannot be below its minimum.'
  for (const field of fields) {
    for (const rule of field.validationRules ?? []) {
      const ruleError = validateRule(field, rule, fields)
      if (ruleError) return `${field.label}: ${ruleError}`
    }
  }
  return ''
}

function validateRule(field: DynamicFormField, rule: DynamicFormValidationRule, fields: DynamicFormField[]) {
  const compareCode = code(rule.compareFieldCode ?? '')
  const compareField = (compareCode ? fields.find(row => code(row.stableFieldCode) === compareCode) : undefined)
    ?? (rule.compareFieldId != null ? fields.find(row => row.id === rule.compareFieldId) : undefined)
  const ruleType = canonicalValidationRuleType(rule.ruleType, field.fieldTypeCode, !!(compareCode || rule.compareFieldId))
  const available = validationRuleOptions(field.fieldTypeCode).some(option => option.value === ruleType)
  if (!available) return `Validation type ${ruleType || '(blank)'} is not compatible with this field type.`
  if ((rule.errorMessage ?? '').length > 500) return 'Validation error messages cannot exceed 500 characters.'
  if (ruleType === 'REGEX' && !rule.textValue?.trim()) return 'Enter a regular-expression pattern.'
  if ((ruleType === 'MIN_LENGTH' || ruleType === 'MAX_LENGTH') && (rule.integerValue == null || !Number.isInteger(rule.integerValue) || rule.integerValue < 0)) return 'Enter a non-negative whole-number length.'
  if ((ruleType === 'MIN_NUMBER' || ruleType === 'MAX_NUMBER') && (rule.decimalValue == null || !Number.isFinite(Number(rule.decimalValue)))) return 'Enter a valid numeric limit.'
  if ((ruleType === 'MIN_DATE' || ruleType === 'MAX_DATE') && !validDayjs(rule.dateValue)) return 'Select a valid date limit.'
  if (ruleType === 'COMPARE_VALUE') {
    if (!comparisonOperatorOptions(field.fieldTypeCode).some(option => option.value === rule.comparisonOperator)) return 'Select a supported comparison.'
    if (isText(field.fieldTypeCode) && rule.textValue == null) return 'Enter the text comparison value.'
    if (field.fieldTypeCode === 'NUMBER' && (rule.decimalValue == null || !Number.isFinite(Number(rule.decimalValue)))) return 'Enter a valid numeric comparison value.'
    if (isDate(field.fieldTypeCode) && !validDayjs(rule.dateValue)) return 'Select a valid date comparison value.'
    if (field.fieldTypeCode === 'CHECKBOX' && rule.booleanValue == null) return 'Select the checkbox comparison value.'
  }
  if (ruleType === 'COMPARE_FIELD') {
    if (!compareField) return 'Select a comparison field from this form version.'
    if (compareField.id === field.id) return 'A field cannot be compared with itself.'
    if (!compareField.isActive || !scalarTypesCompatible(field.fieldTypeCode, compareField.fieldTypeCode)) return 'The comparison field must be active and use a compatible scalar data type.'
    if (!comparisonOperatorOptions(field.fieldTypeCode).some(option => option.value === rule.comparisonOperator)) return 'Select a supported field comparison.'
  }
  return ''
}
function extensions(value: string) { try { return (JSON.parse(value) as string[]).map(item => `.${item}`).join(', ') } catch { return 'Configured types' } }
function formatBytes(value: number) { return value >= 1024 * 1024 ? `${(value / (1024 * 1024)).toFixed(value % (1024 * 1024) ? 1 : 0)} MB` : `${Math.ceil(value / 1024)} KB` }
