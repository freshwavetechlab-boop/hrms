import { useEffect, useState } from 'react'
import { Alert, Spin } from 'antd'
import { getPayslipDesign, previewPayslipDesignPdf } from '../services/payslipService'
import type { Component, Org, PayslipTemplate } from '../types/payroll'
import { money } from '../utils/salary'
import PdfCanvasPreview from './PdfCanvasPreview'

export function SavedPayslipDesignSample({ clientId, templateId }: { clientId: number; templateId: number }) {
  const [pdf, setPdf] = useState<Blob | null>(null)
  const [error, setError] = useState('')
  useEffect(() => {
    const controller = new AbortController()
    const { signal } = controller
    async function load() {
      const saved = await getPayslipDesign(clientId, templateId, { signal })
      if (signal.aborted) return
      const profile = saved.data
      if (!saved.ok || !profile) throw new Error(saved.error || 'This template has no saved payslip design to preview.')
      if (profile.template.id !== templateId || profile.template.clientId !== clientId) throw new Error('The saved design does not belong to the selected client and template.')
      const result = await previewPayslipDesignPdf({
        requestId: '', reviewed: false, template: profile.template, sourceKind: profile.sourceKind,
        sourceBatchId: profile.sourceBatchId, sourceHash: profile.sourceHash, existingProfileHash: profile.profileHash,
        fields: profile.fields, amountDecimalPlaces: profile.amountDecimalPlaces, payPeriods: profile.payPeriods,
        simpleLayout: profile.simpleLayout, keepMappedZeroRows: profile.keepMappedZeroRows,
      }, { signal })
      if (signal.aborted) return
      if (!result.ok || !result.data) throw new Error(result.error || 'Unable to preview this saved design.')
      setPdf(result.data.blob)
    }
    void load().catch(reason => { if (!signal.aborted) setError(reason instanceof Error ? reason.message : 'Unable to load the saved design.') })
    return () => controller.abort()
  }, [clientId, templateId])

  if (error) return <Alert type="error" showIcon message={error} />
  if (!pdf) return <div className="local-pdf-loading" role="status"><Spin />Loading saved design…</div>
  return <PdfCanvasPreview blob={pdf} title="Saved payslip design preview" />
}

export function LegacyPayslipSample({ template, org, clientName, annualCtc, components }: {
  template: PayslipTemplate
  org: Org
  clientName: string
  annualCtc?: string
  components: Component[]
}) {
  const monthly = Number(annualCtc || 600000) / 12
  const lines = components.filter(item => item.active).slice(0, 6).map((component, index) => ({
    component,
    amount: component.category === 'Deduction' ? monthly * 0.048 : index === 0 ? monthly * 0.4 : index === 1 ? monthly * 0.2 : monthly * 0.08,
  }))
  return <div className={`payslip-preview ${template.theme.toLowerCase()}`}>
    <header>{template.showLogo && <b className={org.logoDataUrl ? 'payslip-logo-mark' : ''}>{org.logoDataUrl ? <img src={org.logoDataUrl} alt="Organization logo" /> : 'P'}</b>}
      <div><h3>{org.name || 'Your Organization'}</h3><p>Payslip for June 2026</p>{template.showClient && <small>Client: {clientName}</small>}</div>
    </header>
    <section>
      <div><span>Employee</span><strong>Demo Employee</strong></div><div><span>Designation</span><strong>Software Engineer</strong></div>
      <div><span>Pay Days</span><strong>30</strong></div><div><span>Bank</span><strong>{template.showBank ? 'HDFC ****1234' : '-'}</strong></div>
    </section>
    <table><thead><tr><th>Earnings</th><th>Amount</th><th>Deductions</th><th>Amount</th></tr></thead><tbody>
      {lines.map(({ component, amount }, index) => {
        const deduction = component.category === 'Deduction'
        return <tr key={component.id}>
          <td>{deduction ? '' : component.name}</td><td>{deduction ? '' : money(amount)}</td>
          <td>{deduction ? component.name : index === 0 ? 'Professional Tax' : ''}</td><td>{deduction ? money(amount) : index === 0 ? '200' : ''}</td>
        </tr>
      })}
    </tbody></table>
    {template.showYtd && <p className="ytd">YTD Gross: Rs {money(monthly * 6)} | YTD Tax: Rs {money(1200)}</p>}
    <footer>{template.note}</footer>
  </div>
}
