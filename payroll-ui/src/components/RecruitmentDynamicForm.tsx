import { useMemo, useState } from 'react'
import { FileDoneOutlined, InboxOutlined } from '@ant-design/icons'
import { Checkbox, Form, Input, InputNumber, Radio, Select, Tag, Upload } from 'antd'
import type { UploadRequestOption } from 'rc-upload/lib/interface'
import type {
  DynamicFormField, DynamicFormVersion, DynamicLookupOption, PublicFormValue, PublicUploadedFile,
} from '../types/recruitmentOrchestration'
import './RecruitmentOrchestration.css'

type Props = {
  form: DynamicFormVersion
  values: PublicFormValue[]
  files: PublicUploadedFile[]
  disabled?: boolean
  onChange: (values: PublicFormValue[]) => void
  onUpload: (field: DynamicFormField, file: File, onProgress: (percent: number) => void) => Promise<{ ok: boolean; error?: string }>
  onLoadOptions?: (field: DynamicFormField, search: string) => Promise<DynamicLookupOption[]>
}

export default function RecruitmentDynamicForm({ form, values, files, disabled = false, onChange, onUpload, onLoadOptions }: Props) {
  const [remoteOptions, setRemoteOptions] = useState<Record<number, DynamicLookupOption[]>>({})
  const [searching, setSearching] = useState<Record<number, boolean>>({})
  const valueMap = useMemo(() => new Map(values.map(row => [row.fieldId, row])), [values])

  const patch = (fieldId: number, value: Partial<PublicFormValue>) => {
    const next = values.filter(row => row.fieldId !== fieldId)
    onChange([...next, { fieldId, ...value }])
  }
  const search = async (field: DynamicFormField, term: string) => {
    if (!field.lookupSourceCode || !onLoadOptions) return
    setSearching(current => ({ ...current, [field.id]: true }))
    try {
      const options = await onLoadOptions(field, term)
      setRemoteOptions(current => ({ ...current, [field.id]: options }))
    } finally {
      setSearching(current => ({ ...current, [field.id]: false }))
    }
  }
  const uploader = (field: DynamicFormField) => async ({ file, onProgress, onSuccess, onError }: UploadRequestOption) => {
    if (typeof file === 'string' || !(file instanceof Blob)) return onError?.(new Error('The selected file is invalid.'))
    const selected = file instanceof File ? file : new File([file], 'upload')
    const validationError = validateUpload(field, selected, files.filter(row => row.fieldId === field.id))
    if (validationError) return onError?.(new Error(validationError))
    const result = await onUpload(field, selected, percent => onProgress?.({ percent }))
    if (result.ok) onSuccess?.({}); else onError?.(new Error(result.error || 'Upload failed.'))
  }

  return <div>{[...form.sections].sort((a, b) => a.displayOrder - b.displayOrder).map(section => <section className="public-form-section" key={section.id}>
    <h3>{section.sectionLabel}</h3>{section.description && <p>{section.description}</p>}
    <div className="public-form-grid">{[...section.fields].filter(field => field.isActive).sort((a, b) => a.displayOrder - b.displayOrder).map(field => {
      const answer = valueMap.get(field.id); const uploaded = files.filter(row => row.fieldId === field.id)
      const usesLookup = Boolean(field.lookupSourceCode)
      const staticOptions = [...field.options].filter(row => row.isActive).sort((a, b) => a.displayOrder - b.displayOrder).map(row => ({ value: row.id, label: row.optionLabel }))
      return <Form.Item key={field.id} data-testid={`dynamic-field-${field.stableFieldCode}`} style={{ gridColumn: `span ${Math.max(1, Math.min(12, field.widthColumns))}` }} label={field.label} required={field.isRequired} extra={field.helpText}>
        {field.fieldTypeCode === 'TEXT' && <Input disabled={disabled} value={answer?.textValue ?? ''} placeholder={field.placeholder} minLength={field.minimumLength ?? undefined} maxLength={field.maximumLength ?? undefined} onChange={event => patch(field.id, { textValue: event.target.value })} />}
        {field.fieldTypeCode === 'TEXTAREA' && <Input.TextArea disabled={disabled} rows={4} value={answer?.textValue ?? ''} placeholder={field.placeholder} minLength={field.minimumLength ?? undefined} maxLength={field.maximumLength ?? undefined} onChange={event => patch(field.id, { textValue: event.target.value })} />}
        {field.fieldTypeCode === 'EMAIL' && <Input disabled={disabled} type="email" value={answer?.textValue ?? ''} placeholder={field.placeholder || 'name@example.com'} onChange={event => patch(field.id, { textValue: event.target.value })} />}
        {field.fieldTypeCode === 'PHONE' && <Input disabled={disabled} type="tel" value={answer?.textValue ?? ''} placeholder={field.placeholder || 'Mobile number'} onChange={event => patch(field.id, { textValue: event.target.value })} />}
        {field.fieldTypeCode === 'NUMBER' && <InputNumber disabled={disabled} style={{ width: '100%' }} min={field.minimumNumber ?? undefined} max={field.maximumNumber ?? undefined} value={answer?.decimalValue ?? undefined} placeholder={field.placeholder} onChange={decimalValue => patch(field.id, { decimalValue: decimalValue == null ? null : Number(decimalValue) })} />}
        {field.fieldTypeCode === 'DATE' && <Input disabled={disabled} type="date" min={field.minimumDate?.slice(0, 10)} max={field.maximumDate?.slice(0, 10)} value={answer?.dateValue?.slice(0, 10) ?? ''} onChange={event => patch(field.id, { dateValue: event.target.value })} />}
        {field.fieldTypeCode === 'DATETIME' && <Input disabled={disabled} type="datetime-local" min={field.minimumDate?.slice(0, 16)} max={field.maximumDate?.slice(0, 16)} value={answer?.dateTimeValue?.slice(0, 16) ?? ''} onChange={event => patch(field.id, { dateTimeValue: event.target.value })} />}
        {(field.fieldTypeCode === 'SEARCH_SELECT' || field.fieldTypeCode === 'MULTI_SELECT') && <Select<string | number | Array<string | number>>
          disabled={disabled}
          mode={field.fieldTypeCode === 'MULTI_SELECT' ? 'multiple' : undefined}
          showSearch
          filterOption={!usesLookup}
          optionFilterProp="label"
          loading={searching[field.id]}
          onFocus={() => usesLookup && void search(field, '')}
          onSearch={term => usesLookup && void search(field, term)}
          value={field.fieldTypeCode === 'MULTI_SELECT'
            ? (usesLookup ? answer?.selectedOptionValues ?? [] : answer?.selectedOptionIds ?? [])
            : (usesLookup ? answer?.selectedOptionValues?.[0] : answer?.selectedOptionIds?.[0])}
          placeholder={field.placeholder || 'Select'}
          onChange={selected => {
            const list = Array.isArray(selected) ? selected : selected == null ? [] : [selected]
            patch(field.id, usesLookup
              ? { selectedOptionValues: list.map(String), selectedOptionIds: [] }
              : { selectedOptionIds: list.map(Number), selectedOptionValues: [] })
          }}
          options={usesLookup
            ? (remoteOptions[field.id] ?? []).map(row => ({ value: row.value, label: row.label }))
            : staticOptions}
        />}
        {field.fieldTypeCode === 'RADIO' && <Radio.Group disabled={disabled} value={answer?.selectedOptionIds?.[0]} onChange={event => patch(field.id, { selectedOptionIds: [Number(event.target.value)] })} options={staticOptions} />}
        {field.fieldTypeCode === 'CHECKBOX' && <Checkbox disabled={disabled} checked={Boolean(answer?.booleanValue)} onChange={event => patch(field.id, { booleanValue: event.target.checked })}>{field.placeholder || field.label}</Checkbox>}
        {field.fieldTypeCode === 'UPLOAD' && <div className="public-upload-box">
          {!!uploaded.length && <div>{uploaded.map(file => <Tag icon={<FileDoneOutlined />} color="green" key={file.attachmentPublicId || file.publicId || `${field.id}-${file.originalFileName}`}>{file.originalFileName}</Tag>)}</div>}
          <Upload.Dragger disabled={disabled} multiple={Boolean(field.attachmentConstraints?.allowMultiple)} maxCount={field.attachmentConstraints?.maximumFileCount || 1} accept={uploadAccept(field)} showUploadList={false} customRequest={uploader(field)}><p className="ant-upload-drag-icon"><InboxOutlined /></p><p>{uploaded.length ? 'Add another file if permitted' : 'Choose or drop file here'}</p><small>{uploadRuleSummary(field)}</small></Upload.Dragger>
        </div>}
      </Form.Item>
    })}</div>
  </section>)}</div>
}

