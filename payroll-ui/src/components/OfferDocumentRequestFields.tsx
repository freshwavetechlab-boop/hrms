import { Alert, Button, Checkbox, Skeleton, Switch } from 'antd'
import type { OfferDocumentRequestField, OfferDocumentRequestSelection } from '../services/recruitmentOfferMailService'
import './OfferDocumentRequestFields.css'

export type OfferDocumentRequestFieldsProps = {
  fields: OfferDocumentRequestField[]
  value: OfferDocumentRequestSelection[]
  onChange: (value: OfferDocumentRequestSelection[]) => void
  loading?: boolean
  error?: string
  disabled?: boolean
  onRetry?: () => void
}

export default function OfferDocumentRequestFields({ fields, value, onChange, loading = false, error = '', disabled = false, onRetry }: OfferDocumentRequestFieldsProps) {
  const requested = new Map(value.map(item => [item.fieldConfigurationId, item.required]))
  return <section className="offer-document-requests" aria-label="Requested documents">
    <div className="offer-document-requests-heading"><strong>Documents to request</strong><span>{value.length} selected</span></div>
    {error && <Alert type="error" showIcon message={error} action={onRetry && !disabled ? <Button size="small" onClick={onRetry}>Retry</Button> : undefined} />}
    {loading ? <Skeleton active paragraph={{ rows: 3 }} /> : <>
      <div className="offer-document-request-list" role="table" aria-label="Offer document requests">
        <div className="offer-document-request-head" role="row"><span role="columnheader">Document</span><span role="columnheader">Requested</span><span role="columnheader">Mandatory</span></div>
        {fields.map(field => <div className="offer-document-request-row" role="row" key={field.fieldConfigurationId}>
          <span role="cell">{field.label}</span>
          <span role="cell"><Checkbox aria-label={`Request ${field.label}`} checked={requested.has(field.fieldConfigurationId)} disabled={disabled} onChange={event => onChange(event.target.checked
            ? [...value.filter(item => item.fieldConfigurationId !== field.fieldConfigurationId), { fieldConfigurationId: field.fieldConfigurationId, required: true }]
            : value.filter(item => item.fieldConfigurationId !== field.fieldConfigurationId))} /></span>
          <span role="cell"><Switch aria-label={`${field.label} mandatory`} checked={requested.get(field.fieldConfigurationId) === true} disabled={disabled || !requested.has(field.fieldConfigurationId)} size="small" onChange={required => onChange(value.map(item => item.fieldConfigurationId === field.fieldConfigurationId ? { ...item, required } : item))} /></span>
        </div>)}
      </div>
      {!value.length && fields.length > 0 && <p className="offer-document-request-required" role="status">Select at least one document to include the upload link.</p>}
    </>}
  </section>
}
