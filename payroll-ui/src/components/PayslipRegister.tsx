import { useEffect, useMemo, useState } from 'react'
import { getClients, getEmployees, getPayRun, getPayRuns } from '../services/payrollService'
import { getOrganization, getWorkLocations } from '../services/settingsService'
import { org0 } from '../data/payrollDefaults'
import type { Client, Employee, Org, PayRun, PayRunSalaryLine, RunEmployee, WorkLocation } from '../types/payroll'
import { money } from '../utils/salary'
import DataTable from './DataTable'
import SearchSelect, { selectOptions } from './SearchSelect'
import './PayslipRegister.css'

type PayslipLine = { id: string; name: string; category: string; monthlyAmount: number; amount: number; proRata: boolean }
type PayslipContext = { employee?: Employee; location?: WorkLocation; earnings: PayslipLine[]; deductions: PayslipLine[]; earningTotal: number; deductionTotal: number; netPay: number }

const lineNumber = (value: unknown) => Number(String(value ?? '').replace(/,/g, '')) || 0
const clean = (value: unknown, fallback = '-') => String(value ?? '').trim() || fallback
const escapeHtml = (value: unknown) => String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char] ?? char))
const amount = (value: number) => Number(value || 0).toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })

function parseLines(row: RunEmployee): PayslipLine[] {
  try {
    const parsed = JSON.parse(row.detailsJson || '[]') as PayRunSalaryLine[]
    return parsed.map(item => ({
      id: clean(item.id ?? item.Id, ''),
      name: clean(item.name ?? item.Name, 'Salary component'),
      category: clean(item.category ?? item.Category, ''),
      monthlyAmount: lineNumber(item.monthlyAmount),
      amount: lineNumber(item.amount),
      proRata: Boolean(item.proRata ?? item.ProRata)
    })).filter(item => item.id && !['GROSS_EARNED', 'NET_PAY', 'EMPLOYER_COST'].includes(item.id.toUpperCase()))
  } catch {
    return []
  }
}

function dateText(value: string | Date | null | undefined) {
  if (!value) return '-'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '-'
  return new Intl.DateTimeFormat('en-US', { month: 'short', day: '2-digit', year: 'numeric' }).format(date)
}

function periodRange(payPeriod: string) {
  const [year, month] = payPeriod.split('-').map(Number)
  if (!year || !month) return payPeriod || '-'
  return `${dateText(new Date(year, month - 1, 1))} - ${dateText(new Date(year, month, 0))}`
}

function periodTitle(payPeriod: string) {
  const [year, month] = payPeriod.split('-').map(Number)
  if (!year || !month) return payPeriod || 'Payslip'
  return new Intl.DateTimeFormat('en-US', { month: 'long', year: 'numeric' }).format(new Date(year, month - 1, 1))
}

const ones = ['', 'one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight', 'nine', 'ten', 'eleven', 'twelve', 'thirteen', 'fourteen', 'fifteen', 'sixteen', 'seventeen', 'eighteen', 'nineteen']
const tens = ['', '', 'twenty', 'thirty', 'forty', 'fifty', 'sixty', 'seventy', 'eighty', 'ninety']
function belowThousand(value: number): string {
  const words: string[] = []
  if (value >= 100) { words.push(`${ones[Math.floor(value / 100)]} hundred`); value %= 100 }
  if (value >= 20) { words.push(tens[Math.floor(value / 10)]); value %= 10 }
  if (value > 0) words.push(ones[value])
  return words.join(' ')
}
function amountInWords(value: number) {
  let number = Math.round(value || 0)
  if (!number) return 'Zero only'
  const parts: string[] = []
  const crore = Math.floor(number / 10000000); number %= 10000000
  const lakh = Math.floor(number / 100000); number %= 100000
  const thousand = Math.floor(number / 1000); number %= 1000
  if (crore) parts.push(`${belowThousand(crore)} crore`)
  if (lakh) parts.push(`${belowThousand(lakh)} lakh`)
  if (thousand) parts.push(`${belowThousand(thousand)} thousand`)
  if (number) parts.push(belowThousand(number))
  return `${parts.join(' ')} only`.replace(/\b\w/g, char => char.toUpperCase())
}

