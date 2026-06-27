import { useEffect, useMemo, useState } from 'react'
import DataTable from './DataTable'
import { Card, Chk, F, Sel } from './FormPrimitives'
import { useAuthSession } from './AuthGate'
import type { Client, ClientTaxSetting, TaxDeclarationSection, TaxFinalAdjustment, TaxSlab, TaxSurcharge } from '../types/payroll'
import { deleteTaxEngineRow, getTaxEngineSetup, saveTaxClientSetting, saveTaxDeclarationSection, saveTaxFinalAdjustment, saveTaxSlab, saveTaxSurcharge, type TaxEngineSetup } from '../services/taxEngineService'

const fy = `${new Date().getFullYear()}-${String(new Date().getFullYear() + 1).slice(2)}`
const clientTax0: ClientTaxSetting = { id: 0, clientId: '', enabled: true, financialYear: fy, defaultRegime: 'New', allowEmployeeRegimeSelection: true, regimeSelectionWindowOpen: false, regimeSelectionCutoff: '', allowDeclarations: true, plannedDeclarationWindowOpen: false, actualDeclarationWindowOpen: false, declarationWindowStart: '', declarationWindowEnd: '', plannedDeclarationStart: '', plannedDeclarationEnd: '', actualDeclarationStart: '', actualDeclarationEnd: '', poiProcessingMonth: '', reminderEmailsEnabled: true, reminderFrequency: 'Weekly', reminderBeforeLockDays: 7, requireProofUpload: true, requireApproval: true, taxDeductionComponentCode: 'TDS', projectMonthlyTds: true, lockAfterApproval: true, active: true }
const slab0: TaxSlab = { id: 0, financialYear: fy, regime: 'New', incomeFrom: '0', incomeTo: '', ratePercent: '', effectiveFrom: new Date().toISOString().slice(0, 10), active: true }
const surcharge0: TaxSurcharge = { id: 0, financialYear: fy, incomeFrom: '0', incomeTo: '', surchargePercent: '', active: true }
const finalAdjustment0: TaxFinalAdjustment = { id: 0, financialYear: fy, label: 'Health & Education Cess', valueType: 'Percent', value: '4', applyOrder: '100', active: true }
const section0: TaxDeclarationSection = { id: 0, financialYear: fy, code: '', name: '', regime: 'Old', limitAmount: '', proofRequired: true, requiresApproval: true, active: true }

