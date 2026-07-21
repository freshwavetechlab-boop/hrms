import { useEffect, useMemo, useState } from 'react'
import type { LoadState, Payslip, PayslipDocument, User } from '../types'
import { essApi } from '../services/essApi'
import { downloadHtmlPdf } from '../utils/htmlPdf'

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
  const [selectedPeriod, setSelectedPeriod] = useState('')

  useEffect(() => {
    setState('loading')
    void essApi.payslips()
      .then(items => { setRows(items); setState('ready') })
      .catch(() => setState('error'))
  }, [user.email])
  useEffect(() => {
    window.dispatchEvent(new CustomEvent('ess:page-title', { detail: { section: 'Pay & tax', title: 'Payslips' } }))
  }, [])
  const periods = useMemo(() => Array.from(new Set(rows.map(row => row.payPeriod))).sort((a, b) => b.localeCompare(a)), [rows])
  const visibleRows = useMemo(() => selectedPeriod ? rows.filter(row => row.payPeriod === selectedPeriod) : rows, [rows, selectedPeriod])

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
      await downloadHtmlPdf(next.html, next.fileName || `payslip-${next.payPeriod}`)
    } finally {
      setBusy(null)
    }
  }

  const downloadOpenDocument = async () => {
    if (!document) return
    setBusy(`modal-${document.payRunId}`)
    try {
      await downloadHtmlPdf(document.html, document.fileName || `payslip-${document.payPeriod}`)
    } finally {
      setBusy(null)
    }
  }

  return (
    <section className="pay-workspace">
      {state === 'loading' && <div className="empty-work"><span>Loading payslips...</span></div>}
      {state === 'error' && <div className="empty-work"><b>Pay information is unavailable.</b><span>Contact payroll if you expect a payslip for a completed pay run.</span></div>}

      {state === 'ready' && (
        <>
          <div className="pay-filter-bar">
            <label>
              <span>Pay month</span>
              <select value={selectedPeriod} onChange={event => setSelectedPeriod(event.target.value)}>
                <option value="">All available months</option>
                {periods.map(period => <option value={period} key={period}>{periodText(period)}</option>)}
              </select>
            </label>
            <small>{visibleRows.length} payslip{visibleRows.length === 1 ? '' : 's'} shown</small>
          </div>
          <div className="pay-table-card">
          {rows.length ? (
            <div className="pay-table-scroll">
              <table className="pay-table mobile-card-table">
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
                  {visibleRows.map(item => {
                    const deductions = item.statutoryDeductions + item.oneTimeDeductions
                    return (
                      <tr key={item.payRunId}>
                        <td data-label="Pay period"><div><b>{periodText(item.payPeriod)}</b><span>{item.payPeriod}</span></div></td>
                        <td data-label="Pay date">{dateText(item.payDate)}</td>
                        <td data-label="Gross pay">{money(item.grossPay)}</td>
                        <td data-label="Deductions">{money(deductions)}</td>
                        <td data-label="Net pay"><b>{money(item.netPay)}</b></td>
                        <td data-label="Payment"><span className="pay-status-pill">{item.paymentStatus || 'Pending'}</span></td>
                        <td data-label="Run status">{item.runStatus}</td>
                        <td data-label="Actions">
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
        </>
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
            <button type="button" className="download-payslip" disabled={busy !== null} onClick={() => void downloadOpenDocument()}>
              {busy === `modal-${document.payRunId}` ? 'Preparing PDF...' : 'Download PDF'}
            </button>
          </section>
        </div>
      )}
    </section>
  )
}