function createContext(selected: RunEmployee, employees: Employee[], locations: WorkLocation[]): PayslipContext {
  const employee = employees.find(item => item.id === selected.employeeId)
  const location = locations.find(item => item.id === employee?.workLocationId)
  const lines = parseLines(selected)
  const earnings = lines.filter(item => ['Earning', 'Reimbursement'].includes(item.category) && item.amount > 0)
  const deductions = lines.filter(item => /deduction/i.test(item.category) && item.amount > 0)
  if (selected.oneTimeEarnings > 0 && !earnings.some(item => item.id === 'ONE_TIME_EARNINGS')) earnings.push({ id: 'ONE_TIME_EARNINGS', name: 'One Time Earnings', category: 'Earning', monthlyAmount: selected.oneTimeEarnings, amount: selected.oneTimeEarnings, proRata: false })
  if (selected.oneTimeDeductions > 0 && !deductions.some(item => item.id === 'ONE_TIME_DEDUCTIONS')) deductions.push({ id: 'ONE_TIME_DEDUCTIONS', name: 'One Time Deductions', category: 'Deduction', monthlyAmount: selected.oneTimeDeductions, amount: selected.oneTimeDeductions, proRata: false })
  return { employee, location, earnings, deductions, earningTotal: selected.grossPay + selected.oneTimeEarnings, deductionTotal: selected.statutoryDeductions + selected.oneTimeDeductions, netPay: selected.netPay }
}

function payrollDays(run: PayRun, selected: RunEmployee) {
  const lop = Math.max(0, Number(run.totalWorkingDays || 0) - Number(selected.payableDays || 0))
  const leaveDays = (selected.leaveBreakdown ?? []).reduce((sum, item) => sum + Number(item.days || 0), 0)
  return { workingDays: run.totalWorkingDays || 0, leaves: leaveDays, lop }
}

function renderRows(earnings: PayslipLine[], deductions: PayslipLine[]) {
  const rows = Array.from({ length: Math.max(earnings.length, deductions.length, 1) }, (_, index) => ({ earning: earnings[index], deduction: deductions[index] }))
  return rows.map(({ earning, deduction }) => `<tr><td>${escapeHtml(earning?.name ?? '')}</td><td class="num">${earning ? amount(earning.monthlyAmount) : ''}</td><td class="num">${earning ? amount(earning.amount) : ''}</td><td>${escapeHtml(deduction?.name ?? '')}</td><td class="num">${deduction ? amount(deduction.amount) : ''}</td></tr>`).join('')
}

function detail(label: string, value: unknown) {
  return `<div class="detail"><span>${escapeHtml(label)}</span><strong>${escapeHtml(clean(value))}</strong></div>`
}

function payslipCss() {
  return `@page{size:A4;margin:12mm}*{box-sizing:border-box}body{margin:0;background:#fff;color:#000;font-family:Arial,Helvetica,sans-serif;font-size:11px}.slip{width:720px;margin:18px auto 0}.slip-head{display:grid;grid-template-columns:150px 1fr 90px;align-items:center;min-height:118px}.logo{width:120px;height:86px;display:grid;place-items:center}.logo img{max-width:120px;max-height:86px;object-fit:contain}.logo b{color:#2f80bd}.slip-head h1{margin:0;text-align:center;font-size:14px;font-weight:800;text-transform:uppercase}.slip-head p{margin:2px 0;text-align:center;font-size:12px}.slip h2{margin:8px 0 14px;text-align:center;color:#337fb6;font-size:15px}.info,.salary,.net{width:100%;border-collapse:collapse}.info th,.salary th{padding:7px 8px;border:1px solid #5c9bd3;background:#eaf4fb;color:#2f80bd;text-align:left;font-weight:800}.info td{width:50%;vertical-align:top;padding:9px 10px;border:1px solid #5c9bd3}.detail{display:grid;grid-template-columns:78px 1fr;gap:7px;margin:0 0 5px;line-height:1.12}.detail strong{font-weight:800}.salary{margin-top:14px}.salary th:nth-child(1),.salary th:nth-child(4){width:20%}.salary th:nth-child(2),.salary th:nth-child(3),.salary th:nth-child(5){width:20%}.salary td{height:23px;padding:4px 8px;border-left:1px solid #5c9bd3;border-right:1px solid #5c9bd3;font-weight:700}.salary tbody tr:first-child td{border-top:1px solid #5c9bd3}.salary tbody tr:last-child td{border-bottom:1px solid #5c9bd3}.salary .total td{border-top:1px solid #5c9bd3;border-bottom:1px solid #5c9bd3;font-weight:800}.num{text-align:right}.net{width:255px;margin-top:14px}.net td{padding:4px 8px;border-top:1px solid #5c9bd3;border-bottom:1px solid #5c9bd3;font-size:12px;font-weight:800}.words{margin:5px 0 0;font-weight:800}@media print{body{print-color-adjust:exact;-webkit-print-color-adjust:exact}.slip{margin:0 auto}}`
}

