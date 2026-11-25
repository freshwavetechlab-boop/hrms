import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Select, Space, Spin, Tag } from 'antd'
import { EyeOutlined, ReloadOutlined } from '@ant-design/icons'
import { getCorePayslipDesign, getCopiedPayslipDesign, getPayslipDesign, getPayslipDesignSources, previewPayslipDesign, previewPayslipDesignPdf, savePayslipDesign, type PayslipDesignCatalog, type PayslipDesignField, type PayslipDesignSource, type PayslipDesignTemplate, type SavedPayslipDesign, type SavePayslipDesignRequest } from '../services/payslipService'
import { PayslipPreviewLease, payslipDesignIssues, payslipDesignPeriodIssues, payslipDesignPeriodLabel, payslipDesignPeriodRequest, payslipDesignPreviewIssues, payslipDesignScopeCapabilityIssues } from '../utils/payslipDesign'
import SearchSelect from './SearchSelect'
import PdfCanvasPreview from './PdfCanvasPreview'
import PayslipFieldPicker from './PayslipFieldPicker'
import PayrollSimulation from './PayrollSimulation'
import type { Client, Component, Structure } from '../types/payroll'
import './ExcelPayslipDesignEditor.css'

type Props = { template: PayslipDesignTemplate; copySeed?: SavedPayslipDesign; salaryTemplates: Structure[]; components: Component[]; clients: Client[]; organizationName?: string; logoDataUrl?: string; onSaved: (template: PayslipDesignTemplate) => void; onBusyChange: (busy: boolean) => void }
export default function ExcelPayslipDesignEditor({ template, copySeed, salaryTemplates, components, clients, organizationName, logoDataUrl, onSaved, onBusyChange }: Props) {
  const [sources, setSources] = useState<PayslipDesignSource[]>([])
  const [sourceId, setSourceId] = useState('')
  const [profileHash, setProfileHash] = useState('')
  const [catalog, setCatalog] = useState<PayslipDesignCatalog | null>(null)
  const [fields, setFields] = useState<PayslipDesignField[]>([])
  const [decimals, setDecimals] = useState<0 | 2>(copySeed?.amountDecimalPlaces ?? 0)
  const [corePayrollOnly, setCorePayrollOnly] = useState(!!copySeed || template.id === 0)
  const [newDesignSource, setNewDesignSource] = useState<'CorePayroll' | 'ExcelSource'>('CorePayroll')
  const [showSample, setShowSample] = useState(false)
  const [simulationOpen, setSimulationOpen] = useState(false)
  const [salaryTemplateId, setSalaryTemplateId] = useState(() => salaryTemplates.length === 1 ? String(salaryTemplates[0].id) : '')
  const [periodMode, setPeriodMode] = useState<'all' | 'selected'>('all')
  const [selectedPeriods, setSelectedPeriods] = useState<string[]>([])
  const [periodScopeChanged, setPeriodScopeChanged] = useState(false)
  const [loading, setLoading] = useState(template.clientId > 0)
  const [saving, setSaving] = useState(false)
  const [stale, setStale] = useState(false)
  const [error, setError] = useState('')
  const [acknowledgedKey, setAcknowledgedKey] = useState('')
  const [uncertain, setUncertain] = useState(false)
  const [saved, setSaved] = useState(false)
  const [previewLease] = useState(() => new PayslipPreviewLease())
  const [designPreview, setDesignPreview] = useState<{ key: string; loading: boolean; url: string; blob: Blob | null; fileName: string; error: string } | null>(null)
  const previewAbort = useRef<AbortController | null>(null)
  const previewTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const sequence = useRef(0), mounted = useRef(true), pending = useRef<{ key: string; request: SavePayslipDesignRequest } | null>(null)
  const busyCallback = useRef(onBusyChange)
  useEffect(() => { busyCallback.current = onBusyChange }, [onBusyChange])
  const payPeriods = periodMode === 'all' ? null : selectedPeriods
  const periodIssues = payslipDesignPeriodIssues(payPeriods)
  const reviewKey = JSON.stringify({ template, sourceId, sourceHash: catalog?.sourceHash, fields, decimals, payPeriods, periodScopeChanged })
  const mappingIssues = payslipDesignIssues(catalog, fields, template.clientId)
  const scopeCapabilityIssues = payslipDesignScopeCapabilityIssues(catalog, payPeriods)
  const issues = [...mappingIssues, ...periodIssues, ...scopeCapabilityIssues]
  const previewIssues = [...payslipDesignPreviewIssues(catalog, fields, template.clientId), ...periodIssues]
  const locked = saving || uncertain
  const coreRequest = catalog?.sourceKind === 'CorePayroll' ? { sourceKind: 'CorePayroll' as const,
    simpleLayout: catalog.simpleLayout, keepMappedZeroRows: catalog.keepMappedZeroRows } : {}
  const simulationTemplate = salaryTemplates.find(value => String(value.id) === salaryTemplateId)
  const salaryTemplateKeys = salaryTemplates.map(value => String(value.id)).join(';')
  useEffect(() => {
    if (!salaryTemplates.some(value => String(value.id) === salaryTemplateId)) setSalaryTemplateId(salaryTemplates.length === 1 ? String(salaryTemplates[0].id) : '')
  }, [salaryTemplateKeys, salaryTemplateId]) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    mounted.current = true
    const request = ++sequence.current, controller = new AbortController()
    const current = () => mounted.current && sequence.current === request
    if (template.clientId > 0) void (async () => {
      const profileResult = template.id > 0 ? await getPayslipDesign(template.clientId, template.id, { signal: controller.signal }) : null
      if (!current()) return
      if (profileResult?.ok && profileResult.data) {
        const profile = profileResult.data
        if (profile.template.clientId !== template.clientId || profile.template.id !== template.id) { setError('The saved design belongs to a different client or template.'); setLoading(false); return }
        setProfileHash(profile.profileHash)
        setPeriodMode(profile.payPeriods == null ? 'all' : 'selected')
        setSelectedPeriods(profile.payPeriods ? [...profile.payPeriods] : [])
        setPeriodScopeChanged(false)
        setSourceId(profile.sourceBatchId); setDecimals(profile.amountDecimalPlaces); setFields(profile.fields.map(field => ({ ...field })))
        const portable = profile.sourceKind === 'CorePayroll'
        setCorePayrollOnly(portable)
        if (!portable) {
          const sources = await getPayslipDesignSources(template.clientId, { signal: controller.signal })
          if (!current()) return
          if (!sources.ok || !Array.isArray(sources.data)) { setError(sources.error || 'Unable to load this client’s saved sources.'); setLoading(false); return }
          setSources(sources.data)
        }
        const result = portable
          ? await getCopiedPayslipDesign(template.clientId, template.clientId, template.id, profile.profileHash, { signal: controller.signal })
          : await previewPayslipDesign(template.clientId, profile.sourceBatchId, { signal: controller.signal })
        if (!current()) return
        if (!result.ok || !result.data || result.data.clientId !== template.clientId || result.data.sourceBatchId !== profile.sourceBatchId) setError(result.error || 'Unable to verify the saved source for this client.')
        else {
          setCatalog(result.data)
          if (result.data.sourceHash !== profile.sourceHash) { setStale(true); setError('The saved source changed. Reload its current labels and review every mapping before saving.') }
          else setSaved(true)
        }
      } else if (profileResult && !profileResult.ok && profileResult.status !== 404) setError(profileResult.error || 'Unable to load the saved design.')
      else if (copySeed && template.id === 0) {
        const result = await getCopiedPayslipDesign(template.clientId, copySeed.template.clientId, copySeed.template.id, copySeed.profileHash, { signal: controller.signal })
        if (!current()) return
        if (!result.ok || !result.data || result.data.clientId !== template.clientId || result.data.sourceKind !== 'CorePayroll'
          || result.data.sourceBatchId !== '' || typeof result.data.keepMappedZeroRows !== 'boolean') setError(result.error || 'Update the API to copy a core payroll design without an Excel upload.')
        else { setCorePayrollOnly(true); setCatalog(result.data); setSourceId(''); setFields(result.data.fields.map(field => ({ ...field }))) }
      } else if (template.id === 0 && newDesignSource === 'CorePayroll') {
        const result = await getCorePayslipDesign(template.clientId, { signal: controller.signal })
        if (!current()) return
        if (!result.ok || !result.data || result.data.clientId !== template.clientId || result.data.sourceKind !== 'CorePayroll') setError(result.error || 'Unable to load this client’s salary template fields. Update the API and retry.')
        else { setCorePayrollOnly(true); setCatalog(result.data); setSourceId(''); setFields(result.data.fields.map(field => ({ ...field }))) }
      } else {
        const result = await getPayslipDesignSources(template.clientId, { signal: controller.signal })
        if (!current()) return
        if (!result.ok || !Array.isArray(result.data)) setError(result.error || 'Unable to load this client’s saved sources.')
        else setSources(result.data)
      }
      setLoading(false)
    })()
    return () => { mounted.current = false; sequence.current += 1; controller.abort(); busyCallback.current(false) }
    // The parent keys this editor by client and template. Name/visibility edits
    // affect acknowledgement without refetching or replacing mappings.
  }, [template.clientId, template.id, newDesignSource]) // eslint-disable-line react-hooks/exhaustive-deps

  const loadSource = async (id: string) => {
    if (locked || template.clientId <= 0) return
    const request = ++sequence.current
    setSourceId(id); setCatalog(null); setFields([]); setAcknowledgedKey(''); setError(''); setStale(false); setSaved(false)
    pending.current = null
    if (!id) return
    setLoading(true)
    const result = await previewPayslipDesign(template.clientId, id)
    if (!mounted.current || request !== sequence.current) return
    setLoading(false)
    if (!result.ok || !result.data || result.data.clientId !== template.clientId || result.data.sourceBatchId !== id) { setError(result.error || 'The selected source could not be verified for this client.'); return }
    setCatalog(result.data); setFields(result.data.fields.map(field => ({ ...field })))
  }
  const updatePeriodMode = (mode: 'all' | 'selected') => {
    if (loading || locked) return
    if (mode === 'selected' && !selectedPeriods.length) {
      const month = sources.find(source => source.id === sourceId)?.month
      if (month && !payslipDesignPeriodIssues([month]).length) setSelectedPeriods([month])
    }
    setPeriodMode(mode); setPeriodScopeChanged(true); setSaved(false)
  }
  const previewDesign = async () => {
    if (previewTimer.current) { clearTimeout(previewTimer.current); previewTimer.current = null }
    if (!catalog || loading || stale || locked || previewIssues.length || !template.name.trim()) return
    previewAbort.current?.abort()
    const controller = new AbortController()
    previewAbort.current = controller
    const key = reviewKey, requestSequence = previewLease.begin(key)
    setDesignPreview({ key, loading: true, url: '', blob: null, fileName: '', error: '' })
    const result = await previewPayslipDesignPdf({
      requestId: '', template: { ...template, name: template.name.trim() }, sourceBatchId: sourceId, sourceHash: catalog.sourceHash,
      existingProfileHash: profileHash, amountDecimalPlaces: decimals, fields: fields.map(field => ({ ...field })), reviewed: false,
      ...coreRequest,
      ...payslipDesignPeriodRequest(payPeriods, periodScopeChanged),
    }, { signal: controller.signal })
    if (!mounted.current || !previewLease.current(key, requestSequence)) return
    if (!result.ok || !result.data) {
      setDesignPreview({ key, loading: false, url: '', blob: null, fileName: '', error: result.status === 404 ? 'Update the API to preview this Excel Payslip design.' : result.error || 'Unable to preview this design.' })
      return
    }
    const url = previewLease.accept(key, requestSequence, result.data.blob)
    if (url) setDesignPreview({ key, loading: false, url, blob: result.data.blob, fileName: result.data.fileName, error: '' })
  }
  const save = async () => {
    if (saving || loading || stale || issues.length || acknowledgedKey !== reviewKey || !template.name.trim()) return
    if (pending.current?.key !== reviewKey) pending.current = { key: reviewKey, request: { requestId: crypto.randomUUID(), template: { ...template, name: template.name.trim() }, sourceBatchId: sourceId, sourceHash: catalog!.sourceHash, existingProfileHash: profileHash, amountDecimalPlaces: decimals, ...coreRequest, ...payslipDesignPeriodRequest(payPeriods, periodScopeChanged), fields: fields.map(field => ({ ...field })), reviewed: true } }
    const request = pending.current.request
    setSaving(true); onBusyChange(true); setError('')
    const result = await savePayslipDesign(request)
    if (!mounted.current) return
    setSaving(false)
    if (!result.ok || !result.data) {
      const unknown = result.status === 0 || result.status >= 500
      setUncertain(unknown); onBusyChange(unknown)
      setError(unknown ? 'Save status could not be confirmed. Retry this same save before changing the design.' : result.error || 'Unable to save the design.')
      if (!unknown) { pending.current = null; setAcknowledgedKey('') }
      return
    }
    if (result.data.clientId !== template.clientId || result.data.theme !== 'Excel Payslip') { setUncertain(true); setError('The save response did not match this client and theme. Retry the same request to verify it.'); return }
    pending.current = null; setUncertain(false); onBusyChange(false); setSaved(true); setAcknowledgedKey(''); onSaved(result.data)
  }

  useEffect(() => {
    previewAbort.current?.abort(); previewLease.clear()
    if (!showSample || loading || stale || locked || previewIssues.length || !catalog || !template.name.trim()) return
    previewTimer.current = setTimeout(() => { previewTimer.current = null; void previewDesign() }, 600)
    return () => {
      if (previewTimer.current) clearTimeout(previewTimer.current)
      previewTimer.current = null; previewAbort.current?.abort(); previewLease.clear()
    }
    // reviewKey serializes every preview input. Unrelated render/callback changes
    // must not cancel a current PDF or issue another request.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reviewKey, loading, stale, locked, previewIssues.length, previewLease, showSample])

  useEffect(() => () => { previewAbort.current?.abort(); previewLease.clear() }, [previewLease])
  const currentPreview = !loading && !locked && !stale && designPreview?.key === reviewKey ? designPreview : null
  const downloadSample = () => {
    if (!currentPreview?.url) return
    const link = document.createElement('a')
    link.href = currentPreview.url; link.download = currentPreview.fileName; link.click()
  }

  if (template.clientId <= 0) return <Alert type="info" showIcon message="Choose a client to configure its Excel Payslip design." />
  return <section className="excel-payslip-design-editor" aria-label="Excel Payslip design">
    <Alert type="info" showIcon message={corePayrollOnly ? 'Design only · amounts from this client’s core payroll' : 'Use saved Excel labels with approved payroll amounts'} description={corePayrollOnly ? 'Labels, field mappings, order and visibility belong to this independent template. Final slips read this client’s approved payroll and employee details. No Excel upload is required.' : 'Choose this client’s source, map each label to a saved payroll component or employee field, then save the template and design together. Hiding a field changes its display only.'} />
    {copySeed && catalog && <Alert type="success" showIcon message={`Independent copy of ${copySeed.template.name}`} description="Layout, mappings, order, visibility and amount display are ready. Save this copy as it is or edit it first; the original template is unchanged." />}
    <div className="excel-payslip-design-controls">
      {template.id === 0 && !copySeed && <label><span>Design fields</span><Select aria-label="Payslip design fields source" value={newDesignSource} disabled={loading || locked} options={[{ value: 'CorePayroll', label: 'Salary template components' }, { value: 'ExcelSource', label: 'Saved Excel labels (optional)' }]} onChange={value => { setNewDesignSource(value); setCorePayrollOnly(value === 'CorePayroll'); setCatalog(null); setFields([]); setSourceId(''); setProfileHash(''); setAcknowledgedKey(''); setError(''); setStale(false); setSaved(false); setLoading(true) }} /></label>}
      {!corePayrollOnly && <label><span>Saved source</span><SearchSelect value={sourceId} disabled={loading || locked} placeholder="Select this client’s saved batch" options={sources.map(source => ({ value: source.id, label: source.month + ' · ' + source.name }))} onChange={value => void loadSource(value)} /></label>}
      <label><span>Amount display</span><SearchSelect value={decimals} disabled={locked} options={[{ value: 0, label: 'Whole rupees' }, { value: 2, label: 'Two decimal places' }]} onChange={value => { setDecimals(Number(value) === 2 ? 2 : 0); setSaved(false) }} /></label>
      <label><span>Apply design to</span><Select aria-label="Payroll month scope" value={periodMode} disabled={loading || locked} options={[{ value: 'all', label: 'All payroll months' }, { value: 'selected', label: 'Selected payroll months' }]} onChange={updatePeriodMode} style={{ width: '100%', minWidth: 0 }} /></label>
      {periodMode === 'selected' && <label><span>Payroll months</span><Select aria-label="Selected payroll months" mode="tags" value={selectedPeriods} disabled={loading || locked} allowClear showSearch maxTagCount="responsive" tokenSeparators={[',']} status={periodIssues.length ? 'error' : undefined} placeholder="Select months or enter YYYY-MM" options={[...new Set([...sources.map(source => source.month), ...selectedPeriods])].filter(month => !payslipDesignPeriodIssues([month]).length).sort().map(month => ({ value: month, label: payslipDesignPeriodLabel(month) }))} onChange={(months: string[]) => { setSelectedPeriods(months); setPeriodScopeChanged(true); setSaved(false) }} style={{ width: '100%', minWidth: 0 }} /></label>}
      {!corePayrollOnly && <Button icon={<ReloadOutlined />} disabled={!sourceId || loading || locked} onClick={() => void loadSource(sourceId)}>Reload source labels</Button>}
    </div>
    <Space wrap aria-label="Saved design payroll month scope"><Tag color={periodMode === 'selected' ? 'blue' : undefined}>{periodMode === 'all' ? 'All payroll months' : selectedPeriods.length === 1 ? 'Selected: ' + payslipDesignPeriodLabel(selectedPeriods[0]) : selectedPeriods.length ? selectedPeriods.length + ' selected payroll months' : 'Selected: Choose months'}</Tag></Space>
    {periodIssues.length > 0 && <Alert type="warning" showIcon message={periodIssues[0]} />}
    {catalog && scopeCapabilityIssues.length > 0 && <Alert type="warning" showIcon message={scopeCapabilityIssues[0]} />}
    {error && <Alert type={uncertain ? 'warning' : 'error'} showIcon message={error} />}
    {loading && <div className="excel-payslip-design-loading" role="status"><Spin /><span>Reading saved design fields…</span></div>}
    <div className="payslip-design-simulator-toolbar"><label><span>Salary template for simulation</span><Select aria-label="Payslip simulation salary template" value={salaryTemplateId || undefined} showSearch optionFilterProp="label" placeholder="Select salary template / location" disabled={locked || loading} options={salaryTemplates.map(value => ({ value: String(value.id), label: value.name }))} onChange={setSalaryTemplateId} /></label><Button type="primary" icon={<EyeOutlined />} disabled={!simulationTemplate || !catalog || loading || locked || stale || !!mappingIssues.length || !template.name.trim()} title={mappingIssues[0]} onClick={() => setSimulationOpen(true)}>Simulate & preview payslip</Button><Button type="text" disabled={loading || locked} onClick={() => setShowSample(value => !value)}>{showSample ? 'Hide layout sample' : 'Layout sample'}</Button></div>
    {showSample && <section className="excel-payslip-design-preview" aria-label="Payslip design preview">
      <header><div><strong>Payslip design preview</strong><small>Sample design — synthetic employee and salary values</small></div><Space wrap><Button icon={<EyeOutlined />} loading={!!currentPreview?.loading} disabled={!catalog || loading || locked || stale || !!previewIssues.length || !template.name.trim()} onClick={() => void previewDesign()}>Preview design</Button><Button disabled={!currentPreview?.url} onClick={downloadSample}>Download sample PDF</Button></Space></header>
      {currentPreview?.error
        ? <Alert type="error" showIcon message={currentPreview.error} />
        : currentPreview?.loading
          ? <div className="excel-payslip-design-preview-empty" role="status"><Spin /><span>Rendering your Excel Payslip design…</span></div>
          : currentPreview?.blob
            ? <PdfCanvasPreview blob={currentPreview.blob} title="Synthetic Excel Payslip design preview" />
            : <div className="excel-payslip-design-preview-empty"><EyeOutlined /><span>{!catalog ? corePayrollOnly ? 'Loading copied payslip design…' : 'Choose a saved source to preview its payslip layout.' : periodIssues.length ? periodIssues[0] : previewIssues.length ? 'Review the design fields to preview this layout.' : 'Preparing the sample design preview…'}</span></div>}
      <p>The same layout renders approved payroll in Payslip Register. This sample uses synthetic values; complete the field mappings before saving.</p>
    </section>}
    {catalog && !loading && <>
      <Space wrap><Tag color="blue">{'Shown: ' + fields.filter(field => field.visible).length + ' of ' + fields.length + ' fields'}</Tag><Tag>{catalog.simpleLayout ? 'Layout: Simple' : 'Layout: Standard'}</Tag><span>{corePayrollOnly ? 'Independent design with core payroll and Employee Master mappings.' : 'Saved source and available Master fields; this count is not a fixed limit.'}</span></Space>
      <PayslipFieldPicker catalog={catalog} fields={fields} disabled={locked} portable={corePayrollOnly} onChange={next => { setFields(next); setSaved(false) }} />
      {mappingIssues.length > 0 && <Alert type="warning" showIcon message={mappingIssues.length + ' mapping ' + (mappingIssues.length === 1 ? 'item needs' : 'items need') + ' review'} description={<ul>{mappingIssues.slice(0, 8).map(issue => <li key={issue}>{issue}</li>)}{mappingIssues.length > 8 && <li>Review the remaining fields above.</li>}</ul>} />}
      <div className="excel-payslip-design-save-bar">
        <Checkbox disabled={locked || !!issues.length || stale || !template.name.trim()} checked={acknowledgedKey === reviewKey} onChange={event => setAcknowledgedKey(event.target.checked ? reviewKey : '')}>I reviewed this client’s labels, mappings, order, visibility and payroll months.</Checkbox>
        <Space wrap><Button type="primary" loading={saving} disabled={loading || saving || stale || !!issues.length || acknowledgedKey !== reviewKey || !template.name.trim()} onClick={() => void save()}>{uncertain ? 'Retry same save' : 'Save template and design'}</Button>{(saved || template.id > 0) && <Button href={'/reports/payroll-reports?report=payslip-register&clientId=' + template.clientId}>Open Payslip Register</Button>}</Space>
      </div>
    </>}
    {!corePayrollOnly && !loading && !sources.length && !error && <Alert type="info" showIcon message="No saved Excel Payslip source is available for this client." />}
    {simulationOpen && simulationTemplate && catalog && <PayrollSimulation template={simulationTemplate} components={components} clients={clients} fixedClientId={template.clientId} preferEmployeeSalary={false} payslipDesign={{ template, fields, nativeCodes: catalog.nativeCodes, amountDecimalPlaces: decimals, simpleLayout: catalog.simpleLayout, keepMappedZeroRows: catalog.keepMappedZeroRows ?? !catalog.simpleLayout, organizationName, logoDataUrl }} onClose={() => setSimulationOpen(false)} />}
  </section>
}
