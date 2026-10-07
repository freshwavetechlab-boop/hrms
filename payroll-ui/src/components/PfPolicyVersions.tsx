import { useEffect, useState } from 'react'
import { PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Col, Descriptions, Divider, Drawer, Form, Input, InputNumber, Row, Segmented, Select, Space, Tag } from 'antd'
import { useSearchParams } from 'react-router-dom'
import type { Client, Component, Structure } from '../types/payroll'
import { getPfPolicyVersions, publishPfPolicyVersion, savePfPolicyVersion, type PfContributionRule, type PfPolicyInput, type PfPolicyVersion } from '../services/pfPolicyService'
import { useSessionPreference } from '../hooks/useRecruitmentPreferences'
import { useAuthSession } from './AuthGate'
import DataTable from './DataTable'
import { PageHeaderPortal } from './layout/AppPageHeader'
import './PfPolicyVersions.css'

type ContributionDraft = { componentId: string; ratePercent: number | null; ceilingMode: '' | 'Capped' | 'Uncapped'; monthlyWageCeiling: number | null }
type PolicyDraft = Omit<PfPolicyInput, 'ceilingBasis' | 'contributions'> & { ceilingBasis: PfPolicyInput['ceilingBasis'] | ''; contributions: ContributionDraft[]; overrideEdli: boolean }
const day = (value: string) => value.slice(0, 10)
const dateTime = (value: string | null) => value ? new Date(value.endsWith('Z') ? value : `${value}Z`).toLocaleString() : '—'
const templateClientIds = (value: string) => value.split(/[;,|]/).map(item => Number(item.split(':')[0]))
const emptyDraft = (): PolicyDraft => ({ salaryStructureId: '', name: '', effectiveFrom: '', reason: '', baseComponentCode: '', ceilingBasis: '', contributions: [], edliMonthlyWageCeiling: null, overrideEdli: false })
const toDraft = (row: PfPolicyVersion, keepDraft: boolean): PolicyDraft => ({
  ...row, id: keepDraft ? row.id : undefined, effectiveFrom: keepDraft ? day(row.effectiveFrom) : '', reason: keepDraft ? row.reason : '',
  overrideEdli: row.edliMonthlyWageCeiling !== null,
  contributions: row.contributions.map(rule => ({ ...rule, ceilingMode: rule.monthlyWageCeiling === null ? 'Uncapped' : 'Capped' })),
})

