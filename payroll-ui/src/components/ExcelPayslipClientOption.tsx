import { Tag } from 'antd'
import './ExcelPayslipClientOption.css'

export default function ExcelPayslipClientOption({ name, hasSavedBatch, badgeLabel = 'Saved batch' }: { name: string; hasSavedBatch: boolean; badgeLabel?: string }) {
  return <span className="excel-payslip-client-option-label"><span>{name}</span>{hasSavedBatch && <Tag color="green">{badgeLabel}</Tag>}</span>
}
