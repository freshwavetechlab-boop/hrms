import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { FileDoneOutlined, InboxOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Empty, Form, Input, InputNumber, Progress, Radio, Select, Skeleton, Space, Tag, Upload } from 'antd'
import type { UploadRequestOption } from 'rc-upload/lib/interface'
import { useToast } from './ToastProvider'
import { getEntityAttachments, openAttachmentWithTicket, uploadEntityAttachment } from '../services/attachmentService'
import { getEmployeeAttributeContext, saveEmployeeAttributeValues, searchEmployeeAttributeLookup } from '../services/employeeAttributeService'
import type { EntityAttachment } from '../types/payroll'
import type {
  EmployeeAttributeField,
  EmployeeAttributeForm,
  EmployeeAttributeLookupOption,
  EmployeeAttributeValue,
} from '../types/employeeAttributes'
import './EmployeeDynamicFields.css'

type Props = {
  employeeId: number
  clientId: number
  infotypeCode: string
  changeReason?: string
  onSaved?: () => void | Promise<void>
}

type FieldErrors = Record<number, string>

export default function EmployeeDynamicFields({ employeeId, clientId, infotypeCode, changeReason = '', onSaved }: Props) {
  const notify = useToast()
  const [forms, setForms] = useState<EmployeeAttributeForm[]>([])
  const [values, setValues] = useState<EmployeeAttributeValue[]>([])
  const [attachments, setAttachments] = useState<EntityAttachment[]>([])
  const [lookupOptions, setLookupOptions] = useState<Record<number, EmployeeAttributeLookupOption[]>>({})
  const [searching, setSearching] = useState<Record<number, boolean>>({})
  const [uploading, setUploading] = useState<Record<number, boolean>>({})
  const [uploadProgress, setUploadProgress] = useState<Record<number, number>>({})
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [loadError, setLoadError] = useState('')
  const lookupTimers = useRef(new Map<number, number>())
  const lookupRequests = useRef(new Map<number, number>())

  const load = useCallback(async () => {
    if (employeeId <= 0 || clientId <= 0 || !infotypeCode.trim()) {
      setForms([]); setValues([]); setAttachments([]); setLoadError('')
      return
    }
    setLoading(true); setLoadError('')
    try {
      const [context, files] = await Promise.all([
        getEmployeeAttributeContext(employeeId, clientId, infotypeCode),
        getEntityAttachments('EMPLOYEE', employeeId),
      ])
      setForms(Array.isArray(context.forms) ? context.forms : [])
      setValues(Array.isArray(context.values) ? context.values : [])
      setAttachments(Array.isArray(files) ? files : [])
    } catch (cause) {
      setForms([]); setValues([]); setAttachments([])
      setLoadError(cause instanceof Error ? cause.message : 'Additional employee fields could not be loaded.')
    } finally {
      setLoading(false)
    }
  }, [clientId, employeeId, infotypeCode])

  useEffect(() => { void load() }, [load])
  useEffect(() => () => { lookupTimers.current.forEach(timer => window.clearTimeout(timer)); lookupTimers.current.clear() }, [])

  const activeForms = useMemo(() => forms
    .filter(form => !['draft', 'retired', 'inactive'].includes(String(form.status || '').toLowerCase()))
    .map(form => ({
      ...form,
      sections: (Array.isArray(form.sections) ? form.sections : [])
        .map(section => ({ ...section, fields: (Array.isArray(section.fields) ? section.fields : []).filter(field => field.isActive !== false).sort((a, b) => a.displayOrder - b.displayOrder) }))
        .filter(section => section.fields.length)
        .sort((a, b) => a.displayOrder - b.displayOrder),
    }))
    .filter(form => form.sections.length), [forms])
  const fields = useMemo(() => activeForms.flatMap(form => form.sections.flatMap(section => section.fields)), [activeForms])
  const valueMap = useMemo(() => new Map(values.map(value => [value.fieldId, value])), [values])

  const patch = (fieldId: number, patchValue: Partial<EmployeeAttributeValue>) => {
    setValues(current => {
      const existing = current.find(value => value.fieldId === fieldId)
      return [...current.filter(value => value.fieldId !== fieldId), { ...(existing ?? { fieldId }), ...patchValue, fieldId }]
    })
    setFieldErrors(current => {
      if (!current[fieldId]) return current
      const next = { ...current }; delete next[fieldId]; return next
    })
  }

  const runLookup = (field: EmployeeAttributeField, search: string, immediate = false) => {
    if (!field.lookupSourceCode) return
    const previous = lookupTimers.current.get(field.id)
    if (previous) window.clearTimeout(previous)
    const execute = async () => {
      const request = (lookupRequests.current.get(field.id) ?? 0) + 1
      lookupRequests.current.set(field.id, request)
      setSearching(current => ({ ...current, [field.id]: true }))
      try {
        const options = await searchEmployeeAttributeLookup(employeeId, clientId, field.id, search)
        if (lookupRequests.current.get(field.id) === request) setLookupOptions(current => ({ ...current, [field.id]: Array.isArray(options) ? options : [] }))
      } finally {
        if (lookupRequests.current.get(field.id) === request) setSearching(current => ({ ...current, [field.id]: false }))
      }
    }
    if (immediate) void execute()
    else lookupTimers.current.set(field.id, window.setTimeout(() => void execute(), 250))
  }

  const filesFor = (field: EmployeeAttributeField) => field.attachmentFieldConfigurationId
    ? attachments.filter(file => file.fieldConfigurationId === field.attachmentFieldConfigurationId)
    : []

  const uploadFor = (field: EmployeeAttributeField) => async ({ file, onProgress, onSuccess, onError }: UploadRequestOption) => {
    if (typeof file === 'string' || !(file instanceof Blob)) { onError?.(new Error('The selected file is invalid.')); return }
    if (!field.attachmentFieldConfigurationId) { onError?.(new Error('This upload field is not linked to the global attachment configuration.')); return }
    const selected = file instanceof File ? file : new File([file], 'upload')
    const validationError = validateUpload(field, selected, filesFor(field))
    if (validationError) { onError?.(new Error(validationError)); notify(validationError, 'warning'); return }
    setUploading(current => ({ ...current, [field.id]: true })); setUploadProgress(current => ({ ...current, [field.id]: 1 }))
    const response = await uploadEntityAttachment(field.attachmentFieldConfigurationId, 'EMPLOYEE', employeeId, selected, {}, percent => {
      setUploadProgress(current => ({ ...current, [field.id]: percent })); onProgress?.({ percent })
    })
    setUploading(current => ({ ...current, [field.id]: false }))
    if (!response.ok) { onError?.(new Error(response.error || 'File upload failed.')); return }
    onSuccess?.(response.data)
    notify(`${field.label} uploaded.`, 'success')
    setUploadProgress(current => ({ ...current, [field.id]: 0 }))
    setAttachments(await getEntityAttachments('EMPLOYEE', employeeId))
    setFieldErrors(current => { const next = { ...current }; delete next[field.id]; return next })
    await onSaved?.()
  }

  const save = async () => {
    const errors = validateFields(fields, valueMap, attachments)
    setFieldErrors(errors)
    const firstError = Object.values(errors)[0]
    if (firstError) { notify(firstError, 'warning'); return }
    setSaving(true)
    try {
      const response = await saveEmployeeAttributeValues(employeeId, { clientId, infotypeCode, changeReason: changeReason.trim(), values })
      if (!response.ok) { notify(response.error || 'Additional employee fields could not be saved.', 'error'); return }
      const savedValues = Array.isArray(response.data?.values) ? response.data.values : values
      setValues(savedValues)
      notify('Additional employee fields saved.', 'success')
      await onSaved?.()
    } finally {
      setSaving(false)
    }
  }

  if (employeeId <= 0) return <Alert className="employee-dynamic-message" type="info" showIcon message="Save the employee first" description="Additional configured fields become available after the Employee ID is generated." />
  if (loading) return <div className="employee-dynamic-loading" data-testid="employee-dynamic-fields"><Skeleton active paragraph={{ rows: 3 }} /></div>
  if (loadError) return <Alert className="employee-dynamic-message" type="error" showIcon message="Additional fields could not be loaded" description={loadError} action={<Button size="small" onClick={() => void load()}>Retry</Button>} />
  if (!activeForms.length) return null

  return <section className="employee-dynamic-fields" data-testid="employee-dynamic-fields">
    <header className="employee-dynamic-heading"><div><span>CONFIGURED FIELDS</span><h4>Additional employee information</h4><p>These fields are controlled by the published Employee form configuration for this client and infotype.</p></div><Tag color="purple">{fields.length} field{fields.length === 1 ? '' : 's'}</Tag></header>
    {Object.keys(fieldErrors).length > 0 && <Alert type="warning" showIcon message="Review the highlighted fields" description={`${Object.keys(fieldErrors).length} field${Object.keys(fieldErrors).length === 1 ? '' : 's'} need attention before saving.`} />}
    <Form component="div" layout="vertical" className="employee-dynamic-form">
      {activeForms.map(form => <section className="employee-dynamic-form-card" data-testid={`employee-dynamic-form-${form.formCode}`} key={`${form.id}-${form.formDefinitionId}`}>
        {activeForms.length > 1 && <div className="employee-dynamic-form-title"><b>{form.formName || form.formCode}</b><span>Published v{form.versionNumber || '-'}</span></div>}
        {form.sections.map(section => <section className="employee-dynamic-section" key={section.id}>
          <div className="employee-dynamic-section-title"><h5>{section.sectionLabel}</h5>{section.description && <p>{section.description}</p>}</div>
          <div className="employee-dynamic-grid">{section.fields.map(field => <DynamicField
            key={field.id}
            field={field}
            answer={valueMap.get(field.id)}
            error={fieldErrors[field.id]}
            files={filesFor(field)}
            lookupOptions={lookupOptions[field.id] ?? []}
            searching={Boolean(searching[field.id])}
            uploading={Boolean(uploading[field.id])}
            progress={uploadProgress[field.id] ?? 0}
            patch={value => patch(field.id, value)}
            search={(term, immediate) => runLookup(field, term, immediate)}
            upload={uploadFor(field)}
          />)}</div>
        </section>)}
      </section>)}
    </Form>
    <div className="employee-dynamic-actions"><span>Changes are stored with the selected employee and infotype.</span><Button data-testid="employee-dynamic-fields-save" type="primary" loading={saving} disabled={Object.values(uploading).some(Boolean)} onClick={() => void save()}>Save additional fields</Button></div>
  </section>
}