export function validateDynamicForm(form: DynamicFormVersion, values: PublicFormValue[], files: PublicUploadedFile[]) {
  const valueMap = new Map(values.map(row => [row.fieldId, row]))
  for (const field of form.sections.flatMap(section => section.fields).filter(field => field.isActive)) {
    const answer = valueMap.get(field.id); const uploaded = files.filter(row => row.fieldId === field.id)
    if (field.isRequired && isEmpty(field, answer, uploaded)) return `${field.label} is required.`
    const text = answer?.textValue?.trim() ?? ''
    if (field.minimumLength && text && text.length < field.minimumLength) return `${field.label} must contain at least ${field.minimumLength} characters.`
    if (field.maximumLength && text.length > field.maximumLength) return `${field.label} cannot exceed ${field.maximumLength} characters.`
    if (field.fieldTypeCode === 'EMAIL' && text && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(text)) return `Enter a valid ${field.label.toLowerCase()}.`
    if (field.fieldTypeCode === 'PHONE' && text && !/^[+\d][\d\s()-]{6,20}$/.test(text)) return `Enter a valid ${field.label.toLowerCase()}.`
    for (const rule of field.validationRules) {
      if (rule.ruleType === 'PATTERN' && text && rule.textValue) {
        try { if (!new RegExp(rule.textValue).test(text)) return rule.errorMessage || `${field.label} is invalid.` } catch { return `The configured validation for ${field.label} is invalid.` }
      }
    }
  }
  return ''
}

