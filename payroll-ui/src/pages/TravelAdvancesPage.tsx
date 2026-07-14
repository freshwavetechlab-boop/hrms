import { useEffect, useMemo, useState } from 'react'
import { Button, Modal } from 'antd'
import DataTable from '../components/DataTable'
import SearchSelect, { selectOptions } from '../components/SearchSelect'
import { getClients } from '../services/payrollService'
import { getTravelAdvances, payTravelAdvance, recoverTravelAdvance, settleTravelAdvance } from '../services/settingsService'
import type { Client, TravelAdvance } from '../types/payroll'

const statuses = ['Approved', 'Partially Paid', 'Paid', 'Partially Settled', 'Settled', 'Recoverable', 'Cancelled']
const money = (value: number) => `Rs ${Number(value || 0).toLocaleString('en-IN')}`
const dateText = (value?: string | null) => value ? new Date(value).toLocaleDateString('en-GB') : '-'
const openBalance = (row: TravelAdvance) => Math.max((row.paidAmount || 0) - (row.settledAmount || 0) - (row.recoverableAmount || 0), 0)

export default function TravelAdvancesPage() {
  const [clients, setClients] = useState<Client[]>([])
  const [rows, setRows] = useState<TravelAdvance[]>([])
  const [clientId, setClientId] = useState(0)
  const [status, setStatus] = useState('')
  const [selected, setSelected] = useState<TravelAdvance | null>(null)
  const [action, setAction] = useState<'pay' | 'settle' | 'recover' | ''>('')
  const [form, setForm] = useState({ amount: 0, mode: 'Bank Transfer', reference: '', date: new Date().toISOString().slice(0, 10), remarks: '' })

  const load = async () => setRows(await getTravelAdvances(clientId, status))
  useEffect(() => { void getClients().then(setClients) }, [])
  useEffect(() => { void load() }, [clientId, status])

  const summary = useMemo(() => ({
    approved: rows.reduce((sum, row) => sum + Number(row.approvedAmount || 0), 0),
    paid: rows.reduce((sum, row) => sum + Number(row.paidAmount || 0), 0),
    open: rows.reduce((sum, row) => sum + openBalance(row), 0)
  }), [rows])

  const openAction = (row: TravelAdvance, nextAction: typeof action) => {
    const defaultAmount = nextAction === 'pay'
      ? Math.max(row.approvedAmount - row.paidAmount, 0)
      : nextAction === 'settle' || nextAction === 'recover'
        ? openBalance(row)
        : 0
    setSelected(row)
    setAction(nextAction)
    setForm({ amount: defaultAmount, mode: row.paymentMode || 'Bank Transfer', reference: row.paymentReference || '', date: new Date().toISOString().slice(0, 10), remarks: '' })
  }

  const saveAction = async () => {
    if (!selected || !action) return
    const response = action === 'pay'
      ? await payTravelAdvance(selected.id, { paidAmount: form.amount, paymentMode: form.mode, paymentReference: form.reference, paidDate: form.date, remarks: form.remarks })
      : action === 'settle'
        ? await settleTravelAdvance(selected.id, { settledAmount: form.amount, remarks: form.remarks })
        : await recoverTravelAdvance(selected.id, { recoverableAmount: form.amount, remarks: form.remarks })
    if (response.ok) {
      setSelected(null)
      setAction('')
      await load()
    }
  }

  return <section className="travel-advances-page">
    <div className="card report-workspace travel-advance-workspace">
      <div className="travel-advance-filters">
        <label><span>Client</span><SearchSelect value={clientId} onChange={value => setClientId(Number(value))} options={selectOptions(clients.map(client => ({ value: client.id, label: client.name })), 'All clients', 0)} /></label>
        <label><span>Status</span><SearchSelect value={status} onChange={setStatus} options={selectOptions(statuses, 'All statuses')} /></label>
        <Button onClick={() => void load()}>Refresh</Button>
      </div>
      <div className="travel-advance-summary">
        <article><span>Approved</span><b>{money(summary.approved)}</b></article>
        <article><span>Paid</span><b>{money(summary.paid)}</b></article>
        <article><span>Open advance</span><b>{money(summary.open)}</b></article>
      </div>
    </div>
    <div className="card report-result">
      <DataTable
        rows={rows}
        getRowId={row => row.id}
        exportFileName="travel-advances"
        emptyText="No travel advances found."
        columns={[
          { key: 'requestNumber', label: 'Travel request', render: row => <><b>{row.requestNumber}</b><small>{row.clientName}</small></>, value: row => row.requestNumber },
          { key: 'employeeName', label: 'Employee', render: row => <><b>{row.employeeName}</b><small>{row.employeeCode}</small></>, value: row => `${row.employeeCode} ${row.employeeName}` },
          { key: 'approvedAmount', label: 'Approved', render: row => money(row.approvedAmount), value: row => row.approvedAmount },
          { key: 'paidAmount', label: 'Paid', render: row => money(row.paidAmount), value: row => row.paidAmount },
          { key: 'settledAmount', label: 'Settled', render: row => money(row.settledAmount), value: row => row.settledAmount },
          { key: 'open', label: 'Open', render: row => money(openBalance(row)), value: row => openBalance(row) },
          { key: 'dueDate', label: 'Due date', render: row => dateText(row.dueDate), value: row => row.dueDate || '' },
          { key: 'status', label: 'Status', render: row => <span className={`task-status ${row.status.toLowerCase().replace(/\s+/g, '-')}`}>{row.status}</span>, value: row => row.status }
        ]}
        actions={row => <div className="travel-advance-actions">
          {row.approvedAmount > row.paidAmount && !['Cancelled', 'Settled'].includes(row.status) && <Button size="small" type="primary" onClick={() => openAction(row, 'pay')}>Pay</Button>}
          {openBalance(row) > 0 && <Button size="small" onClick={() => openAction(row, 'settle')}>Settle</Button>}
          {openBalance(row) > 0 && <Button size="small" danger onClick={() => openAction(row, 'recover')}>Recover</Button>}
        </div>}
      />
    </div>
    <Modal title={action === 'pay' ? 'Pay travel advance' : action === 'settle' ? 'Settle travel advance' : 'Mark advance recoverable'} open={!!selected && !!action} onCancel={() => { setSelected(null); setAction('') }} onOk={() => void saveAction()} okText={action === 'pay' ? 'Record payment' : action === 'settle' ? 'Settle' : 'Mark recoverable'} width={620}>
      {selected && <div className="travel-advance-modal">
        <div className="travel-advance-context"><b>{selected.requestNumber}</b><span>{selected.employeeName} / {selected.employeeCode}</span><span>Open amount: {money(openBalance(selected))}</span></div>
        <label><span>Amount</span><input type="number" min={0} value={form.amount} onChange={event => setForm({ ...form, amount: Number(event.target.value || 0) })} /></label>
        {action === 'pay' && <><label><span>Payment mode</span><select value={form.mode} onChange={event => setForm({ ...form, mode: event.target.value })}><option>Bank Transfer</option><option>Cash</option><option>Company Card</option><option>Cheque</option></select></label><label><span>Payment reference</span><input value={form.reference} onChange={event => setForm({ ...form, reference: event.target.value })} /></label><label><span>Paid date</span><input type="date" value={form.date} onChange={event => setForm({ ...form, date: event.target.value })} /></label></>}
        <label><span>Remarks</span><textarea value={form.remarks} onChange={event => setForm({ ...form, remarks: event.target.value })} /></label>
      </div>}
    </Modal>
  </section>
}