function DynamicField(p: {
  field: EmployeeAttributeField
  answer?: EmployeeAttributeValue
  error?: string
  files: EntityAttachment[]
  lookupOptions: EmployeeAttributeLookupOption[]
  searching: boolean
  uploading: boolean
  progress: number
  patch: (value: Partial<EmployeeAttributeValue>) => void
  search: (term: string, immediate?: boolean) => void
  upload: (request: UploadRequestOption) => Promise<void>
}) {
  const { field, answer } = p
  const type = String(field.fieldTypeCode || '').toUpperCase()
  const usesLookup = Boolean(field.lookupSourceCode)
  const staticOptions = (Array.isArray(field.options) ? field.options : []).filter(option => option.isActive !== false).sort((a, b) => a.displayOrder - b.displayOrder).map(option => ({ value: option.id, label: option.optionLabel }))
  const width = Math.max(1, Math.min(12, Number(field.widthColumns) || 12))
  const testId = `employee-dynamic-field-${field.stableFieldCode || field.id}`
  const selectValue = (multiple: boolean) => multiple
    ? (usesLookup ? answer?.selectedOptionValues ?? [] : answer?.selectedOptionIds ?? [])
    : (usesLookup ? answer?.selectedOptionValues?.[0] : answer?.selectedOptionIds?.[0])
  const select = (selected: string | number | Array<string | number> | null | undefined) => {
    const list = Array.isArray(selected) ? selected : selected == null ? [] : [selected]
    p.patch(usesLookup ? { selectedOptionValues: list.map(String), selectedOptionIds: [] } : { selectedOptionIds: list.map(Number), selectedOptionValues: [] })
  }
  return <Form.Item
    className="employee-dynamic-field"
    data-testid={testId}
    style={{ gridColumn: `span ${width}` }}
    label={field.label}
    required={field.isRequired}
    validateStatus={p.error ? 'error' : undefined}
    help={p.error || field.helpText || undefined}
  >
    {type === 'TEXT' && <Input value={answer?.textValue ?? ''} placeholder={field.placeholder} minLength={field.minimumLength ?? undefined} maxLength={field.maximumLength ?? undefined} onChange={event => p.patch({ textValue: event.target.value })} />}
    {type === 'TEXTAREA' && <Input.TextArea rows={3} value={answer?.textValue ?? ''} placeholder={field.placeholder} minLength={field.minimumLength ?? undefined} maxLength={field.maximumLength ?? undefined} onChange={event => p.patch({ textValue: event.target.value })} />}
    {type === 'EMAIL' && <Input type="email" value={answer?.textValue ?? ''} placeholder={field.placeholder || 'name@example.com'} onChange={event => p.patch({ textValue: event.target.value })} />}
    {type === 'PHONE' && <Input type="tel" value={answer?.textValue ?? ''} placeholder={field.placeholder || 'Mobile number'} onChange={event => p.patch({ textValue: event.target.value })} />}
    {type === 'NUMBER' && <InputNumber style={{ width: '100%' }} min={field.minimumNumber ?? undefined} max={field.maximumNumber ?? undefined} value={answer?.decimalValue ?? undefined} placeholder={field.placeholder} onChange={value => p.patch({ decimalValue: value == null ? null : Number(value) })} />}
    {type === 'DATE' && <Input type="date" min={field.minimumDate?.slice(0, 10)} max={field.maximumDate?.slice(0, 10)} value={answer?.dateValue?.slice(0, 10) ?? ''} onChange={event => p.patch({ dateValue: event.target.value })} />}
    {type === 'DATETIME' && <Input type="datetime-local" min={field.minimumDate?.slice(0, 16)} max={field.maximumDate?.slice(0, 16)} value={answer?.dateTimeValue?.slice(0, 16) ?? ''} onChange={event => p.patch({ dateTimeValue: event.target.value })} />}
    {['SELECT', 'SEARCH_SELECT', 'MULTI_SELECT'].includes(type) && <Select<string | number | Array<string | number>>
      mode={type === 'MULTI_SELECT' ? 'multiple' : undefined}
      showSearch={type !== 'SELECT' || usesLookup}
      allowClear
      filterOption={!usesLookup}
      optionFilterProp="label"
      loading={p.searching}
      value={selectValue(type === 'MULTI_SELECT')}
      placeholder={field.placeholder || 'Select'}
      onFocus={() => usesLookup && p.search('', true)}
      onSearch={term => usesLookup && p.search(term)}
      onChange={select}
      options={usesLookup ? p.lookupOptions.map(option => ({ value: option.value, label: option.label })) : staticOptions}
    />}
    {type === 'RADIO' && <Radio.Group value={answer?.selectedOptionIds?.[0]} onChange={event => p.patch({ selectedOptionIds: [Number(event.target.value)], selectedOptionValues: [] })} options={staticOptions} />}
    {type === 'CHECKBOX' && <Checkbox checked={Boolean(answer?.booleanValue)} onChange={event => p.patch({ booleanValue: event.target.checked })}>{field.placeholder || field.label}</Checkbox>}
    {type === 'UPLOAD' && <div className="employee-dynamic-upload" data-testid={`${testId}-upload`}>
      {!!p.files.length && <Space size={[6, 6]} wrap>{p.files.map(file => <Button type="link" size="small" icon={<FileDoneOutlined />} key={file.publicId} onClick={() => void openAttachmentWithTicket(file.publicId, 'Preview')}>{file.originalFileName}</Button>)}</Space>}
      {!field.attachmentFieldConfigurationId
        ? <Alert type="warning" showIcon message="Attachment configuration is missing" description="Link this field to a global attachment field before uploading files." />
        : <Upload.Dragger multiple={Boolean(field.attachmentConstraints?.allowMultiple)} maxCount={field.attachmentConstraints?.maximumFileCount || 1} accept={uploadAccept(field)} showUploadList={false} disabled={p.uploading || uploadLimitReached(field, p.files)} customRequest={p.upload}>
          <p className="ant-upload-drag-icon"><InboxOutlined /></p><p>{uploadLimitReached(field, p.files) ? 'Maximum file count reached' : 'Choose or drop file here'}</p><small>{uploadSummary(field)}</small>
        </Upload.Dragger>}
      {p.uploading && <Progress percent={p.progress} size="small" />}
    </div>}
  </Form.Item>
}