export default function TaxEngineManager({ clients, onMessage, mode = 'company' }: { clients: Client[]; onMessage: (message: string) => void; mode?: 'company' | 'statutory' }) {
  const session = useAuthSession()
  const canManageStatutory = Boolean(session?.user.permissions.includes('tax.statutory.manage'))
  const tabs = mode === 'statutory' && canManageStatutory ? ['Financial Years', 'Rule Versions', 'Tax Slabs', 'Tax Surcharges', 'Final Adjustments', 'Declaration Sections', 'HRA Rules', 'Source References', 'Audit Log', 'Engine Preview'] : ['Company Settings']
  const [tab, setTab] = useState<string>(mode === 'statutory' ? 'Financial Years' : 'Company Settings')
  const [tax, setTax] = useState<TaxEngineSetup>({ financialYears: [], ruleVersions: [], regimes: [], clientSettings: [], slabs: [], surcharges: [], finalAdjustments: [], declarationSections: [], deductionOptions: [], standardDeductions: [], rebateRules: [], exemptionRules: [], hraRules: [], sourceReferences: [], auditLogs: [] })
  const [clientRule, setClientRule] = useState<ClientTaxSetting>(clientTax0)
  const [slab, setSlab] = useState<TaxSlab>(slab0)
  const [surcharge, setSurcharge] = useState<TaxSurcharge>(surcharge0)
  const [finalAdjustment, setFinalAdjustment] = useState<TaxFinalAdjustment>(finalAdjustment0)
  const [section, setSection] = useState<TaxDeclarationSection>(section0)
  const clientOptions = clients.map(client => `${client.id}:${client.name}`)
  const load = async () => setTax(await getTaxEngineSetup())
  useEffect(() => { void load() }, [])
  useEffect(() => { if (!tabs.includes(tab)) setTab(mode === 'statutory' ? 'Financial Years' : 'Company Settings') }, [canManageStatutory, mode])

  const selectedRule = useMemo(() => tax.clientSettings.find(item => item.clientId === clientRule.clientId && item.financialYear === clientRule.financialYear), [clientRule, tax.clientSettings])
  const clientRuleErrors = validateClientRule(clientRule)
  const slabErrors = validateSlab(slab)
  const surchargeErrors = validateSurcharge(surcharge)
  const finalAdjustmentErrors = validateFinalAdjustment(finalAdjustment)
  const sectionErrors = validateSection(section)

  const saveClientRule = async () => {
    if (clientRuleErrors.length) return onMessage(clientRuleErrors[0])
    const result = await saveTaxClientSetting(clientRule)
    onMessage(result.ok ? 'Tax client rule saved.' : result.error)
    if (result.ok) { setClientRule(clientTax0); await load() }
  }
  const saveSlab = async () => {
    if (slabErrors.length) return onMessage(slabErrors[0])
    const result = await saveTaxSlab(slab)
    onMessage(result.ok ? 'Tax slab saved.' : result.error)
    if (result.ok) { setSlab({ ...slab0, financialYear: slab.financialYear }); await load() }
  }
  const saveSection = async () => {
    if (sectionErrors.length) return onMessage(sectionErrors[0])
    const result = await saveTaxDeclarationSection({ ...section, code: section.code.toUpperCase() })
    onMessage(result.ok ? 'Declaration section saved.' : result.error)
    if (result.ok) { setSection({ ...section0, financialYear: section.financialYear }); await load() }
  }
  const saveSurcharge = async () => {
    if (surchargeErrors.length) return onMessage(surchargeErrors[0])
    const result = await saveTaxSurcharge(surcharge)
    onMessage(result.ok ? 'Tax surcharge threshold saved.' : result.error)
    if (result.ok) { setSurcharge({ ...surcharge0, financialYear: surcharge.financialYear }); await load() }
  }
  const saveFinalAdjustment = async () => {
    if (finalAdjustmentErrors.length) return onMessage(finalAdjustmentErrors[0])
    const result = await saveTaxFinalAdjustment(finalAdjustment)
    onMessage(result.ok ? 'Final tax adjustment saved.' : result.error)
    if (result.ok) { setFinalAdjustment({ ...finalAdjustment0, financialYear: finalAdjustment.financialYear }); await load() }
  }
  const copyClientRule = () => {
    if (!clientRule.clientId) return onMessage('Select client before copying client rule.')
    const sourceFy = previousFy(clientRule.financialYear)
    const source = tax.clientSettings.find(item => item.clientId === clientRule.clientId && item.financialYear === sourceFy)
    if (!source) return onMessage(`No client rule found for ${sourceFy}.`)
    setClientRule({ ...source, id: 0, financialYear: clientRule.financialYear })
    onMessage(`Copied client rule from ${sourceFy}. You can edit and save it now.`)
  }
  const copySlabs = async () => {
    if (!isFy(slab.financialYear)) return onMessage('Enter financial year in 2026-27 format.')
    const sourceFy = previousFy(slab.financialYear)
    const rows = tax.slabs.filter(item => item.financialYear === sourceFy)
    if (!rows.length) return onMessage(`No slabs found for ${sourceFy}.`)
    const results = await Promise.all(rows.map(item => saveTaxSlab({ ...item, id: 0, financialYear: slab.financialYear })))
    onMessage(results.every(item => item.ok) ? `Copied ${rows.length} slabs from ${sourceFy}. You can edit them now.` : 'Some slabs could not be copied.')
    await load()
  }
  const copySections = async () => {
    if (!isFy(section.financialYear)) return onMessage('Enter financial year in 2026-27 format.')
    const sourceFy = previousFy(section.financialYear)
    const rows = tax.declarationSections.filter(item => item.financialYear === sourceFy)
    if (!rows.length) return onMessage(`No declaration sections found for ${sourceFy}.`)
    const results = await Promise.all(rows.map(item => saveTaxDeclarationSection({ ...item, id: 0, financialYear: section.financialYear })))
    onMessage(results.every(item => item.ok) ? `Copied ${rows.length} sections from ${sourceFy}. You can edit them now.` : 'Some sections could not be copied.')
    await load()
  }
  const copySurcharges = async () => {
    if (!isFy(surcharge.financialYear)) return onMessage('Enter financial year in 2026-27 format.')
    const sourceFy = previousFy(surcharge.financialYear)
    const rows = tax.surcharges.filter(item => item.financialYear === sourceFy)
    if (!rows.length) return onMessage(`No surcharge thresholds found for ${sourceFy}.`)
    const results = await Promise.all(rows.map(item => saveTaxSurcharge({ ...item, id: 0, financialYear: surcharge.financialYear })))
    onMessage(results.every(item => item.ok) ? `Copied ${rows.length} surcharge thresholds from ${sourceFy}. You can edit them now.` : 'Some surcharge thresholds could not be copied.')
    await load()
  }
  const copyFinalAdjustments = async () => {
    if (!isFy(finalAdjustment.financialYear)) return onMessage('Enter financial year in 2026-27 format.')
    const sourceFy = previousFy(finalAdjustment.financialYear)
    const rows = tax.finalAdjustments.filter(item => item.financialYear === sourceFy)
    if (!rows.length) return onMessage(`No final adjustments found for ${sourceFy}.`)
    const results = await Promise.all(rows.map(item => saveTaxFinalAdjustment({ ...item, id: 0, financialYear: finalAdjustment.financialYear })))
    onMessage(results.every(item => item.ok) ? `Copied ${rows.length} final adjustments from ${sourceFy}. You can edit them now.` : 'Some final adjustments could not be copied.')
    await load()
  }
  const confirmDelete = (label: string) => window.confirm(`Delete this ${label}? This action cannot be undone.`)
  const removeClientRule = async (id: number) => { if (!confirmDelete('client tax rule')) return; await deleteTaxEngineRow('client-settings', id); await load() }
  const removeSlab = async (id: number) => { if (!confirmDelete('tax slab')) return; await deleteTaxEngineRow('slabs', id); await load() }
  const removeSurcharge = async (id: number) => { if (!confirmDelete('tax surcharge threshold')) return; await deleteTaxEngineRow('surcharges', id); await load() }
  const removeFinalAdjustment = async (id: number) => { if (!confirmDelete('final tax adjustment')) return; await deleteTaxEngineRow('final-adjustments', id); await load() }
  const removeSection = async (id: number) => { if (!confirmDelete('declaration section')) return; await deleteTaxEngineRow('sections', id); await load() }

  if (mode === 'statutory' && !canManageStatutory) return <Card t="Statutory rules"><p className="tax-note">You do not have access to statutory tax rule maintenance.</p></Card>

  return <Card t={mode === 'statutory' ? 'Statutory Setup' : 'Tax Engine'}>
    <div className="page-tabs tax-tabs">{tabs.map(item => <button type="button" className={tab === item ? 'active' : ''} onClick={() => setTab(item)} key={item}>{item}</button>)}</div>
    <div className="component-guide tax-guide"><b>{mode === 'statutory' ? 'Statutory rule book' : 'Client tax controls'}</b><span>{mode === 'statutory' ? 'Maintain FY-wise slabs, surcharges, declaration sections and audit references used by payroll tax calculation.' : 'Configure client-wise regime windows, declaration/POI release and TDS reminder rules.'}</span></div>

    {tab === 'Company Settings' && <>
      <div className="tax-rule-layout">
        <section className="tax-rule-card"><h3>Core setup</h3><div className="grid">
          <F l={<Req label="Client" />}><Sel v={clientRule.clientId} set={value => setClientRule({ ...(tax.clientSettings.find(item => item.clientId === value && item.financialYear === clientRule.financialYear) ?? clientTax0), clientId: value })} a={clientOptions} /></F>
          <F l={<Req label="Financial year" />}><input value={clientRule.financialYear} onChange={e => setClientRule({ ...clientRule, financialYear: e.target.value })} placeholder="2026-27" /></F>
          <F l={<Req label="Default regime" />}><Sel v={clientRule.defaultRegime} set={value => setClientRule({ ...clientRule, defaultRegime: value as 'Old' | 'New' })} a={['Old', 'New']} /></F>
        </div></section>
        <section className="tax-rule-card tax-activity-card"><div className="tax-card-title"><h3>Activity windows</h3><span>Open each employee activity at the right stage of the financial year.</span></div><div className="tax-window-grid">
          <article className={clientRule.regimeSelectionWindowOpen ? 'open' : ''}><header><i>1</i><div><b>Regime selection</b><span>Employee chooses Old or New regime.</span></div><em>{clientRule.regimeSelectionWindowOpen ? 'Open' : 'Closed'}</em></header><div className="tax-window-controls"><Chk l="Release window" v={clientRule.regimeSelectionWindowOpen} set={value => setClientRule({ ...clientRule, regimeSelectionWindowOpen: value })} /><F l="Cutoff date"><input type="date" value={dateOnly(clientRule.regimeSelectionCutoff)} onChange={e => setClientRule({ ...clientRule, regimeSelectionCutoff: e.target.value })} /></F></div></article>
          <article className={clientRule.plannedDeclarationWindowOpen ? 'open' : ''}><header><i>2</i><div><b>IT declaration</b><span>Planned investments declared after regime selection.</span></div><em>{clientRule.plannedDeclarationWindowOpen ? 'Open' : 'Closed'}</em></header><div className="tax-window-controls two"><Chk l="Release window" v={clientRule.plannedDeclarationWindowOpen} set={value => setClientRule({ ...clientRule, plannedDeclarationWindowOpen: value })} /><F l="Start date"><input type="date" value={dateOnly(clientRule.plannedDeclarationStart)} onChange={e => setClientRule({ ...clientRule, plannedDeclarationStart: e.target.value, declarationWindowStart: e.target.value })} /></F><F l="Lock date"><input type="date" value={dateOnly(clientRule.plannedDeclarationEnd)} onChange={e => setClientRule({ ...clientRule, plannedDeclarationEnd: e.target.value, declarationWindowEnd: e.target.value })} /></F></div></article>
          <article className={clientRule.actualDeclarationWindowOpen ? 'open' : ''}><header><i>3</i><div><b>POI submission</b><span>Actual investment proofs collected near year end.</span></div><em>{clientRule.actualDeclarationWindowOpen ? 'Open' : 'Closed'}</em></header><div className="tax-window-controls two"><Chk l="Release window" v={clientRule.actualDeclarationWindowOpen} set={value => setClientRule({ ...clientRule, actualDeclarationWindowOpen: value })} /><F l="Start date"><input type="date" value={dateOnly(clientRule.actualDeclarationStart)} onChange={e => setClientRule({ ...clientRule, actualDeclarationStart: e.target.value })} /></F><F l="Lock date"><input type="date" value={dateOnly(clientRule.actualDeclarationEnd)} onChange={e => setClientRule({ ...clientRule, actualDeclarationEnd: e.target.value })} /></F><F l="Payroll month"><input type="month" value={clientRule.poiProcessingMonth || ''} onChange={e => setClientRule({ ...clientRule, poiProcessingMonth: e.target.value })} /></F></div></article>
        </div>{clientRuleErrors.length > 0 && <p className="inline-error">{clientRuleErrors[0]}</p>}</section>
        <section className="tax-rule-card"><h3>Reminder settings</h3><div className="grid">
          <Chk l="Reminder emails" v={clientRule.reminderEmailsEnabled} set={value => setClientRule({ ...clientRule, reminderEmailsEnabled: value })} />
          <F l="Reminder frequency"><Sel v={clientRule.reminderFrequency} set={value => setClientRule({ ...clientRule, reminderFrequency: value })} a={['Daily', 'Weekly', 'Fortnightly']} /></F>
          <F l="Days before lock"><input value={clientRule.reminderBeforeLockDays} onChange={e => setClientRule({ ...clientRule, reminderBeforeLockDays: e.target.value.replace(/\D/g, '') })} /></F>
        </div></section>
        <section className="tax-rule-card"><h3>Feature switches</h3><div className="tax-switch-grid">
          <Chk l="Enable tax engine" v={clientRule.enabled} set={value => setClientRule({ ...clientRule, enabled: value })} /><Chk l="Employee regime selection" v={clientRule.allowEmployeeRegimeSelection} set={value => setClientRule({ ...clientRule, allowEmployeeRegimeSelection: value })} /><Chk l="IT declaration release" v={clientRule.plannedDeclarationWindowOpen} set={value => setClientRule({ ...clientRule, plannedDeclarationWindowOpen: value })} /><Chk l="POI submission release" v={clientRule.actualDeclarationWindowOpen} set={value => setClientRule({ ...clientRule, actualDeclarationWindowOpen: value })} /><Chk l="Active" v={clientRule.active} set={value => setClientRule({ ...clientRule, active: value })} />
        </div></section>
      </div>
      <div className="tax-sticky-actions"><span>{selectedRule ? 'Editing existing client rule' : 'New client tax rule'}</span><button type="button" className="secondary" onClick={() => setClientRule(clientTax0)}>Clear form</button><button type="button" className="secondary" onClick={copyClientRule}>Copy from {previousFy(clientRule.financialYear)}</button><button type="button" disabled={clientRuleErrors.length > 0} onClick={saveClientRule}>{clientRule.id ? 'Update client rule' : 'Save client rule'}</button></div>
      <DataTable rows={tax.clientSettings} columns={[{ key: 'clientId', label: 'Client' }, { key: 'financialYear', label: 'FY' }, { key: 'defaultRegime', label: 'Default Regime' }, { key: 'regimeOpen', label: 'Regime', render: row => row.regimeSelectionWindowOpen ? 'Open' : 'Closed' }, { key: 'itOpen', label: 'IT Declaration', render: row => row.plannedDeclarationWindowOpen ? 'Open' : 'Closed' }, { key: 'poiOpen', label: 'POI', render: row => row.actualDeclarationWindowOpen ? 'Open' : 'Closed' }, { key: 'poiProcessingMonth', label: 'POI Month' }, { key: 'reminderFrequency', label: 'Reminder' }, { key: 'active', label: 'Status', render: row => row.active ? 'Active' : 'Inactive' }]} actions={row => <ActionButtons onEdit={() => setClientRule(row)} onDelete={() => removeClientRule(row.id)} />} />
    </>}

    {tab === 'Financial Years' && <DataTable rows={tax.financialYears as Record<string, unknown>[]} columns={[{ key: 'code', label: 'FY' }, { key: 'startDate', label: 'Start', value: row => dateOnly(row.startDate as string) }, { key: 'endDate', label: 'End', value: row => dateOnly(row.endDate as string) }, { key: 'active', label: 'Status', render: row => row.active ? 'Active' : 'Inactive' }, { key: 'notes', label: 'Notes' }]} />}

    {tab === 'Rule Versions' && <DataTable rows={tax.ruleVersions as Record<string, unknown>[]} columns={[{ key: 'financialYear', label: 'FY' }, { key: 'versionNumber', label: 'Version' }, { key: 'versionName', label: 'Name' }, { key: 'effectiveFrom', label: 'Effective From', value: row => dateOnly(row.effectiveFrom as string) }, { key: 'effectiveTo', label: 'Effective To', value: row => dateOnly(row.effectiveTo as string) || 'Open' }, { key: 'isPublished', label: 'Published', render: row => row.isPublished ? 'Yes' : 'No' }, { key: 'active', label: 'Status', render: row => row.active ? 'Active' : 'Inactive' }, { key: 'source', label: 'Source' }]} />}

    {tab === 'Tax Slabs' && <>
      <div className="tax-copy-bar"><label><span><Req label="Financial year" /></span><input value={slab.financialYear} onChange={e => setSlab({ ...slab, financialYear: e.target.value })} placeholder="2026-27" /></label><button type="button" onClick={copySlabs}>Copy from {previousFy(slab.financialYear)}</button><button type="button" className="secondary" onClick={() => setSlab({ ...slab0, financialYear: slab.financialYear })}>Clear form</button></div>
      <p className="tax-note">Cess or any final tax addition is configured in Final Adjustments. Surcharge is configured separately by annual taxable income thresholds.</p>
      <div className="grid">
        <F l={<Req label="Regime" />}><Sel v={slab.regime} set={value => setSlab({ ...slab, regime: value as 'Old' | 'New' })} a={['Old', 'New']} /></F>
        <F l={<Req label="Income from" />}><input value={slab.incomeFrom} onChange={e => setSlab({ ...slab, incomeFrom: money(e.target.value) })} /></F>
        <F l="Income to"><input value={slab.incomeTo} onChange={e => setSlab({ ...slab, incomeTo: money(e.target.value) })} placeholder="No upper limit" /></F>
        <F l={<Req label="Rate %" />}><input value={slab.ratePercent} onChange={e => setSlab({ ...slab, ratePercent: pct(e.target.value) })} /></F>
        <F l={<Req label="Effective from" />}><input type="date" value={dateOnly(slab.effectiveFrom)} onChange={e => setSlab({ ...slab, effectiveFrom: e.target.value })} /></F>
        <Chk l="Active" v={slab.active} set={value => setSlab({ ...slab, active: value })} />
        <button type="button" className="tax-submit" disabled={slabErrors.length > 0} onClick={saveSlab}>{slab.id ? 'Update slab' : 'Add slab'}</button>
      </div>
      {slabErrors.length > 0 && <p className="inline-error">{slabErrors[0]}</p>}
      <DataTable rows={tax.slabs} columns={[{ key: 'financialYear', label: 'FY' }, { key: 'regime', label: 'Regime' }, { key: 'range', label: 'Income Range', value: row => `${row.incomeFrom || 0} - ${row.incomeTo || 'No limit'}` }, { key: 'ratePercent', label: 'Rate %' }, { key: 'effectiveFrom', label: 'Effective From', value: row => dateOnly(row.effectiveFrom) }, { key: 'active', label: 'Status', render: row => row.active ? 'Active' : 'Inactive' }]} actions={row => <ActionButtons onEdit={() => setSlab(row)} onDelete={() => removeSlab(row.id)} />} />
    </>}

    {tab === 'Tax Surcharges' && <>
      <div className="tax-copy-bar"><label><span><Req label="Financial year" /></span><input value={surcharge.financialYear} onChange={e => setSurcharge({ ...surcharge, financialYear: e.target.value })} placeholder="2026-27" /></label><button type="button" onClick={copySurcharges}>Copy from {previousFy(surcharge.financialYear)}</button><button type="button" className="secondary" onClick={() => setSurcharge({ ...surcharge0, financialYear: surcharge.financialYear })}>Clear form</button></div>
      <div className="grid">
        <F l={<Req label="Annual income from" />}><input value={surcharge.incomeFrom} onChange={e => setSurcharge({ ...surcharge, incomeFrom: money(e.target.value) })} /></F>
        <F l="Annual income to"><input value={surcharge.incomeTo} onChange={e => setSurcharge({ ...surcharge, incomeTo: money(e.target.value) })} placeholder="No upper limit" /></F>
        <F l={<Req label="Surcharge %" />}><input value={surcharge.surchargePercent} onChange={e => setSurcharge({ ...surcharge, surchargePercent: pct(e.target.value) })} /></F>
        <Chk l="Active" v={surcharge.active} set={value => setSurcharge({ ...surcharge, active: value })} />
        <button type="button" className="tax-submit" disabled={surchargeErrors.length > 0} onClick={saveSurcharge}>{surcharge.id ? 'Update threshold' : 'Add threshold'}</button>
      </div>
      {surchargeErrors.length > 0 && <p className="inline-error">{surchargeErrors[0]}</p>}
      <DataTable rows={tax.surcharges} columns={[{ key: 'financialYear', label: 'FY' }, { key: 'range', label: 'Annual Taxable Income', value: row => `${row.incomeFrom || 0} - ${row.incomeTo || 'No limit'}` }, { key: 'surchargePercent', label: 'Surcharge %' }, { key: 'active', label: 'Status', render: row => row.active ? 'Active' : 'Inactive' }]} actions={row => <ActionButtons onEdit={() => setSurcharge(row)} onDelete={() => removeSurcharge(row.id)} />} />
    </>}

    {tab === 'Final Adjustments' && <>
      <div className="tax-copy-bar"><label><span><Req label="Financial year" /></span><input value={finalAdjustment.financialYear} onChange={e => setFinalAdjustment({ ...finalAdjustment, financialYear: e.target.value })} placeholder="2026-27" /></label><button type="button" onClick={copyFinalAdjustments}>Copy from {previousFy(finalAdjustment.financialYear)}</button><button type="button" className="secondary" onClick={() => setFinalAdjustment({ ...finalAdjustment0, financialYear: finalAdjustment.financialYear })}>Clear form</button></div>
      <div className="grid">
        <F l={<Req label="Label" />}><input value={finalAdjustment.label} onChange={e => setFinalAdjustment({ ...finalAdjustment, label: e.target.value })} placeholder="Health & Education Cess" /></F>
        <F l={<Req label="Value type" />}><Sel v={finalAdjustment.valueType} set={value => setFinalAdjustment({ ...finalAdjustment, valueType: value as 'Percent' | 'Fixed' })} a={['Percent', 'Fixed']} /></F>
        <F l={<Req label="Value" />}><input value={finalAdjustment.value} onChange={e => setFinalAdjustment({ ...finalAdjustment, value: money(e.target.value) })} placeholder={finalAdjustment.valueType === 'Percent' ? '4' : '1000'} /></F>
        <F l={<Req label="Apply order" />}><input value={finalAdjustment.applyOrder} onChange={e => setFinalAdjustment({ ...finalAdjustment, applyOrder: e.target.value.replace(/\D/g, '') })} /></F>
        <Chk l="Active" v={finalAdjustment.active} set={value => setFinalAdjustment({ ...finalAdjustment, active: value })} />
        <button type="button" className="tax-submit" disabled={finalAdjustmentErrors.length > 0} onClick={saveFinalAdjustment}>{finalAdjustment.id ? 'Update adjustment' : 'Add adjustment'}</button>
      </div>
      {finalAdjustmentErrors.length > 0 && <p className="inline-error">{finalAdjustmentErrors[0]}</p>}
      <DataTable rows={tax.finalAdjustments} columns={[{ key: 'financialYear', label: 'FY' }, { key: 'label', label: 'Label' }, { key: 'valueType', label: 'Type' }, { key: 'value', label: 'Value' }, { key: 'applyOrder', label: 'Order' }, { key: 'active', label: 'Status', render: row => row.active ? 'Active' : 'Inactive' }]} actions={row => <ActionButtons onEdit={() => setFinalAdjustment(row)} onDelete={() => removeFinalAdjustment(row.id)} />} />
    </>}

    {tab === 'Declaration Sections' && <>
      <div className="tax-copy-bar"><label><span><Req label="Financial year" /></span><input value={section.financialYear} onChange={e => setSection({ ...section, financialYear: e.target.value })} placeholder="2026-27" /></label><button type="button" onClick={copySections}>Copy from {previousFy(section.financialYear)}</button><button type="button" className="secondary" onClick={() => setSection({ ...section0, financialYear: section.financialYear })}>Clear form</button></div>
      <div className="grid">
        <F l={<Req label="Code" />}><input value={section.code} onChange={e => setSection({ ...section, code: e.target.value.toUpperCase() })} placeholder="80C" /></F>
        <F l={<Req label="Name" />}><input value={section.name} onChange={e => setSection({ ...section, name: e.target.value })} /></F>
        <F l={<Req label="Regime" />}><Sel v={section.regime} set={value => setSection({ ...section, regime: value as 'Old' | 'New' | 'Both' })} a={['Old', 'New', 'Both']} /></F>
        <F l="Limit amount"><input value={section.limitAmount} onChange={e => setSection({ ...section, limitAmount: money(e.target.value) })} placeholder="No limit" /></F>
        <Chk l="Proof required" v={section.proofRequired} set={value => setSection({ ...section, proofRequired: value })} /><Chk l="Approval required" v={section.requiresApproval} set={value => setSection({ ...section, requiresApproval: value })} /><Chk l="Active" v={section.active} set={value => setSection({ ...section, active: value })} />
        <button type="button" className="tax-submit" disabled={sectionErrors.length > 0} onClick={saveSection}>{section.id ? 'Update section' : 'Add section'}</button>
      </div>
      {sectionErrors.length > 0 && <p className="inline-error">{sectionErrors[0]}</p>}
      <DataTable rows={tax.declarationSections} columns={[{ key: 'financialYear', label: 'FY' }, { key: 'code', label: 'Code' }, { key: 'name', label: 'Name' }, { key: 'regime', label: 'Regime' }, { key: 'limitAmount', label: 'Limit' }, { key: 'proofRequired', label: 'Proof', render: row => row.proofRequired ? 'Required' : 'Optional' }, { key: 'requiresApproval', label: 'Approval', render: row => row.requiresApproval ? 'Required' : 'Not required' }, { key: 'active', label: 'Status', render: row => row.active ? 'Active' : 'Inactive' }]} actions={row => <ActionButtons onEdit={() => setSection(row)} onDelete={() => removeSection(row.id)} />} />
    </>}

    {tab === 'HRA Rules' && <DataTable rows={tax.hraRules as Record<string, unknown>[]} columns={[{ key: 'financialYear', label: 'FY' }, { key: 'ruleVersionId', label: 'Rule Version' }, { key: 'isApplicable', label: 'Applicable', render: row => row.isApplicable ? 'Yes' : 'No' }, { key: 'metroSalaryPercent', label: 'Metro %' }, { key: 'nonMetroSalaryPercent', label: 'Non Metro %' }, { key: 'rentMinusBasicPercent', label: 'Rent less Basic %' }, { key: 'formulaType', label: 'Formula' }, { key: 'active', label: 'Status', render: row => row.active ? 'Active' : 'Inactive' }]} />}

    {tab === 'Source References' && <DataTable rows={tax.sourceReferences as Record<string, unknown>[]} columns={[{ key: 'financialYear', label: 'FY' }, { key: 'sourceType', label: 'Type' }, { key: 'title', label: 'Title' }, { key: 'documentNumber', label: 'Document No.' }, { key: 'publishedDate', label: 'Published', value: row => dateOnly(row.publishedDate as string) }, { key: 'effectiveFrom', label: 'Effective', value: row => dateOnly(row.effectiveFrom as string) }, { key: 'url', label: 'URL' }]} />}

    {tab === 'Audit Log' && <DataTable rows={tax.auditLogs as Record<string, unknown>[]} columns={[{ key: 'changedOn', label: 'Changed On', value: row => String(row.changedOn || '').replace('T', ' ').slice(0, 19) }, { key: 'entityName', label: 'Entity' }, { key: 'entityId', label: 'ID' }, { key: 'action', label: 'Action' }, { key: 'changedBy', label: 'Changed By' }, { key: 'changeReason', label: 'Reason' }]} />}

    {tab === 'Engine Preview' && <div className="tax-engine-preview"><b>Tax engine contract</b><span>Payroll reads active FY rule version, statutory slabs, surcharge thresholds, final adjustments, company windows, employee regime, approved proofs, salary and TDS already deducted.</span><span>Statutory rules are global and versioned by financial year. Company settings only open or close employee activities.</span></div>}
  </Card>
}

