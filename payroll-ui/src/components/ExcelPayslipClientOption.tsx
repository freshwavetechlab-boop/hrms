import { Tag } from 'antd'
import './ExcelPayslipClientOption.css'

export default function ExcelPayslipClientOption({ name, hasSavedBatch }: { name: string; hasSavedBatch: boolean }) {
  return <span className="excel-payslip-client-option-label"><span>{name}</span>{hasSavedBatch && <Tag color="green">Saved batch</Tag>}</span>
}