function validateFields(fields: EmployeeAttributeField[], values: Map<number, EmployeeAttributeValue>, attachments: EntityAttachment[]): FieldErrors {
  const errors: FieldErrors = {}
  for (const field of fields) {
    const answer = values.get(field.id)
    const type = String(field.fieldTypeCode || '').toUpperCase()
    const files = field.attachmentFieldConfigurationId ? attachments.filter(file => file.fieldConfigurationId === field.attachmentFieldConfigurationId) : []
    if (field.isRequired && isEmpty(field, answer, files)) { errors[field.id] = `${field.label} is required.`; continue }
    const text = answer?.textValue?.trim() ?? ''
    if (field.minimumLength && text && text.length < field.minimumLength) errors[field.id] = `${field.label} must contain at least ${field.minimumLength} characters.`
    else if (field.maximumLength && text.length > field.maximumLength) errors[field.id] = `${field.label} cannot exceed ${field.maximumLength} characters.`
    else if (type === 'EMAIL' && text && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(text)) errors[field.id] = `Enter a valid ${field.label.toLowerCase()}.`
    else if (type === 'PHONE' && text && !/^[+\d][\d\s()-]{6,20}$/.test(text)) errors[field.id] = `Enter a valid ${field.label.toLowerCase()}.`
    else if (type === 'NUMBER' && answer?.decimalValue != null && field.minimumNumber != null && answer.decimalValue < field.minimumNumber) errors[field.id] = `${field.label} cannot be less than ${field.minimumNumber}.`
    else if (type === 'NUMBER' && answer?.decimalValue != null && field.maximumNumber != null && answer.decimalValue > field.maximumNumber) errors[field.id] = `${field.label} cannot exceed ${field.maximumNumber}.`
  }
  return errors
}