function ActionButtons({ onEdit, onDelete }: { onEdit: () => void; onDelete: () => void }) {
  return <span className="row-actions"><button type="button" onClick={onEdit}>Edit</button><button type="button" className="danger" onClick={onDelete}>Delete</button></span>
}

function Req({ label }: { label: string }) {
  return <>{label} <em>*</em></>
}

function validateClientRule(rule: ClientTaxSetting) {
  const errors: string[] = []
  const range = financialYearRange(rule.financialYear)
  if (!rule.clientId) errors.push('Select client for tax settings.')
  if (!isFy(rule.financialYear)) errors.push('Enter financial year in 2026-27 format.')
  if (!rule.defaultRegime) errors.push('Default regime is required.')
  if (numberOrNull(String(rule.reminderBeforeLockDays)) === null || Number(rule.reminderBeforeLockDays) < 0) errors.push('Reminder before lock days must be zero or more.')
  if (rule.regimeSelectionWindowOpen && !rule.allowEmployeeRegimeSelection) errors.push('Enable employee regime selection before opening the regime window.')
  if (rule.plannedDeclarationWindowOpen && !rule.allowDeclarations) errors.push('Enable declarations before opening planned investment.')
  if (rule.actualDeclarationWindowOpen && !rule.allowDeclarations) errors.push('Enable declarations before opening actual investment.')
  if (rule.regimeSelectionCutoff && range && !within(dateOnly(rule.regimeSelectionCutoff), range.start, range.end)) errors.push('Selection cutoff must fall within the financial year.')
  if (rule.plannedDeclarationStart && range && !within(dateOnly(rule.plannedDeclarationStart), range.start, range.end)) errors.push('Planned declaration start must fall within the financial year.')
  if (rule.plannedDeclarationEnd && range && !within(dateOnly(rule.plannedDeclarationEnd), range.start, range.end)) errors.push('Planned declaration end must fall within the financial year.')
  if (rule.actualDeclarationStart && range && !within(dateOnly(rule.actualDeclarationStart), range.start, range.end)) errors.push('Actual declaration start must fall within the financial year.')
  if (rule.actualDeclarationEnd && range && !within(dateOnly(rule.actualDeclarationEnd), range.start, range.end)) errors.push('Actual declaration end must fall within the financial year.')
  if (rule.poiProcessingMonth && range && !within(`${rule.poiProcessingMonth}-01`, range.start, range.end)) errors.push('POI processing month must fall within the financial year.')
  if (rule.plannedDeclarationStart && rule.plannedDeclarationEnd && dateOnly(rule.plannedDeclarationStart) > dateOnly(rule.plannedDeclarationEnd)) errors.push('Planned declaration start date cannot be after planned declaration end date.')
  if (rule.actualDeclarationStart && rule.actualDeclarationEnd && dateOnly(rule.actualDeclarationStart) > dateOnly(rule.actualDeclarationEnd)) errors.push('Actual declaration start date cannot be after actual declaration end date.')
  return errors
}

