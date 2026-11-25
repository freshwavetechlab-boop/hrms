import { useState } from 'react'
import { Alert, Button, Modal, Tag } from 'antd'
import { EyeOutlined } from '@ant-design/icons'
import { money } from '../utils/salary'
import { simulationPayslipPreview, type SimulationPayslip, type SimulationPayslipContext, type SimulationPayslipLine } from '../utils/salarySimulationPayslip'
import { configuredSimulationAmount, configuredSimulationPayslip, type ConfiguredSimulationPayslip, type SimulationPayslipDesign } from '../utils/simulationConfiguredPayslip'
import './SalarySimulationPayslipPreview.css'

export default function SalarySimulationPayslipPreview(props: SimulationPayslipContext & { clientName: string; design?: SimulationPayslipDesign }) {
  const [open, setOpen] = useState(false)
  const { model, reason: nativeReason } = simulationPayslipPreview(props)
  const configured = model && props.design ? configuredSimulationPayslip(props, model, props.design) : null
  const reason = nativeReason || configured?.reason || ''
  const ready = !!model && (!props.design || !!configured?.model)
  return <>
    {props.design && !ready && reason && !props.loading && <span className="salary-simulation-preview-reason" role="status">{reason}</span>}
    <Button icon={<EyeOutlined />} disabled={!ready} title={reason || 'Preview this current calculation without saving payroll'} onClick={() => setOpen(true)}>Preview payslip</Button>
    <Modal open={open} title="Payslip preview · Simulation" zIndex={1800} width={900} onCancel={() => setOpen(false)} footer={<Button onClick={() => setOpen(false)}>Close preview</Button>} className="salary-simulation-payslip-modal">
      {!ready || !model ? <Alert type="warning" showIcon message="Calculation or design needs review" description={reason} /> : props.design && configured?.model ? <ConfiguredPayslip design={props.design} model={configured.model} baseline={model} clientName={props.clientName} /> : <article className="salary-simulation-payslip" aria-label="Simulation payslip">
        <header><div><h2>{props.clientName || 'Salary simulation'}</h2><p>Salary slip · {model.month}</p></div><Tag color="purple">SIMULATION</Tag></header>
        <p className="salary-simulation-payslip-note">Calculation preview only. This does not save payroll or issue a payslip.</p>
        <dl className="salary-simulation-payslip-facts">
          <div><dt>Employee</dt><dd>{model.employeeName}</dd></div><div><dt>Employee code</dt><dd>{model.employeeCode || 'Template scenario'}</dd></div>
          <div><dt>Template</dt><dd>{model.templateName}</dd></div><div><dt>Work location</dt><dd>{model.location}</dd></div>
          <div><dt>Payroll days</dt><dd>{model.payrollDays ?? 'Not supplied'}</dd></div><div><dt>Present / payable days</dt><dd>{model.presentDays ?? 'Not supplied'} / {model.payableDays ?? 'Not supplied'}</dd></div>
        </dl>
        <div className="salary-simulation-payslip-columns"><LineTable title="Earnings" lines={model.earnings} /><LineTable title="Employee deductions" lines={model.deductions} /></div>
        {!!model.benefits.length && <LineTable title="Employer contributions" lines={model.benefits} />}
        {!!model.otherLines.length && <section className="salary-simulation-payslip-other"><p>Other native payroll lines from effective policies, adjustments or location parts are included below to reconcile the engine totals.</p><LineTable title="Other native payroll lines" lines={model.otherLines} showCategory /></section>}
        <dl className="salary-simulation-payslip-totals"><div><dt>Gross earnings</dt><dd>{money(model.gross)}</dd></div><div><dt>Employee deductions</dt><dd>{money(model.totalDeductions)}</dd></div><div><dt>Employer contributions</dt><dd>{money(model.employerContributions)}</dd></div></dl>
        <div className="salary-simulation-payslip-net"><strong>NET PAY</strong><strong>{money(model.net)}</strong></div>
      </article>}
    </Modal>
  </>
}
function ConfiguredPayslip({ design, model, baseline, clientName }: { design: SimulationPayslipDesign; model: ConfiguredSimulationPayslip; baseline: SimulationPayslip; clientName: string }) {
  const amount = (value: number) => configuredSimulationAmount(value, design.amountDecimalPlaces)
  return <article className={`salary-simulation-payslip${design.simpleLayout ? ' salary-simulation-payslip-simple' : ''}`} aria-label="Configured simulation payslip">
    <header><div>{design.template.showLogo && design.logoDataUrl && <img className="salary-simulation-payslip-logo" src={design.logoDataUrl} alt={design.organizationName || 'Organization logo'} />}<h2>{design.organizationName || design.template.name}</h2><p>Salary slip · {baseline.month}</p>{design.template.showClient && <p>Client: {clientName || 'Not supplied'}</p>}</div><Tag color="purple">SIMULATION</Tag></header>
    <p className="salary-simulation-payslip-note">Configured design with current native simulation amounts. No payroll or payslip is saved.</p>
    <dl className="salary-simulation-payslip-facts"><div><dt>Employee</dt><dd>{baseline.employeeName}</dd></div><div><dt>Employee code</dt><dd>{baseline.employeeCode || 'Not supplied'}</dd></div>{model.information.map((field, index) => <div key={`${field.label}:${index}`}><dt>{field.label}</dt><dd>{field.value}</dd></div>)}</dl>
    <div className="salary-simulation-payslip-columns"><LineTable title="Earnings" lines={model.earnings} amount={amount} showCode={false} /><LineTable title="Deductions" lines={model.deductions} amount={amount} showCode={false} /></div>
    {!!model.benefits.length && <LineTable title="Employer contributions" lines={model.benefits} amount={amount} showCode={false} />}
    <dl className="salary-simulation-payslip-totals"><div><dt>Total earnings</dt><dd>{amount(baseline.gross)}</dd></div><div><dt>Total deductions</dt><dd>{amount(baseline.totalDeductions)}</dd></div><div><dt>Employer contributions</dt><dd>{amount(baseline.employerContributions)}</dd></div></dl>
    <div className="salary-simulation-payslip-net"><strong>NET PAY</strong><strong>{amount(baseline.net)}</strong></div>
    {model.showYtd && <p className="salary-simulation-payslip-note">YTD earnings / deductions / net pay: Not supplied in this simulation.</p>}
    <p className="salary-simulation-payslip-note">{design.template.note}</p>
  </article>
}
function LineTable({ title, lines, showCategory = false, showCode = true, amount = money }: { title: string; lines: SimulationPayslipLine[]; showCategory?: boolean; showCode?: boolean; amount?: (value: number) => string }) {
  return <section className="salary-simulation-payslip-group"><h3>{title}</h3><table><thead><tr><th scope="col">Component</th><th scope="col">Amount (₹)</th></tr></thead><tbody>{lines.length ? lines.map((line, index) => <tr key={`${line.code}:${index}`}><td>{line.name}{showCode && <small>{line.code}{showCategory ? ` · ${line.category}` : ''}</small>}</td><td>{amount(line.amount)}</td></tr>) : <tr><td colSpan={2}>None</td></tr>}</tbody></table></section>
}