function validateUpload(field: DynamicFormField, file: File, existing: PublicUploadedFile[]) {
  const rules = field.attachmentConstraints
  if (!rules) return ''
  if (existing.length >= Math.max(1, rules.maximumFileCount)) return `A maximum of ${rules.maximumFileCount} file(s) is allowed.`
  if (rules.maximumFileSizeBytes > 0 && file.size > rules.maximumFileSizeBytes) return `File size cannot exceed ${formatBytes(rules.maximumFileSizeBytes)}.`
  const extension = `.${file.name.split('.').pop()?.toLowerCase() ?? ''}`
  const extensions = rules.allowedExtensions.map(value => value.startsWith('.') ? value.toLowerCase() : `.${value.toLowerCase()}`)
  if (extensions.length && !extensions.includes(extension)) return `Allowed file types: ${extensions.join(', ')}.`
  if (rules.allowedMimeTypes.length && file.type && !rules.allowedMimeTypes.some(value => value.toLowerCase() === file.type.toLowerCase())) return 'The selected file type is not allowed.'
  const total = existing.reduce((sum, row) => sum + (row.fileSizeBytes ?? 0), 0) + file.size
  if (rules.maximumTotalSizeBytes && total > rules.maximumTotalSizeBytes) return `Combined file size cannot exceed ${formatBytes(rules.maximumTotalSizeBytes)}.`
  return ''
}

function uploadAccept(field: DynamicFormField) {
  const rules = field.attachmentConstraints
  if (!rules) return undefined
  return [...rules.allowedExtensions.map(value => value.startsWith('.') ? value : `.${value}`), ...rules.allowedMimeTypes].join(',') || undefined
}

function uploadRuleSummary(field: DynamicFormField) {
  const rules = field.attachmentConstraints
  if (!rules) return 'Allowed types, size and file count are enforced by the configured secure attachment field.'
  const types = rules.allowedExtensions.length ? rules.allowedExtensions.join(', ') : 'configured file types'
  const size = rules.maximumFileSizeBytes > 0 ? `, up to ${formatBytes(rules.maximumFileSizeBytes)} each` : ''
  return `${types}${size}; maximum ${Math.max(1, rules.maximumFileCount)} file(s).`
}

function formatBytes(bytes: number) {
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`
  return `${(bytes / (1024 * 1024)).toFixed(bytes % (1024 * 1024) ? 1 : 0)} MB`
}

function isEmpty(field: DynamicFormField, answer: PublicFormValue | undefined, files: PublicUploadedFile[]) {
  if (field.fieldTypeCode === 'UPLOAD') return files.length === 0
  if (field.fieldTypeCode === 'CHECKBOX') return !answer?.booleanValue
  if (field.fieldTypeCode === 'NUMBER') return answer?.decimalValue == null && answer?.integerValue == null
  if (field.fieldTypeCode === 'DATE') return !answer?.dateValue
  if (field.fieldTypeCode === 'DATETIME') return !answer?.dateTimeValue
  if (['SEARCH_SELECT', 'MULTI_SELECT'].includes(field.fieldTypeCode)) return field.lookupSourceCode ? !answer?.selectedOptionValues?.length : !answer?.selectedOptionIds?.length
  if (field.fieldTypeCode === 'RADIO') return !answer?.selectedOptionIds?.length
  return !answer?.textValue?.trim()
}
