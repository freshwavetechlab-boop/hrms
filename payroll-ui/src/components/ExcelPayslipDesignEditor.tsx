import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Select, Space, Spin, Tag } from 'antd'
import { ArrowDownOutlined, ArrowUpOutlined, EyeOutlined, ReloadOutlined } from '@ant-design/icons'
import { getPayslipDesign, getPayslipDesignSources, previewPayslipDesign, previewPayslipDesignPdf, savePayslipDesign, type PayslipDesignCatalog, type PayslipDesignField, type PayslipDesignSource, type PayslipDesignTemplate, type SavePayslipDesignRequest } from '../services/payslipService'
import { movePayslipDesignField, nativePayslipKind, PayslipPreviewLease, payslipDesignIssues, payslipDesignPeriodIssues, payslipDesignPeriodLabel, payslipDesignPeriodRequest, payslipDesignPreviewIssues, payslipDesignScopeCapabilityIssues, payslipDesignInformationOptions, payslipKindLabel } from '../utils/payslipDesign'
import SearchSelect from './SearchSelect'
import PdfCanvasPreview from './PdfCanvasPreview'
import './ExcelPayslipDesignEditor.css'

type Props = { template: PayslipDesignTemplate; onSaved: (template: PayslipDesignTemplate) => void; onBusyChange: (busy: boolean) => void }
export default function ExcelPayslipDesignEditor({ template, onSaved, onBusyChange }: Props) {
  const [sources, setSources] = useState<PayslipDesignSource[]>([])
  const [sourceId, setSourceId] = useState('')
  const [profileHash, setProfileHash] = useState('')
  const [catalog, setCatalog] = useState<PayslipDesignCatalog | null>(null)
  const [fields, setFields] = useState<PayslipDesignField[]>([])
  const [decimals, setDecimals] = useState<0 | 2>(0)
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

  useEffect(() => {
    mounted.current = true
    const request = ++sequence.current, controller = new AbortController()
    const current = () => mounted.current && sequence.current === request
    if (template.clientId > 0) void (async () => {
      const [sourceResult, profileResult] = await Promise.all([
        getPayslipDesignSources(template.clientId, { signal: controller.signal }),
        template.id > 0 ? getPayslipDesign(template.clientId, template.id, { signal: controller.signal }) : Promise.resolve(null),
      ])
      if (!current()) return
      if (!sourceResult.ok || !Array.isArray(sourceResult.data)) { setError(sourceResult.status === 404 ? 'Update the API to configure an Excel Payslip design.' : sourceResult.error || 'Unable to load this client’s saved sources.'); setLoading(false); return }
      setSources(sourceResult.data)
      if (profileResult?.ok && profileResult.data) {
        const profile = profileResult.data
        if (profile.template.clientId !== template.clientId || profile.template.id !== template.id) { setError('The saved design belongs to a different client or template.'); setLoading(false); return }
        setProfileHash(profile.profileHash)
        setPeriodMode(profile.payPeriods == null ? 'all' : 'selected')
        setSelectedPeriods(profile.payPeriods ? [...profile.payPeriods] : [])
        setPeriodScopeChanged(false)
        setSourceId(profile.sourceBatchId); setDecimals(profile.amountDecimalPlaces); setFields(profile.fields.map(field => ({ ...field })))
        const result = await previewPayslipDesign(template.clientId, profile.sourceBatchId, { signal: controller.signal })
        if (!current()) return
        if (!result.ok || !result.data || result.data.clientId !== template.clientId || result.data.sourceBatchId !== profile.sourceBatchId) setError(result.error || 'Unable to verify the saved source for this client.')
        else {
          setCatalog(result.data)
          if (result.data.sourceHash !== profile.sourceHash) { setStale(true); setError('The saved source changed. Reload its current labels and review every mapping before saving.') }
          else setSaved(true)
        }
      } else if (profileResult && !profileResult.ok && profileResult.status !== 404) setError(profileResult.error || 'Unable to load the saved design.')
      setLoading(false)
    })()
    return () => { mounted.current = false; sequence.current += 1; controller.abort(); busyCallback.current(false) }
    // The parent keys this editor by client and template. Name/visibility edits
    // affect acknowledgement without refetching or replacing mappings.
  }, [template.clientId, template.id])

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
  const updateField = (column: number, patch: Partial<PayslipDesignField>) => {
    if (locked) return
    setSaved(false); setFields(current => current.map(field => field.sourceColumn === column ? { ...field, ...patch } : field))
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
    if (pending.current?.key !== reviewKey) pending.current = { key: reviewKey, request: { requestId: crypto.randomUUID(), template: { ...template, name: template.name.trim() }, sourceBatchId: sourceId, sourceHash: catalog!.sourceHash, existingProfileHash: profileHash, amountDecimalPlaces: decimals, ...payslipDesignPeriodRequest(payPeriods, periodScopeChanged), fields: fields.map(field => ({ ...field })), reviewed: true } }
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
    if (loading || stale || locked || previewIssues.length || !catalog || !template.name.trim()) return
    previewTimer.current = setTimeout(() => { previewTimer.current = null; void previewDesign() }, 600)
    return () => {
      if (previewTimer.current) clearTimeout(previewTimer.current)
      previewTimer.current = null; previewAbort.current?.abort(); previewLease.clear()
    }
    // reviewKey serializes every preview input. Unrelated render/callback changes
    // must not cancel a current PDF or issue another request.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reviewKey, loading, stale, locked, previewIssues.length, previewLease])

  useEffect(() => () => { previewAbort.current?.abort(); previewLease.clear() }, [previewLease])
  const currentPreview = !loading && !locked && !stale && designPreview?.key === reviewKey ? designPreview : null
  const downloadSample = () => {
    if (!currentPreview?.url) return
    const link = document.createElement('a')
    link.href = currentPreview.url; link.download = currentPreview.fileName; link.click()
  }

  if (template.clientId <= 0) return <Alert type="info" showIcon message="Choose a client to configure its Excel Payslip design." />
  return <section className="excel-payslip-design-editor" aria-label="Excel Payslip design">
    <Alert type="info" showIcon message="Use saved Excel labels with approved payroll amounts" description="Choose this client’s source, map each label to a saved payroll component or employee field, then save the template and design together. Hiding a field changes its display only." />
    <div className="excel-payslip-design-controls">
      <label><span>Saved source</span><SearchSelect value={sourceId} disabled={loading || locked} placeholder="Select this client’s saved batch" options={sources.map(source => ({ value: source.id, label: source.month + ' · ' + source.name }))} onChange={value => void loadSource(value)} /></label>
      <label><span>Amount display</span><SearchSelect value={decimals} disabled={locked} options={[{ value: 0, label: 'Whole rupees' }, { value: 2, label: 'Two decimal places' }]} onChange={value => { setDecimals(Number(value) === 2 ? 2 : 0); setSaved(false) }} /></label>
      <label><span>Apply design to</span><Select aria-label="Payroll month scope" value={periodMode} disabled={loading || locked} options={[{ value: 'all', label: 'All payroll months' }, { value: 'selected', label: 'Selected payroll months' }]} onChange={updatePeriodMode} style={{ width: '100%', minWidth: 0 }} /></label>
      {periodMode === 'selected' && <label><span>Payroll months</span><Select aria-label="Selected payroll months" mode="tags" value={selectedPeriods} disabled={loading || locked} allowClear showSearch maxTagCount="responsive" tokenSeparators={[',']} status={periodIssues.length ? 'error' : undefined} placeholder="Select months or enter YYYY-MM" options={[...new Set([...sources.map(source => source.month), ...selectedPeriods])].filter(month => !payslipDesignPeriodIssues([month]).length).sort().map(month => ({ value: month, label: payslipDesignPeriodLabel(month) }))} onChange={(months: string[]) => { setSelectedPeriods(months); setPeriodScopeChanged(true); setSaved(false) }} style={{ width: '100%', minWidth: 0 }} /></label>}
      <Button icon={<ReloadOutlined />} disabled={!sourceId || loading || locked} onClick={() => void loadSource(sourceId)}>Reload source labels</Button>
    </div>
    <Space wrap aria-label="Saved design payroll month scope"><Tag color={periodMode === 'selected' ? 'blue' : undefined}>{periodMode === 'all' ? 'All payroll months' : selectedPeriods.length === 1 ? 'Selected: ' + payslipDesignPeriodLabel(selectedPeriods[0]) : selectedPeriods.length ? selectedPeriods.length + ' selected payroll months' : 'Selected: Choose months'}</Tag></Space>
    {periodIssues.length > 0 && <Alert type="warning" showIcon message={periodIssues[0]} />}
    {catalog && scopeCapabilityIssues.length > 0 && <Alert type="warning" showIcon message={scopeCapabilityIssues[0]} />}
    {error && <Alert type={uncertain ? 'warning' : 'error'} showIcon message={error} />}
    {loading && <div className="excel-payslip-design-loading" role="status"><Spin /><span>Reading saved design fields…</span></div>}
    <section className="excel-payslip-design-preview" aria-label="Excel Payslip design preview">
      <header><div><strong>Excel Payslip preview</strong><small>Sample design — synthetic employee and salary values</small></div><Space wrap><Button icon={<EyeOutlined />} loading={!!currentPreview?.loading} disabled={!catalog || loading || locked || stale || !!previewIssues.length || !template.name.trim()} onClick={() => void previewDesign()}>Preview design</Button><Button disabled={!currentPreview?.url} onClick={downloadSample}>Download sample PDF</Button></Space></header>
      {currentPreview?.error
        ? <Alert type="error" showIcon message={currentPreview.error} />
        : currentPreview?.loading
          ? <div className="excel-payslip-design-preview-empty" role="status"><Spin /><span>Rendering your Excel Payslip design…</span></div>
          : currentPreview?.blob
            ? <PdfCanvasPreview blob={currentPreview.blob} title="Synthetic Excel Payslip design preview" />
            : <div className="excel-payslip-design-preview-empty"><EyeOutlined /><span>{!catalog ? 'Choose a saved source to preview its payslip layout.' : periodIssues.length ? periodIssues[0] : previewIssues.length ? 'Reload the source labels to preview this layout.' : 'Preparing the sample design preview…'}</span></div>}
      <p>The same layout renders approved payroll in Payslip Register. This sample uses synthetic values; complete the field mappings before saving.</p>
    </section>
    {catalog && !loading && <>
      <Space wrap><Tag color="blue">{'Shown: ' + fields.filter(field => field.visible).length + ' of ' + fields.length + ' fields'}</Tag><Tag>{catalog.simpleLayout ? 'Layout: Simple' : 'Layout: Standard'}</Tag><span>Saved source and available Master fields; this count is not a fixed limit.</span></Space>
      <div className="excel-payslip-design-fields">
        {fields.map((field, index) => <section className="excel-payslip-design-field" key={field.sourceColumn}>
          <div className="excel-payslip-design-label"><strong>{field.label}</strong><small>{field.sourceColumn >= 1500 ? 'Master information' : payslipKindLabel(field.kind)}</small></div>
          <label className="excel-payslip-design-mapping"><span className="sr-only">{field.label} mapping</span><SearchSelect disabled={locked || field.sourceColumn >= 1500} value={field.kind === 'info' ? field.infoToken : field.nativeCode} allowClear={field.kind === 'info' && !field.visible} placeholder={field.kind === 'info' ? field.visible ? 'Select employee / payroll field' : 'Hidden — mapping optional' : 'Select saved payroll component'} options={field.kind === 'info' ? payslipDesignInformationOptions(catalog, field.infoToken) : catalog.nativeCodes.filter(code => nativePayslipKind(code.category) === field.kind).map(code => ({ value: code.code, label: code.name + ' · ' + code.code }))} onChange={value => updateField(field.sourceColumn, field.kind === 'info' ? { infoToken: value, nativeCode: '' } : { nativeCode: value, infoToken: '' })} /></label>
          <div className="excel-payslip-design-field-actions"><Checkbox checked={field.visible} disabled={locked} onChange={event => updateField(field.sourceColumn, { visible: event.target.checked })}>Show</Checkbox><Button size="small" icon={<ArrowUpOutlined />} aria-label={'Move ' + field.label + ' up'} disabled={locked || index === 0} onClick={() => { setSaved(false); setFields(current => movePayslipDesignField(current, field.sourceColumn, -1)) }} /><Button size="small" icon={<ArrowDownOutlined />} aria-label={'Move ' + field.label + ' down'} disabled={locked || index === fields.length - 1} onClick={() => { setSaved(false); setFields(current => movePayslipDesignField(current, field.sourceColumn, 1)) }} /></div>
        </section>)}
      </div>
      {mappingIssues.length > 0 && <Alert type="warning" showIcon message={mappingIssues.length + ' mapping ' + (mappingIssues.length === 1 ? 'item needs' : 'items need') + ' review'} description={<ul>{mappingIssues.slice(0, 8).map(issue => <li key={issue}>{issue}</li>)}{mappingIssues.length > 8 && <li>Review the remaining fields above.</li>}</ul>} />}
      <div className="excel-payslip-design-save-bar">
        <Checkbox disabled={locked || !!issues.length || stale || !template.name.trim()} checked={acknowledgedKey === reviewKey} onChange={event => setAcknowledgedKey(event.target.checked ? reviewKey : '')}>I reviewed this client’s labels, mappings, order, visibility and payroll months.</Checkbox>
        <Space wrap><Button type="primary" loading={saving} disabled={loading || saving || stale || !!issues.length || acknowledgedKey !== reviewKey || !template.name.trim()} onClick={() => void save()}>{uncertain ? 'Retry same save' : 'Save template and design'}</Button>{(saved || template.id > 0) && <Button href={'/reports/payroll-reports?report=payslip-register&clientId=' + template.clientId}>Open Payslip Register</Button>}</Space>
      </div>
    </>}
    {!loading && !sources.length && !error && <Alert type="info" showIcon message="No saved Excel Payslip source is available for this client." />}
  </section>
}
