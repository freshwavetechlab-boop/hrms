import PayRunsPanel from '../components/PayRunsPanel'

export default function PayrollPage({ mode = 'payrun' }: { mode?: 'payrun' | 'adjustments' }) {
  return <PayRunsPanel mode={mode} />
}
