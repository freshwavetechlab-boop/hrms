import { Form, Input, Select } from 'antd'
import type { RecruitmentWorkOrderReview } from '../types/recruitmentCases'
import './RecruitmentWorkOrderFields.css'

type WorkOrderFields = RecruitmentWorkOrderReview

export default function RecruitmentWorkOrderFields({ value, clients, onChange, clientDisabled = false, automaticNumber = false }: {
  value: WorkOrderFields
  clients: Array<{ id: number; name: string }>
  onChange: (patch: Partial<WorkOrderFields>) => void
  clientDisabled?: boolean
  automaticNumber?: boolean
}) {
  return <div className="work-order-fields">
    <Form.Item label="Client" required><Select data-testid="work-order-client" value={value.clientId || undefined} disabled={clientDisabled} showSearch optionFilterProp="label" options={clients.map(client => ({ value: client.id, label: client.name }))} onChange={clientId => onChange({ clientId })} /></Form.Item>
    <Form.Item label="Work order number" required={!automaticNumber} extra={automaticNumber ? 'Leave blank to generate the work order number when saving.' : undefined}><Input data-testid="work-order-number" value={value.workOrderNumber} placeholder={automaticNumber ? 'Generated automatically on save' : undefined} onChange={event => onChange({ workOrderNumber: event.target.value })} /></Form.Item>
    <Form.Item label="Received date & time" required><Input data-testid="work-order-received-at" type="datetime-local" value={value.receivedAtUtc} onChange={event => onChange({ receivedAtUtc: event.target.value })} /></Form.Item>
    <Form.Item label="Status"><Select data-testid="work-order-status" value={value.status} options={['Draft', 'Active', 'On Hold', 'Completed', 'Cancelled'].map(status => ({ value: status }))} onChange={status => onChange({ status })} /></Form.Item>
    <Form.Item className="wide" label="Internal note"><Input.TextArea data-testid="work-order-remarks" rows={3} value={value.remarks} onChange={event => onChange({ remarks: event.target.value })} /></Form.Item>
  </div>
}
