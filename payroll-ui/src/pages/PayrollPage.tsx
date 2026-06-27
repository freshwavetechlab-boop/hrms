import PayRunsPanel from '../components/PayRunsPanel'

export default function PayrollPage({ mode = 'payrun', runType = 'Regular Run' }: { mode?: 'payrun' | 'adjustments'; runType?: 'Regular Run' | 'Off-cycle Run' }) {
  return <PayRunsPanel mode={mode} initialRunType={runType} />
}