function payslipHtml(org: Org, run: PayRun, selected: RunEmployee, context: PayslipContext) {
  const employee = context.employee, personal = employee?.personalDetails, payment = employee?.paymentDetails, days = payrollDays(run, selected)
  const companyName = clean(org.legalName || org.name || run.clientName, run.clientName || 'Organization')
  const companyAddress = [org.addressLine1, org.addressLine2, org.city, org.state, org.postalCode].filter(Boolean).join(', ')
  const logo = org.logoDataUrl ? `<img src="${org.logoDataUrl}" alt="Logo" />` : '<b>LOGO</b>'
  return `<!doctype html><html><head><meta charset="utf-8"><title>Payslip ${escapeHtml(run.payPeriod)} - ${escapeHtml(selected.employeeCode)}</title><style>${payslipCss()}</style></head><body><main class="slip"><header class="slip-head"><div class="logo">${logo}</div><div><h1>${escapeHtml(companyName)}</h1><p>${escapeHtml(companyAddress)}</p><p>${escapeHtml(org.country || 'India')}</p></div></header><h2>Payslip - ${escapeHtml(periodTitle(run.payPeriod))}</h2><table class="info"><thead><tr><th>Employee Details</th><th>Salary Details</th></tr></thead><tbody><tr><td>${detail('Name', selected.employeeName)}${detail('Email Id', employee?.workEmail)}${detail('Emp Code', selected.employeeCode)}${detail('Designation', employee?.designation)}${detail('Date of Joining', dateText(employee?.dateOfJoining))}${detail('Address', personal?.address)}${detail('Location', context.location?.name || personal?.sourceLocation)}${detail('PF Account #', '-')}${detail('PAN', personal?.panNumber)}${detail('UAN', personal?.uanNumber)}${detail('Bank', payment?.bankName)}${detail('Account #', payment?.bankAccountNo)}</td><td>${detail('Salary Period', periodRange(run.payPeriod))}${detail('Att. Period', periodRange(run.payPeriod))}${detail('Working Days', run.totalWorkingDays || 0)}${detail('Leaves', amount(days.leaves))}${detail('LOP', amount(days.lop))}${detail('OT Hours', '0.00')}</td></tr></tbody></table><table class="salary"><thead><tr><th>Earnings</th><th>Rate</th><th>Actual</th><th>Deductions</th><th>Amount</th></tr></thead><tbody>${renderRows(context.earnings, context.deductions)}<tr class="total"><td>Earning Total</td><td class="num">${amount(context.earnings.reduce((sum, item) => sum + item.monthlyAmount, 0))}</td><td class="num">${amount(context.earningTotal)}</td><td>Deduction Total</td><td class="num">${amount(context.deductionTotal)}</td></tr></tbody></table><table class="net"><tbody><tr><td>Net Pay (INR) :</td><td class="num">${amount(context.netPay)}</td></tr></tbody></table><p class="words">${escapeHtml(amountInWords(context.netPay))}</p></main></body></html>`
}

