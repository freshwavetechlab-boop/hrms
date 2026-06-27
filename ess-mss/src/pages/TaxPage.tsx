import { useEffect, useState } from 'react'
import type { LoadState, TaxDeclarationSection, TaxPortal, User } from '../types'
import { essApi } from '../services/essApi'

const money = (value: number | undefined) => Number(value || 0).toLocaleString('en-IN')
const date = (value?: string) => value ? new Date(value).toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }) : 'Not set'

export function TaxPage({ user }: { user: User }) {
  const [portal, setPortal] = useState<TaxPortal | null>(null)
  const [state, setState] = useState<LoadState>('loading')
  const [regime, setRegime] = useState<'Old' | 'New'>('New')
  const [sections, setSections] = useState<TaxDeclarationSection[]>([])
  const [message, setMessage] = useState('')
  const [tab, setTab] = useState<'Regime' | 'Planned' | 'Actual'>('Regime')

  const load = () => essApi.taxPortal().then(data => { setPortal(data); setRegime(data.selectedRegime || data.defaultRegime); setSections(data.sections); setState('ready') }).catch(() => setState('error'))
  useEffect(() => { load() }, [user.email])

  const saveRegime = async () => {
    setMessage('')
    await essApi.saveTaxRegime(regime).then(() => { setMessage('Regime selection saved.'); load() }).catch(error => setMessage(error.message))
  }
  const saveDeclarations = async (phase: 'Planned' | 'Actual') => {
    setMessage('')
    const lines = sections.map(item => ({ sectionId: item.sectionId, amount: Number((phase === 'Actual' ? item.actualAmount : item.plannedAmount) || 0), remarks: item.remarks || '' }))
    await essApi.saveTaxDeclarations(phase, lines).then(() => { setMessage(portal?.requiresApproval ? `${phase} declaration submitted for approval.` : `${phase} declaration saved.`); load() }).catch(error => setMessage(error.message))
  }
  const updateSection = (sectionId: number, patch: Partial<TaxDeclarationSection>) => setSections(current => current.map(item => item.sectionId === sectionId ? { ...item, ...patch } : item))

  if (state === 'loading') return <section className="tax-workspace"><div className="empty-work"><span>Loading tax workspace...</span></div></section>
  if (state === 'error' || !portal) return <section className="tax-workspace"><div className="empty-work"><b>Tax information is unavailable.</b><span>Please contact payroll if this continues.</span></div></section>

  return <section className="tax-workspace">
    <div className="feature-heading"><span className="eyebrow">My tax</span><h3>Tax regime & declarations</h3><p>Follow the available actions for FY {portal.financialYear}. Frevo One HR opens each activity based on payroll settings.</p></div>
    <div className={`tax-guidance ${portal.enabled ? 'open' : 'closed'}`}><b>{portal.enabled ? 'Guidance' : 'Not available'}</b><span>{portal.message || 'Tax self-service is available.'}</span>{message && <em>{message}</em>}</div>
    <div className="tax-status-grid">
      <article><span>Regime cutoff</span><b>{date(portal.regimeSelectionCutoff)}</b></article>
      <article><span>IT Declaration</span><b>{date(portal.plannedDeclarationStart || portal.declarationWindowStart)} - {date(portal.plannedDeclarationEnd || portal.declarationWindowEnd)}</b></article>
      <article><span>POI window</span><b>{date(portal.actualDeclarationStart)} - {date(portal.actualDeclarationEnd)}</b></article>
      <article><span>Approval</span><b>{portal.requiresApproval ? 'Required' : 'Not required'}</b></article>
    </div>
    <div className="tax-top-tabs">{(['Regime', 'Planned', 'Actual'] as const).map(item => <button type="button" key={item} className={tab === item ? 'active' : ''} onClick={() => setTab(item)}>{item === 'Regime' ? 'Regime selection' : item === 'Planned' ? 'IT Declaration' : 'POI'}</button>)}</div>
    {tab === 'Regime' && <div className="tax-panels">
      <section className="tax-primary-panel">
        <h4>Regime selection</h4>
        <p>{portal.canSelectRegime ? 'Select the tax regime payroll should use for projection and TDS.' : portal.regimeSelectionWindowOpen ? 'Regime selection is closed for the configured date.' : 'Payroll has not opened regime selection yet.'}</p>
        <div className="tax-regime-options">
          {(['New', 'Old'] as const).map(item => <button type="button" className={regime === item ? 'active' : ''} disabled={!portal.canSelectRegime} onClick={() => setRegime(item)} key={item}>{item} Regime</button>)}
        </div>
        <div className="tax-panel-footer"><span>Current: {portal.selectedRegime || portal.defaultRegime} {portal.regimeStatus && `(${portal.regimeStatus})`}</span><button type="button" disabled={!portal.canSelectRegime} onClick={saveRegime}>Save regime</button></div>
      </section>
      <section>
        <h4>Final tax adjustments</h4>
        <p>These additions are applied on final tax as configured by payroll.</p>
        <div className="tax-adjustment-list">{portal.finalAdjustments.map(item => <span key={`${item.label}-${item.value}`}><b>{item.label}</b><em>{item.valueType === 'Percent' ? `${item.value}%` : `Rs ${money(item.value)}`}</em></span>)}{!portal.finalAdjustments.length && <small>No final adjustments configured.</small>}</div>
      </section>
    </div>}
    {tab === 'Planned' && <DeclarationPanel portal={portal} sections={sections} phase="Planned" canEdit={portal.canSubmitPlanned} message={!portal.declarationRequired ? 'IT Declaration is not needed under New regime.' : portal.canSubmitPlanned ? 'Declare proposed investments for tax projection.' : portal.plannedDeclarationWindowOpen ? 'IT Declaration window is closed for the configured dates.' : 'Payroll has not opened IT Declaration yet.'} updateSection={updateSection} saveDeclarations={saveDeclarations} />}
    {tab === 'Actual' && <DeclarationPanel portal={portal} sections={sections} phase="Actual" canEdit={portal.canSubmitActual} message={!portal.declarationRequired ? 'POI is not needed under New regime.' : portal.canSubmitActual ? 'Submit actual investment amounts and proof references for payroll review.' : portal.actualDeclarationWindowOpen ? 'POI window is closed for the configured dates.' : 'Payroll has not opened POI yet.'} updateSection={updateSection} saveDeclarations={saveDeclarations} />}
  </section>
}

