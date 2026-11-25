import { Tag } from 'antd'
import { ClientOptionName } from './ClientOption'
import './ExcelPayslipClientOption.css'

export default function ExcelPayslipClientOption({ name, code, hasSavedBatch, badgeLabel = 'Saved batch' }: { name: string; code?: string; hasSavedBatch: boolean; badgeLabel?: string }) {
  return <span className="excel-payslip-client-option-label"><ClientOptionName name={name} code={code} />{hasSavedBatch && <Tag color="green">{badgeLabel}</Tag>}</span>
}