function validateSlab(row: TaxSlab) {
  const errors: string[] = []
  const from = numberOrNull(row.incomeFrom)
  const to = row.incomeTo ? numberOrNull(row.incomeTo) : null
  const rate = numberOrNull(row.ratePercent)
  const range = financialYearRange(row.financialYear)
  if (!isFy(row.financialYear)) errors.push('Enter financial year in 2026-27 format.')
  if (!row.regime) errors.push('Regime is required.')
  if (from === null || from < 0) errors.push('Income from must be zero or more.')
  if (to !== null && from !== null && to <= from) errors.push('Income to must be greater than income from.')
  if (rate === null || rate < 0 || rate > 100) errors.push('Tax rate must be between 0 and 100.')
  if (!row.effectiveFrom) errors.push('Effective from is required.')
  if (row.effectiveFrom && range && !within(dateOnly(row.effectiveFrom), range.start, range.end)) errors.push('Effective from must fall within the financial year.')
  return errors
}

function validateSurcharge(row: TaxSurcharge) {
  const errors: string[] = []
  const from = numberOrNull(row.incomeFrom)
  const to = row.incomeTo ? numberOrNull(row.incomeTo) : null
  const surcharge = numberOrNull(row.surchargePercent)
  if (!isFy(row.financialYear)) errors.push('Enter financial year in 2026-27 format.')
  if (from === null || from < 0) errors.push('Annual income from must be zero or more.')
  if (to !== null && from !== null && to <= from) errors.push('Annual income to must be greater than annual income from.')
  if (surcharge === null || surcharge < 0 || surcharge > 100) errors.push('Surcharge must be between 0 and 100.')
  return errors
}

