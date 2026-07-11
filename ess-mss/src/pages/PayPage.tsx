import { useEffect, useState } from 'react'
import type { LoadState, Payslip, PayslipDocument, User } from '../types'
import { essApi } from '../services/essApi'

const money = (value: number) => `Rs ${Number(value || 0).toLocaleString('en-IN')}`

const dateText = (value?: string) => {
  if (!value) return '-'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '-' : date.toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' })
}

const periodText = (period: string) => {
  const date = new Date(`${period}-01T00:00:00`)
  return Number.isNaN(date.getTime()) ? period : date.toLocaleDateString('en-IN', { month: 'short', year: 'numeric' })
}

export function PayPage({ user }: { user: User }) {
  const [rows, setRows] = useState<Payslip[]>([])
  const [document, setDocument] = useState<PayslipDocument | null>(null)
  const [state, setState] = useState<LoadState>('loading')
  const [busy, setBusy] = useState<string | null>(null)

  useEffect(() => {
    setState('loading')
    void essApi.payslips()
      .then(items => { setRows(items); setState('ready') })
      .catch(() => setState('error'))
  }, [user.email])

  const fetchDocument = async (row: Payslip) => {
    setBusy(`view-${row.payRunId}`)
    try {
      const next = await essApi.payslipDocument(row.payRunId)
      setDocument(next)
    } finally {
      setBusy(null)
    }
  }

  const downloadDocument = async (row: Payslip) => {
    setBusy(`download-${row.payRunId}`)
    try {
      const next = await essApi.payslipDocument(row.payRunId)
      const link = window.document.createElement('a')
      link.href = URL.createObjectURL(new Blob([next.html], { type: 'text/html' }))
      link.download = next.fileName || `payslip-${next.payPeriod}.html`
      link.click()
      URL.revokeObjectURL(link.href)
    } finally {
      setBusy(null)
    }
  }

  return (
    <section className="pay-workspace">
      <div className="feature-heading">
        <span className="eyebrow">My pay</span>
        <h3>Payslips & payment history</h3>
        <p>Access payroll periods that have been approved for you.</p>
      </div>

      {state === 'loading' && <div className="empty-work"><span>Loading payslips...</span></div>}
      {state === 'error' && <div className="empty-work"><b>Pay information is unavailable.</b><span>Contact payroll if you expect a payslip for a completed pay run.</span></div>}

      {state === 'ready' && (
        <div className="pay-table-card">
          {rows.length ? (
            <div className="pay-table-scroll">
              <table className="pay-table">
                <thead>
                  <tr>
                    <th>Pay period</th>
                    <th>Pay date</th>
                    <th>Gross pay</th>
                    <th>Deductions</th>
                    <th>Net pay</th>
                    <th>Payment</th>
                    <th>Run status</th>
                    <th>Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map(item => {
                    const deductions = item.statutoryDeductions + item.oneTimeDeductions
                    return (
                      <tr key={item.payRunId}>
                        <td><b>{periodText(item.payPeriod)}</b><span>{item.payPeriod}</span></td>
                        <td>{dateText(item.payDate)}</td>
                        <td>{money(item.grossPay)}</td>
                        <td>{money(deductions)}</td>
                        <td><b>{money(item.netPay)}</b></td>
                        <td><span className="pay-status-pill">{item.paymentStatus || 'Pending'}</span></td>
                        <td>{item.runStatus}</td>
                        <td>
                          <div className="pay-actions">
                            <button type="button" onClick={() => void fetchDocument(item)} disabled={busy !== null}>
                              {busy === `view-${item.payRunId}` ? 'Opening...' : 'View'}
                            </button>
                            <button type="button" className="secondary" onClick={() => void downloadDocument(item)} disabled={busy !== null}>
                              {busy === `download-${item.payRunId}` ? 'Preparing...' : 'Download'}
                            </button>
                          </div>
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          ) : (
            <div className="empty-work"><b>No payslips are available yet.</b><span>Your payslips appear here after payroll approves your pay run.</span></div>
          )}
        </div>
      )}

      {document && (
        <div className="ess-modal-backdrop" onClick={() => setDocument(null)}>
          <section className="ess-payslip-modal" onClick={event => event.stopPropagation()}>
            <header>
              <div>
                <span className="eyebrow">Payslip</span>
                <h3>{periodText(document.payPeriod)}</h3>
                <p>{document.employeeCode}</p>
              </div>
              <button type="button" onClick={() => setDocument(null)}>x</button>
            </header>
            <iframe title={`Payslip ${document.payPeriod}`} srcDoc={document.html} />
            <button type="button" className="download-payslip" onClick={() => {
              const link = window.document.createElement('a')
              link.href = URL.createObjectURL(new Blob([document.html], { type: 'text/html' }))
              link.download = document.fileName || `payslip-${document.payPeriod}.html`
              link.click()
              URL.revokeObjectURL(link.href)
            }}>Download payslip</button>
          </section>
        </div>
      )}
    </section>
  )
}