export default function PfPolicyVersions({ clients, components, templates, onMessage, showFixedClientChip = true }: {
  clients: Client[]; components: Component[]; templates: Structure[]; onMessage: (message: string) => void; showFixedClientChip?: boolean
}) {
  const session = useAuthSession()
  const [searchParams] = useSearchParams()
  const canManage = Boolean(session?.user.permissions.includes('tax.statutory.manage'))
  const fixedClientId = session?.user.clientId
  const permittedClients = clients.filter(client => client.isActive && (!fixedClientId || client.id === fixedClientId))
  const [preferredClientId, setPreferredClientId] = useSessionPreference(`pf-policy.ui:${session?.user.id ?? 'local'}:client`, Number(searchParams.get('clientId')) || 0)
  const clientId = fixedClientId || permittedClients.find(client => client.id === preferredClientId)?.id || permittedClients[0]?.id || 0
  const client = permittedClients.find(row => row.id === clientId)
  const [view, setView] = useSessionPreference<'Cards' | 'Table'>(`pf-policy.ui:${session?.user.id ?? 'local'}:${clientId}:view`, 'Table')
  const [versions, setVersions] = useState<PfPolicyVersion[]>([])
  const [loading, setLoading] = useState(false), [loadError, setLoadError] = useState(''), [reload, setReload] = useState(0)
  const [loadedClientId, setLoadedClientId] = useState(0)
  const [draft, setDraft] = useState<PolicyDraft>(emptyDraft)
  const [open, setOpen] = useState(false), [saving, setSaving] = useState(false), [saveError, setSaveError] = useState('')
  const [detail, setDetail] = useState<PfPolicyVersion | null>(null)
  const clientTemplates = templates.filter(row => row.active && templateClientIds(row.clientId).includes(clientId))
  const selectedTemplate = clientTemplates.find(row => String(row.id) === draft.salaryStructureId)
  const templateComponents = components.filter(component => component.active && selectedTemplate?.lines.some(line => String(line.componentId).split(':')[0] === String(component.id)))
  const pfComponents = templateComponents.filter(component => ['PF Employee', 'PF Employer'].includes(component.statutoryType))
  const baseComponents = templateComponents.filter(component => component.category === 'Earning')
  const visibleVersions = loadedClientId === clientId ? [...versions].sort((a, b) => b.versionNumber - a.versionNumber) : []
  const componentName = (id: string) => { const component = components.find(row => String(row.id) === id); return component ? `${component.code} · ${component.name}` : `Component #${id}` }
  const contributionName = (rule: PfContributionRule) => rule.componentCode ? `${rule.componentCode}${rule.statutoryType ? ` (${rule.statutoryType})` : ''}` : componentName(rule.componentId)
  const templateName = (id: string) => templates.find(row => String(row.id) === id)?.name || `Template #${id}`
  const basisName = (value: string) => value === 'PayableDays' ? 'Ceiling prorated by payable days' : 'Ceiling prorated by calendar days'
  const ceilingName = (value: number | null) => value === null ? 'Uncapped' : `₹${value.toLocaleString('en-IN')}`

  useEffect(() => {
    let active = true
    void Promise.resolve().then(async () => {
      if (!active) return
      setOpen(false); setDetail(null); setSaveError(''); setLoadError(''); setLoadedClientId(0)
      if (!clientId) { setVersions([]); setLoading(false); return }
      setLoading(true)
      const response = await getPfPolicyVersions(clientId)
      if (!active) return
      if (response.ok) { setVersions(response.data); setLoadedClientId(clientId) }
      else setLoadError(response.error || 'Unable to load PF policy versions.')
      setLoading(false)
    })
    return () => { active = false }
  }, [clientId, reload])

  const beginVersion = (source?: PfPolicyVersion) => {
    setDraft(source ? toDraft(source, source.status === 'Draft') : emptyDraft())
    setSaveError(''); setOpen(true)
  }
  const patchContribution = (index: number, patch: Partial<ContributionDraft>) => setDraft(current => ({ ...current, contributions: current.contributions.map((rule, i) => i === index ? { ...rule, ...patch } : rule) }))
  const rememberVersion = (row: PfPolicyVersion) => setVersions(current => [...current.filter(item => item.id !== row.id), row])
  const publish = async () => {
    if (!canManage || !clientId || saving) return
    if (!selectedTemplate) return setSaveError('Select an active salary template for this client.')
    if (!draft.name.trim() || !draft.effectiveFrom || !draft.reason.trim()) return setSaveError('Enter a policy name, effective date and change reason.')
    if (!draft.ceilingBasis) return setSaveError('Choose how the monthly wage ceiling applies to partial attendance.')
    if (!baseComponents.some(row => row.code === draft.baseComponentCode)) return setSaveError('Select an earning component from this template as the PF wage base.')
    const mapped = draft.contributions.map(rule => pfComponents.find(component => String(component.id) === rule.componentId))
    if (mapped.some(row => !row) || mapped.filter(row => row?.statutoryType === 'PF Employee').length !== 1) return setSaveError('Map exactly one PF Employee component. Other mappings must be PF Employer components from this template.')
    if (new Set(draft.contributions.map(rule => rule.componentId)).size !== draft.contributions.length) return setSaveError('Each contribution component can be mapped only once.')
    if (draft.contributions.some(rule => rule.ratePercent === null || rule.ratePercent < 0 || rule.ratePercent > 100 || !rule.ceilingMode || (rule.ceilingMode === 'Capped' && (rule.monthlyWageCeiling === null || rule.monthlyWageCeiling < 0)))) return setSaveError('Enter each contribution rate and choose a monthly ceiling of zero or more, or Uncapped.')
    if (draft.overrideEdli && (draft.edliMonthlyWageCeiling === null || draft.edliMonthlyWageCeiling < 0)) return setSaveError('Enter an EDLI wage ceiling of zero or more, or retain the existing EDLI rule.')
    const payload: PfPolicyInput = {
      ...(draft.id ? { id: draft.id } : {}), salaryStructureId: draft.salaryStructureId, name: draft.name.trim(), effectiveFrom: draft.effectiveFrom,
      reason: draft.reason.trim(), baseComponentCode: draft.baseComponentCode, ceilingBasis: draft.ceilingBasis,
      contributions: draft.contributions.map(rule => ({ componentId: rule.componentId, ratePercent: rule.ratePercent!, monthlyWageCeiling: rule.ceilingMode === 'Uncapped' ? null : rule.monthlyWageCeiling })),
      edliMonthlyWageCeiling: draft.overrideEdli ? draft.edliMonthlyWageCeiling : null,
    }
    setSaving(true); setSaveError('')
    try {
      const saved = await savePfPolicyVersion(clientId, payload)
      if (!saved.ok || !saved.data) return setSaveError(saved.error || 'Unable to save the new policy version.')
      const savedVersion = saved.data
      rememberVersion(savedVersion)
      setDraft(toDraft(savedVersion, true))
      const published = await publishPfPolicyVersion(clientId, savedVersion.id, payload.reason)
      if (!published.ok || !published.data) return setSaveError(`${published.error || 'Publication failed.'} Draft v${savedVersion.versionNumber} is retained. Review it and retry publishing.`)
      rememberVersion(published.data); setOpen(false)
      onMessage(`PF policy v${published.data.versionNumber} published. Existing payroll results have not been recalculated.`)
    } finally { setSaving(false) }
  }

  return <section className="pf-policy-versions" aria-label="PF policy versions">
    <PageHeaderPortal slot="statutory-settings-client-controls">
      {fixedClientId ? showFixedClientChip && <span className="scoped-client-chip">{client?.name || `Client #${fixedClientId}`}</span> : <Select aria-label="PF policy client" showSearch optionFilterProp="label" value={clientId || undefined} disabled={saving} placeholder="Select client" style={{ width: 240, maxWidth: '100%' }} options={permittedClients.map(row => ({ value: row.id, label: row.name }))} onChange={setPreferredClientId} />}
    </PageHeaderPortal>
    <PageHeaderPortal slot="statutory-settings-view-controls"><Segmented aria-label="PF history view" options={['Table', 'Cards']} value={view} onChange={value => setView(value as 'Table' | 'Cards')} /></PageHeaderPortal>
    <PageHeaderPortal slot="statutory-settings-page-controls"><Space wrap><Button icon={<ReloadOutlined />} disabled={loading || saving || !clientId} onClick={() => setReload(value => value + 1)}>Refresh</Button>{canManage && <Button type="primary" icon={<PlusOutlined />} disabled={!clientId || loading || !!loadError || loadedClientId !== clientId} onClick={() => beginVersion()}>New PF version</Button>}</Space></PageHeaderPortal>
    {loadError && <Alert type="error" showIcon message={loadError} action={<Button size="small" onClick={() => setReload(value => value + 1)}>Retry</Button>} />}
    {!clientId && <Alert type="info" showIcon message="Select a client to view PF policy versions." />}
    <DataTable rows={visibleVersions} loading={loading} fillHeight view={view} getRowId={row => row.id} title="PF policy history" exportFileName={`pf-policy-versions-${clientId || 'client'}`} emptyText={loadError ? 'History could not be loaded.' : 'No PF policy versions. Existing salary-template calculations continue.'} columns={[
      { key: 'versionNumber', label: 'Version', render: row => <Tag>v{row.versionNumber}</Tag> },
      { key: 'name', label: 'Policy' }, { key: 'salaryStructureId', label: 'Salary template', value: row => templateName(row.salaryStructureId) },
      { key: 'effectiveFrom', label: 'Effective from', value: row => day(row.effectiveFrom) },
      { key: 'status', label: 'Status', render: row => <Tag color={row.status === 'Published' ? 'green' : 'orange'}>{row.status}</Tag> },
      { key: 'baseComponentCode', label: 'Wage base' }, { key: 'ceilingBasis', label: 'Ceiling basis', value: row => basisName(row.ceilingBasis) },
      { key: 'contributions', label: 'Contributions', value: row => row.contributions.map(rule => `${contributionName(rule)}: ${rule.ratePercent}%, ${ceilingName(rule.monthlyWageCeiling)}`).join('; ') },
      { key: 'reason', label: 'Change reason' }, { key: 'publishedBy', label: 'Published by' }, { key: 'publishedAtUtc', label: 'Published at', value: row => dateTime(row.publishedAtUtc) },
    ]} actions={row => <Space wrap><Button size="small" onClick={() => setDetail(row)}>View</Button>{canManage && <Button size="small" onClick={() => beginVersion(row)}>{row.status === 'Draft' ? 'Retry publish' : 'New from this'}</Button>}</Space>} />
    <Drawer title={draft.id ? 'Publish PF policy draft' : 'Publish new PF policy version'} open={open} width="min(820px, 96vw)" onClose={() => { if (!saving) setOpen(false) }} closable={!saving} maskClosable={!saving} footer={<Space style={{ display: 'flex', justifyContent: 'flex-end' }}><Button disabled={saving} onClick={() => setOpen(false)}>Cancel</Button><Button type="primary" loading={saving} disabled={!canManage} onClick={() => void publish()}>{draft.id ? 'Retry publish' : 'Publish new version'}</Button></Space>}>
      <Form component="div" layout="vertical" disabled={saving} className="pf-policy-editor">
        {saveError && <Alert type="error" showIcon message={saveError} style={{ marginBottom: 16 }} />}
        <Row gutter={16}><Col xs={24} md={12}><Form.Item label="Salary template" required><Select aria-label="Salary template" value={draft.salaryStructureId || undefined} placeholder="Select this client's template" options={clientTemplates.map(row => ({ value: String(row.id), label: row.name }))} disabled={!!draft.id || saving} onChange={salaryStructureId => setDraft(current => ({ ...current, salaryStructureId, baseComponentCode: '', contributions: [] }))} /></Form.Item></Col><Col xs={24} md={12}><Form.Item label="Policy name" required><Input aria-label="PF policy name" value={draft.name} maxLength={120} onChange={event => setDraft(current => ({ ...current, name: event.target.value }))} /></Form.Item></Col></Row>
        <Row gutter={16}><Col xs={24} md={12}><Form.Item label="Effective from" required><Input aria-label="PF effective from" type="date" value={draft.effectiveFrom} onChange={event => setDraft(current => ({ ...current, effectiveFrom: event.target.value }))} /></Form.Item></Col><Col xs={24} md={12}><Form.Item label="PF wage base" required><Select aria-label="PF wage base" value={draft.baseComponentCode || undefined} placeholder="Select earning component" options={baseComponents.map(row => ({ value: row.code, label: `${row.code} · ${row.name}` }))} onChange={baseComponentCode => setDraft(current => ({ ...current, baseComponentCode }))} /></Form.Item></Col></Row>
        <Form.Item label="Ceiling allocation within the payroll period" required><Select aria-label="PF ceiling allocation" value={draft.ceilingBasis || undefined} placeholder="Choose ceiling treatment" options={[{ value: 'CalendarDays', label: 'Prorate ceiling by calendar days in each version period' }, { value: 'PayableDays', label: 'Prorate ceiling by payable days in each version period' }]} onChange={ceilingBasis => setDraft(current => ({ ...current, ceilingBasis }))} /></Form.Item>
        <Divider orientation="left">PF contribution components</Divider>
        <p>Select the existing template components and enter their rates. EPS and ESI continue to use their template formulas.</p>
        {selectedTemplate && !pfComponents.some(row => row.statutoryType === 'PF Employee') && <Alert type="warning" showIcon message="This template needs a component classified as PF Employee before a policy can be published." />}
        {draft.contributions.map((rule, index) => <div className="pf-contribution-editor" key={index}>
          <Form.Item label={`Contribution ${index + 1}`} required><Select aria-label={`Contribution ${index + 1} component`} value={rule.componentId || undefined} placeholder="Select PF Employee or PF Employer" options={pfComponents.map(row => ({ value: String(row.id), label: `${row.code} · ${row.name} (${row.statutoryType})`, disabled: draft.contributions.some((item, i) => i !== index && item.componentId === String(row.id)) }))} onChange={componentId => patchContribution(index, { componentId })} /></Form.Item>
          <Row gutter={12}><Col xs={24} md={8}><Form.Item label="Rate (%)" required><InputNumber aria-label={`Contribution ${index + 1} rate`} min={0} max={100} precision={4} value={rule.ratePercent} onChange={ratePercent => patchContribution(index, { ratePercent })} style={{ width: '100%' }} /></Form.Item></Col><Col xs={24} md={8}><Form.Item label="Wage ceiling" required><Select aria-label={`Contribution ${index + 1} ceiling`} value={rule.ceilingMode || undefined} placeholder="Choose ceiling" options={[{ value: 'Capped', label: 'Monthly ceiling' }, { value: 'Uncapped', label: 'Uncapped' }]} onChange={ceilingMode => patchContribution(index, { ceilingMode, monthlyWageCeiling: null })} /></Form.Item></Col><Col xs={24} md={8}>{rule.ceilingMode === 'Capped' && <Form.Item label="Monthly ceiling (₹)" required><InputNumber min={0} precision={2} value={rule.monthlyWageCeiling} onChange={monthlyWageCeiling => patchContribution(index, { monthlyWageCeiling })} style={{ width: '100%' }} /></Form.Item>}</Col></Row>
          <Button danger size="small" onClick={() => setDraft(current => ({ ...current, contributions: current.contributions.filter((_, i) => i !== index) }))}>Remove contribution</Button>
        </div>)}
        <Button icon={<PlusOutlined />} disabled={!selectedTemplate || draft.contributions.length >= pfComponents.length} onClick={() => setDraft(current => ({ ...current, contributions: [...current.contributions, { componentId: '', ratePercent: null, ceilingMode: '', monthlyWageCeiling: null }] }))}>Add contribution</Button>
        <Divider />
        <Form.Item><Checkbox checked={draft.overrideEdli} onChange={event => setDraft(current => ({ ...current, overrideEdli: event.target.checked, edliMonthlyWageCeiling: null }))}>Set an EDLI wage ceiling for ECR</Checkbox></Form.Item>
        {draft.overrideEdli && <Form.Item label="EDLI monthly wage ceiling (₹)" required><InputNumber min={0} precision={2} value={draft.edliMonthlyWageCeiling} onChange={edliMonthlyWageCeiling => setDraft(current => ({ ...current, edliMonthlyWageCeiling }))} style={{ width: '100%' }} /></Form.Item>}
        <Form.Item label="Reason for this version" required><Input.TextArea aria-label="PF change reason" value={draft.reason} maxLength={1000} rows={3} onChange={event => setDraft(current => ({ ...current, reason: event.target.value }))} /></Form.Item>
        <Alert type="info" showIcon message="Publication preserves this version for its effective period. Existing payroll results are not recalculated by this action." />
      </Form>
    </Drawer>
    <Drawer title={detail ? `PF policy v${detail.versionNumber}` : 'PF policy'} open={!!detail} width="min(760px, 96vw)" onClose={() => setDetail(null)}>
      {detail && <><Descriptions bordered column={1} size="small">{[
        { key: 'name', label: 'Policy', children: detail.name }, { key: 'template', label: 'Salary template', children: templateName(detail.salaryStructureId) },
        { key: 'effective', label: 'Effective from', children: day(detail.effectiveFrom) }, { key: 'status', label: 'Status', children: detail.status },
        { key: 'base', label: 'Wage base', children: detail.baseComponentCode }, { key: 'basis', label: 'Ceiling basis', children: basisName(detail.ceilingBasis) },
        { key: 'edli', label: 'EDLI ceiling', children: detail.edliMonthlyWageCeiling === null ? 'Existing EDLI rule' : ceilingName(detail.edliMonthlyWageCeiling) },
        { key: 'reason', label: 'Reason', children: detail.reason }, { key: 'publisher', label: 'Published by', children: detail.publishedBy || '—' },
        { key: 'published', label: 'Published at', children: dateTime(detail.publishedAtUtc) },
      ].map(item => <Descriptions.Item key={item.key} label={item.label}>{item.children}</Descriptions.Item>)}</Descriptions><Divider /><DataTable rows={detail.contributions} getRowId={row => row.componentId} exportFileName={`pf-policy-v${detail.versionNumber}-components`} columns={[{ key: 'componentId', label: 'Component', value: contributionName }, { key: 'ratePercent', label: 'Rate (%)' }, { key: 'monthlyWageCeiling', label: 'Monthly ceiling', value: row => ceilingName(row.monthlyWageCeiling) }]} /></>}
    </Drawer>
  </section>
}