export default function PayslipRegister() {
  const [clients, setClients] = useState<Client[]>([])
  const [runs, setRuns] = useState<PayRun[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [org, setOrg] = useState<Org>(org0)
  const [clientId, setClientId] = useState(0)
  const [runId, setRunId] = useState(0)
  const [run, setRun] = useState<PayRun | null>(null)
  const [selected, setSelected] = useState<RunEmployee | null>(null)

  useEffect(() => {
    void Promise.all([getClients(), getPayRuns(), getEmployees(), getWorkLocations(), getOrganization(org0)]).then(([clientRows, runRows, employeeRows, locationRows, organization]) => {
      const active = clientRows.filter(row => row.isActive)
      const activeClientIds = new Set(active.map(client => client.id))
      setClients(active)
      setClientId(active[0]?.id ?? 0)
      setRuns(runRows)
      setEmployees(employeeRows.filter(employee => activeClientIds.has(employee.clientId)))
      setLocations(locationRows.filter(location => location.isActive && activeClientIds.has(location.clientId)))
      setOrg({ ...org0, ...organization })
    })
  }, [])

  const clientRuns = useMemo(() => runs.filter(item => item.clientId === clientId && ['Approved', 'Partially Paid', 'Paid'].includes(item.status)), [runs, clientId])
  const payslipContext = selected ? createContext(selected, employees, locations) : null

  useEffect(() => {
    const available = clientRuns.some(item => item.id === runId) ? runId : clientRuns[0]?.id ?? 0
    setRunId(available)
    setRun(null)
    setSelected(null)
    if (available) void getPayRun(available).then(setRun)
  }, [clientRuns, runId])

  const download = () => {
    if (!run || !selected || !payslipContext) return
    const body = payslipHtml(org, run, selected, payslipContext)
    const link = document.createElement('a')
    link.href = URL.createObjectURL(new Blob([body], { type: 'text/html' }))
    link.download = `payslip-${selected.employeeCode}-${run.payPeriod}.html`
    link.click()
    URL.revokeObjectURL(link.href)
  }

  const printPreview = () => {
    if (!run || !selected || !payslipContext) return
    const popup = window.open('', '_blank')
    if (!popup) return
    popup.document.write(payslipHtml(org, run, selected, payslipContext))
    popup.document.close()
    popup.focus()
  }

  return (
    <section className="payslip-register">
      <section className="card report-workspace">
        <header><div><span className="eyebrow purple">Payroll reports</span><h3>Payslip Register</h3><p>Published payroll results by employee. Preview or download an individual payslip record.</p></div></header>
        <div className="payslip-filters">
          <label><span>Client</span><SearchSelect value={clientId} onChange={value => setClientId(Number(value))} options={clients.map(client => ({ value: client.id, label: client.name }))} /></label>
          <label><span>Pay period</span><SearchSelect value={runId} onChange={value => setRunId(Number(value))} options={selectOptions(clientRuns.map(item => ({ value: item.id, label: `${item.payPeriod} - ${item.status}` })), 'Select approved pay run', 0)} /></label>
        </div>
      </section>
      {run && <section className="card payslip-list"><DataTable rows={run.employees.filter(employee => !employee.isSkipped)} getRowId={row => row.employeeId} exportFileName={`payslip-register-${run.payPeriod}`} columns={[{ key: 'employee', label: 'Employee', value: row => row.employeeName, render: row => <>{row.employeeName}<small>{row.employeeCode}</small></> }, { key: 'department', label: 'Department' }, { key: 'grossPay', label: 'Gross', value: row => money(row.grossPay) }, { key: 'deductions', label: 'Deductions', value: row => money(row.statutoryDeductions + row.oneTimeDeductions) }, { key: 'netPay', label: 'Net pay', value: row => money(row.netPay) }, { key: 'paymentStatus', label: 'Payment' }]} actions={row => <button type="button" onClick={() => setSelected(row)}>Preview</button>} /></section>}
      {!run && <section className="card report-empty"><p>No approved pay run is available for this client.</p></section>}
      {selected && run && payslipContext && <div className="payslip-modal-backdrop" onClick={() => setSelected(null)}><section className="payslip-preview-panel payslip-document-panel" role="dialog" aria-modal="true" aria-label="Payslip preview" onClick={event => event.stopPropagation()}><header><div><span className="eyebrow purple">{run.payPeriod}</span><h3>{selected.employeeName}</h3><p>{selected.employeeCode} - {selected.department}</p></div><button type="button" className="payslip-close" onClick={() => setSelected(null)}>x</button></header><iframe title="Payslip document preview" srcDoc={payslipHtml(org, run, selected, payslipContext)} /><footer><small>Payment status: <b>{selected.paymentStatus}</b></small><div><button type="button" onClick={printPreview}>Open printable</button><button type="button" onClick={download}>Download payslip</button></div></footer></section></div>}
    </section>
  )
}