function validateFinalAdjustment(row: TaxFinalAdjustment) {
  const errors: string[] = []
  const value = numberOrNull(row.value)
  const order = numberOrNull(row.applyOrder)
  if (!isFy(row.financialYear)) errors.push('Enter financial year in 2026-27 format.')
  if (!row.label.trim()) errors.push('Adjustment label is required.')
  if (!['Percent', 'Fixed'].includes(row.valueType)) errors.push('Value type must be Percent or Fixed.')
  if (value === null || value < 0) errors.push('Adjustment value must be zero or more.')
  if (row.valueType === 'Percent' && value !== null && value > 100) errors.push('Percent adjustment cannot exceed 100.')
  if (order === null || order < 0) errors.push('Apply order must be zero or more.')
  return errors
}

function validateSection(row: TaxDeclarationSection) {
  const errors: string[] = []
  const limit = row.limitAmount ? numberOrNull(row.limitAmount) : null
  if (!isFy(row.financialYear)) errors.push('Enter financial year in 2026-27 format.')
  if (!row.code.trim()) errors.push('Section code is required.')
  if (row.code && !/^[A-Z0-9-]{2,20}$/.test(row.code)) errors.push('Section code can use A-Z, 0-9 and hyphen only.')
  if (!row.name.trim()) errors.push('Section name is required.')
  if (!row.regime) errors.push('Regime is required.')
  if (limit !== null && limit < 0) errors.push('Limit amount cannot be negative.')
  if (row.requiresApproval && !row.proofRequired) errors.push('Approval required sections should also require proof upload.')
  return errors
}

function previousFy(value: string) {
  const match = value.match(/^(\d{4})-(\d{2}|\d{4})$/)
  if (!match) return 'previous FY'
  const start = Number(match[1]) - 1
  return `${start}-${String(start + 1).slice(2)}`
}

function financialYearRange(value: string) {
  const match = value.match(/^(\d{4})-(\d{2}|\d{4})$/)
  if (!match) return null
  const startYear = Number(match[1])
  return { start: `${startYear}-04-01`, end: `${startYear + 1}-03-31` }
}

function isFy(value: string) {
  return /^\d{4}-\d{2}$/.test(value)
}

function within(value: string, start: string, end: string) {
  return value >= start && value <= end
}

function dateOnly(value?: string | null) {
  return String(value || '').slice(0, 10)
}

function numberOrNull(value: string) {
  if (!value.trim()) return null
  const parsed = Number(value)
  return Number.isFinite(parsed) ? parsed : null
}

function money(value: string) {
  return value.replace(/[^\d.]/g, '').replace(/(\..*)\./g, '$1')
}

function pct(value: string) {
  return money(value).slice(0, 6)
}