function isEmpty(field: EmployeeAttributeField, answer: EmployeeAttributeValue | undefined, files: EntityAttachment[]) {
  const type = String(field.fieldTypeCode || '').toUpperCase()
  if (type === 'UPLOAD') return files.length === 0
  if (type === 'CHECKBOX') return !answer?.booleanValue
  if (type === 'NUMBER') return answer?.decimalValue == null && answer?.integerValue == null
  if (type === 'DATE') return !answer?.dateValue
  if (type === 'DATETIME') return !answer?.dateTimeValue
  if (['SELECT', 'SEARCH_SELECT', 'MULTI_SELECT'].includes(type)) return field.lookupSourceCode ? !answer?.selectedOptionValues?.length : !answer?.selectedOptionIds?.length
  if (type === 'RADIO') return !answer?.selectedOptionIds?.length
  return !answer?.textValue?.trim()
}

function validateUpload(field: EmployeeAttributeField, file: File, existing: EntityAttachment[]) {
  const constraints = field.attachmentConstraints
  if (!constraints) return ''
  if (existing.length >= Math.max(1, constraints.maximumFileCount)) return `A maximum of ${constraints.maximumFileCount} file(s) is allowed.`
  if (constraints.maximumFileSizeBytes > 0 && file.size > constraints.maximumFileSizeBytes) return `File size cannot exceed ${formatBytes(constraints.maximumFileSizeBytes)}.`
  const extension = file.name.includes('.') ? `.${file.name.split('.').pop()?.toLowerCase()}` : ''
  const allowedExtensions = (constraints.allowedExtensions ?? []).map(value => value.startsWith('.') ? value.toLowerCase() : `.${value.toLowerCase()}`)
  if (allowedExtensions.length && !allowedExtensions.includes(extension)) return `Allowed file types: ${allowedExtensions.join(', ')}.`
  if ((constraints.allowedMimeTypes ?? []).length && file.type && !constraints.allowedMimeTypes.some(value => value.toLowerCase() === file.type.toLowerCase())) return 'The selected file type is not allowed.'
  const total = existing.reduce((sum, row) => sum + Number(row.fileSizeBytes || 0), 0) + file.size
  if (constraints.maximumTotalSizeBytes && total > constraints.maximumTotalSizeBytes) return `Combined file size cannot exceed ${formatBytes(constraints.maximumTotalSizeBytes)}.`
  return ''
}

function uploadLimitReached(field: EmployeeAttributeField, files: EntityAttachment[]) {
  const maximum = field.attachmentConstraints?.maximumFileCount || 1
  return files.length >= maximum
}

function uploadAccept(field: EmployeeAttributeField) {
  const constraints = field.attachmentConstraints
  if (!constraints) return undefined
  return [...(constraints.allowedExtensions ?? []).map(value => value.startsWith('.') ? value : `.${value}`), ...(constraints.allowedMimeTypes ?? [])].join(',') || undefined
}

function uploadSummary(field: EmployeeAttributeField) {
  const constraints = field.attachmentConstraints
  if (!constraints) return 'Configured file type and size rules are enforced by the secure document service.'
  const extensions = constraints.allowedExtensions?.length ? constraints.allowedExtensions.join(', ') : 'configured file types'
  const size = constraints.maximumFileSizeBytes > 0 ? `, up to ${formatBytes(constraints.maximumFileSizeBytes)}` : ''
  return `${extensions}${size}; maximum ${Math.max(1, constraints.maximumFileCount)} file(s).`
}

function formatBytes(bytes: number) {
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`
  return `${(bytes / (1024 * 1024)).toFixed(bytes % (1024 * 1024) ? 1 : 0)} MB`
}
