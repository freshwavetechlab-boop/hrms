import { useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import './OrganizationSetup.css'

type Organization = {
  name: string; legalName: string; businessType: string; businessLocation: string
  industry: string; hasRunPayrollThisYear: boolean; setupCompleted: boolean
  addressLine1: string; addressLine2: string; city: string; state: string
  postalCode: string; country: string
}

const empty: Organization = { name: '', legalName: '', businessType: '', businessLocation: 'India', industry: '', hasRunPayrollThisYear: false, setupCompleted: false, addressLine1: '', addressLine2: '', city: '', state: '', postalCode: '', country: 'India' }
const industries = ['Information Technology', 'Financial Services', 'Healthcare', 'Manufacturing', 'Retail & E-commerce', 'Education', 'Construction & Real Estate', 'Professional Services', 'Hospitality & Travel', 'Other']
const businessTypes = ['Private Limited Company', 'Public Limited Company', 'Limited Liability Partnership', 'Partnership Firm', 'Sole Proprietorship', 'Trust / Society']
const states = ['Andhra Pradesh', 'Arunachal Pradesh', 'Assam', 'Bihar', 'Chhattisgarh', 'Delhi', 'Goa', 'Gujarat', 'Haryana', 'Himachal Pradesh', 'Jammu and Kashmir', 'Jharkhand', 'Karnataka', 'Kerala', 'Madhya Pradesh', 'Maharashtra', 'Manipur', 'Meghalaya', 'Mizoram', 'Nagaland', 'Odisha', 'Puducherry', 'Punjab', 'Rajasthan', 'Sikkim', 'Tamil Nadu', 'Telangana', 'Tripura', 'Uttar Pradesh', 'Uttarakhand', 'West Bengal']
const steps = [['01', 'Organization setup', 'Business identity & address'], ['02', 'Tax information', 'PAN, TAN & TDS details'], ['03', 'Pay schedule', 'Work week & pay date'], ['04', 'Statutory setup', 'EPF, ESI, PT & LWF'], ['05', 'Salary components', 'Earnings & deductions']]
const api = import.meta.env.VITE_API_URL ?? 'http://localhost:5062'

export default function OrganizationSetup() {
  const [data, setData] = useState(empty)
  const [message, setMessage] = useState('Add your business details to begin payroll setup.')
  const [tone, setTone] = useState('info')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    fetch(`${api}/api/organization`).then(async response => {
      if (response.ok) { const saved = await response.json(); setData({ ...empty, ...saved }); if (saved.setupCompleted) { setMessage('Your organization profile is saved and up to date.'); setTone('success') } }
      else if (response.status !== 404) throw new Error()
    }).catch(() => { setMessage('Could not connect to the payroll API. Start the backend and try again.'); setTone('error') }).finally(() => setLoading(false))
  }, [])

  const update = <K extends keyof Organization>(key: K, value: Organization[K]) => setData(current => ({ ...current, [key]: value, setupCompleted: false }))
  const progress = useMemo(() => Math.round([data.name, data.businessLocation, data.industry, data.addressLine1, data.city, data.state, data.postalCode].filter(Boolean).length / 7 * 100), [data])

  const save = async (event: FormEvent) => {
    event.preventDefault()
    if (!/^[1-9][0-9]{5}$/.test(data.postalCode)) { setMessage('Enter a valid 6-digit Indian postal code.'); setTone('error'); return }
    setSaving(true); setTone('info'); setMessage('Saving your organization profile…')
    try {
      const response = await fetch(`${api}/api/organization`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) })
      if (!response.ok) { const problem = await response.json().catch(() => null); throw new Error(problem?.errors ? Object.values(problem.errors).flat()[0] as string : 'We could not save your organization.') }
      setData({ ...empty, ...await response.json() }); setTone('success'); setMessage('Organization setup completed successfully.'); window.scrollTo({ top: 0, behavior: 'smooth' })
    } catch (error) { setTone('error'); setMessage(error instanceof Error ? error.message : 'Unable to save. Please try again.') }
    finally { setSaving(false) }
  }

  return <div className="payroll-app">
    <header className="topbar"><a href="#top" className="brand"><i>P</i>paymint</a><div><button type="button">? <span>Help</span></button><b>NK</b></div></header>
    <div className="workspace" id="top">
      <aside>
        <span className="eyebrow">Getting started</span><h1>Set up payroll</h1><p>Complete these essentials before your first pay run.</p>
        <nav>{steps.map(([number, title, detail], index) => <div className={index === 0 ? 'active' : ''} key={number}><i>{index === 0 && data.setupCompleted ? '✓' : number}</i><span><strong>{title}</strong><small>{detail}</small></span></div>)}</nav>
        <footer>♢ <span><strong>Your data is secure</strong><small>Protected and encrypted</small></span></footer>
      </aside>
      <main>
        <header className="page-heading"><div><span className="eyebrow purple">Step 1 of 5</span><h2>Tell us about your organization</h2><p>This becomes your primary work location and appears across payroll records.</p></div><div className="progress"><b>{progress}%</b><span><i style={{ width: `${progress}%` }} /></span><small>Profile complete</small></div></header>
        <div className={`notice ${tone}`}><i>{tone === 'success' ? '✓' : tone === 'error' ? '!' : 'i'}</i>{message}</div>
        {loading ? <div className="skeleton"><i /><i /><i /></div> : <form onSubmit={save}>
          <Card icon="⌂" color="coral" title="Business identity" text="Your company’s basic operating details.">
            <div className="grid">
              <Field label="Organization name" required wide hint="The trading name your employees recognize."><input value={data.name} onChange={e => update('name', e.target.value)} placeholder="e.g. Acme Technologies" required /></Field>
              <Field label="Legal name" optional><input value={data.legalName} onChange={e => update('legalName', e.target.value)} placeholder="Name on registration documents" /></Field>
              <Field label="Organization type" optional><select value={data.businessType} onChange={e => update('businessType', e.target.value)}><option value="">Select organization type</option>{businessTypes.map(x => <option key={x}>{x}</option>)}</select></Field>
              <Field label="Business location" required><div className="prefix">🇮🇳<select value={data.businessLocation} onChange={e => update('businessLocation', e.target.value)}><option>India</option></select></div></Field>
              <Field label="Industry" required><select value={data.industry} onChange={e => update('industry', e.target.value)} required><option value="">Select your industry</option>{industries.map(x => <option key={x}>{x}</option>)}</select></Field>
            </div>
          </Card>
          <Card icon="⌖" color="blue" title="Primary work location" text="This address will appear on statutory documents and payslips.">
            <div className="grid">
              <Field label="Address line 1" required wide><input value={data.addressLine1} onChange={e => update('addressLine1', e.target.value)} placeholder="Building, street or area" required /></Field>
              <Field label="Address line 2" optional wide><input value={data.addressLine2} onChange={e => update('addressLine2', e.target.value)} placeholder="Landmark, floor or unit" /></Field>
              <Field label="City" required><input value={data.city} onChange={e => update('city', e.target.value)} placeholder="City" required /></Field>
              <Field label="State / Union Territory" required><select value={data.state} onChange={e => update('state', e.target.value)} required><option value="">Select state</option>{states.map(x => <option key={x}>{x}</option>)}</select></Field>
              <Field label="PIN code" required><input value={data.postalCode} onChange={e => update('postalCode', e.target.value.replace(/\D/g, '').slice(0, 6))} placeholder="6-digit PIN code" inputMode="numeric" required /></Field>
              <Field label="Country" required><input value={data.country} readOnly /></Field>
            </div>
          </Card>
          <Card icon="⌁" color="green" title="Payroll history" text="This helps us prepare accurate year-to-date payroll records.">
            <fieldset><legend>Have you paid any employees during the current financial year?</legend><Choice selected={!data.hasRunPayrollThisYear} change={() => update('hasRunPayrollThisYear', false)} title="No, this is our first payroll" text="Start fresh with your first pay run in Paymint." /><Choice selected={data.hasRunPayrollThisYear} change={() => update('hasRunPayrollThisYear', true)} title="Yes, we’ve already run payroll" text="You’ll import previous employee payments before your first pay run." /></fieldset>
          </Card>
          <div className="actions"><p>◷ You can update these details later in Settings.</p><button disabled={saving}>{saving ? 'Saving…' : <>Save &amp; continue <span>→</span></>}</button></div>
        </form>}
      </main>
    </div>
  </div>
}

function Card({ icon, color, title, text, children }: { icon: string; color: string; title: string; text: string; children: React.ReactNode }) { return <section className="card"><header><i className={color}>{icon}</i><div><h3>{title}</h3><p>{text}</p></div></header>{children}</section> }
function Field({ label, required, optional, wide, hint, children }: { label: string; required?: boolean; optional?: boolean; wide?: boolean; hint?: string; children: React.ReactNode }) { return <label className={wide ? 'wide' : ''}><span>{label} {required && <em>*</em>}{optional && <b>Optional</b>}</span>{children}{hint && <small>{hint}</small>}</label> }
function Choice({ selected, change, title, text }: { selected: boolean; change: () => void; title: string; text: string }) { return <label className={selected ? 'selected' : ''}><input type="radio" name="history" checked={selected} onChange={change} /><i /><span><strong>{title}</strong><small>{text}</small></span></label> }
