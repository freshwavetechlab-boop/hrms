import BulkMailProgress, { type BulkMailProgressProps } from './BulkMailProgress'

// Preserve the payslip component API and wording while sharing its progress UI.
export default function ExcelPayslipMailProgress(props: Omit<BulkMailProgressProps, 'documentLabel' | 'documentPlural'>) {
  return <BulkMailProgress {...props} />
}