function DeclarationPanel({ portal, sections, phase, canEdit, message, updateSection, saveDeclarations }: { portal: TaxPortal; sections: TaxDeclarationSection[]; phase: 'Planned' | 'Actual'; canEdit: boolean; message: string; updateSection: (sectionId: number, patch: Partial<TaxDeclarationSection>) => void; saveDeclarations: (phase: 'Planned' | 'Actual') => void }) {
  return <section className="tax-declaration-card">
      <h4>{phase === 'Planned' ? 'IT Declaration' : 'Proof of Investment'}</h4>
      <p>{message}{phase === 'Actual' && portal.poiProcessingMonth ? ` POI processing month: ${portal.poiProcessingMonth}.` : ''}</p>
      <div className="tax-phase-strip"><span className={phase === 'Planned' ? 'active' : ''}>Planned</span><span className={phase === 'Actual' ? 'active' : ''}>Actual</span></div>
      <div className="tax-declaration-list">
        {sections.map(item => <article key={item.sectionId}>
          <div><b>{item.code} - {item.name}</b><span>{item.regime} regime / Limit {item.limitAmount ? `Rs ${money(item.limitAmount)}` : 'No limit'} / {item.proofRequired ? 'Proof required' : 'Proof optional'}</span></div>
          <input disabled={phase !== 'Planned' || !canEdit} value={String(item.plannedAmount || '')} onChange={event => updateSection(item.sectionId, { plannedAmount: Number(event.target.value.replace(/\D/g, '') || 0) })} placeholder="Planned amount" />
          <input disabled={phase !== 'Actual' || !canEdit} value={String(item.actualAmount || '')} onChange={event => updateSection(item.sectionId, { actualAmount: Number(event.target.value.replace(/\D/g, '') || 0) })} placeholder="Actual amount" />
          <input disabled={!canEdit} value={item.remarks || ''} onChange={event => updateSection(item.sectionId, { remarks: event.target.value })} placeholder={phase === 'Actual' ? 'Proof/reference remarks' : 'Remarks'} />
          <em>{item.status}</em>
        </article>)}
        {!sections.length && <div className="empty-work"><b>No declaration sections available.</b><span>{portal.declarationRequired ? `Payroll has not opened any sections for FY ${portal.financialYear}.` : 'Selected regime does not require investment declarations.'}</span></div>}
      </div>
      <div className="tax-panel-footer"><span>{portal.requiresApproval ? 'Submission will go to payroll for approval.' : 'Submission will be accepted directly.'}</span><button type="button" disabled={!canEdit || !sections.length} onClick={() => saveDeclarations(phase)}>Submit {phase === 'Planned' ? 'IT Declaration' : 'POI'}</button></div>
    </section>
}
