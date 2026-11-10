import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Drawer, Form, Input, InputNumber, Select, Space, Switch, Tag } from 'antd'
import { DownloadOutlined, ReloadOutlined, UploadOutlined } from '@ant-design/icons'
import DataTable, { type Column } from './DataTable'
import FileDropZone from './FileDropZone'
import { PageHeaderPortal } from './layout/AppPageHeader'
import { downloadRosterDrafts, editDocumentRoster, getDocumentRoster, getDocumentTemplates, getRosterSigningAsset, getRosterWorkLocations, linkRosterWorkLocation, releaseRosterLetters, removeRosterSigningAsset, uploadDocumentRoster, uploadRosterSigningAsset, type DocumentRosterRow, type DocumentTemplate, type RosterImportResult, type RosterImportSettings, type RosterPreview, type RosterSheet, type RosterSigningAsset } from '../services/recruitmentDocumentService'
import { suggestRosterMapping } from '../utils/recruitmentDocumentLayout'
import { filterRosterRows, rosterLocationName, rosterMasterLocationField, rosterPermissions, rosterReleaseReady, selectedRosterRows, signingAssetFileError } from '../utils/recruitmentRosterOperations'
import { useAuthSession } from './AuthGate'
import { useSessionPreference } from '../hooks/useRecruitmentPreferences'
import type { WorkLocation } from '../types/payroll'
import './RecruitmentDocumentRoster.css'

export default function RecruitmentDocumentRosterLauncher({ clientId, onChanged }: { clientId: number; onChanged: () => Promise<void> }) {
  const [templates, setTemplates] = useState<DocumentTemplate[]>([]), [open, setOpen] = useState(false)
  useEffect(() => {
    let active = true; setOpen(false); setTemplates([])
    if (clientId > 0) void getDocumentTemplates(clientId).then(r => { if (active && r.ok) setTemplates(r.data) })
    return () => { active = false }
  }, [clientId])
  if (!clientId || !templates.length) return null
  return <><PageHeaderPortal slot="recruitment-page-controls"><Button icon={<UploadOutlined />} onClick={() => setOpen(true)}>Employee lists &amp; offer letters</Button></PageHeaderPortal>
    {open && <RecruitmentDocumentRoster key={clientId} clientId={clientId} templates={templates} onClose={() => setOpen(false)} onChanged={onChanged} />}</>
}

function RecruitmentDocumentRoster({ clientId, templates, onClose, onChanged }: { clientId: number; templates: DocumentTemplate[]; onClose: () => void; onChanged: () => Promise<void> }) {
  const session = useAuthSession()
  const { canManageOffers, canImport, canRelease } = rosterPermissions(session?.user.permissions || [])
  const preferenceScope = `recruitment.document-roster:${session?.user.id}:${clientId}`
  const [rows, setRows] = useState<DocumentRosterRow[]>([]), [loading, setLoading] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState(''), [notice, setNotice] = useState('')
  const [group, setGroup] = useSessionPreference(`${preferenceScope}:group`, ''), [workLocationFilter, setWorkLocationFilter] = useSessionPreference(`${preferenceScope}:work-location`, 0)
  const [search, setSearch] = useSessionPreference(`${preferenceScope}:search`, '')
  const [selected, setSelected] = useSessionPreference<number[]>(`${preferenceScope}:selection`, []), [importOpen, setImportOpen] = useState(false)
  const [locations, setLocations] = useState<WorkLocation[]>([]), [locationError, setLocationError] = useState('')
  const [linkOpen, setLinkOpen] = useState(false), [linkLocationId, setLinkLocationId] = useState<number | undefined>()
  const [releaseIds, setReleaseIds] = useState<number[] | null>(null)
  const [edit, setEdit] = useState<DocumentRosterRow | null>(null), [editValues, setEditValues] = useState<Record<string, string>>({})
  const alive = useRef(true)
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  const load = async () => {
    setLoading(true)
    try {
      const [r, locationResult] = await Promise.all([getDocumentRoster(clientId), getRosterWorkLocations(clientId)])
      if (!alive.current) return
      if (!r.ok) setError(r.error || 'Unable to load employee lists.')
      else { setRows(r.data); setSelected(current => current.filter(id => r.data.some(row => row.id === id))) }
      if (locationResult.ok) { setLocations(locationResult.data); setLocationError('') }
      else { setLocations([]); setLocationError(locationResult.error || 'Unable to load permitted work locations.') }
    }
    catch (e) { if (alive.current) setError(e instanceof Error ? e.message : 'Unable to load employee lists.') }
    finally { if (alive.current) setLoading(false) }
  }
  useEffect(() => { void load() }, [clientId]) // scope is remounted when the selected client changes
  const baseVisible = useMemo(() => filterRosterRows(rows, group, workLocationFilter).filter(row => !search.trim() || [row.sourceGroup, row.candidateCode, row.candidateName, rosterLocationName(row, locations), ...Object.values(row.values)].join(' ').toLowerCase().includes(search.trim().toLowerCase())), [rows, group, workLocationFilter, search, locations])
  const [tableView, setTableView] = useState<{ source: DocumentRosterRow[]; ids: number[] } | null>(null)
  const onVisibleRowsChange = useCallback((filtered: DocumentRosterRow[]) => {
    const ids = filtered.map(row => row.id)
    setTableView(current => current?.source === baseVisible && current.ids.length === ids.length && current.ids.every((id, index) => id === ids[index]) ? current : { source: baseVisible, ids })
  }, [baseVisible])
  const visible = useMemo(() => {
    if (tableView?.source !== baseVisible) return []
    const byId = new Map(baseVisible.map(row => [row.id, row]))
    return tableView.ids.flatMap(id => { const row = byId.get(id); return row ? [row] : [] })
  }, [baseVisible, tableView])
  const ready = visible.filter(row => !row.documentIssues.length)
  const selectedRows = selectedRosterRows(visible, selected)
  const selectedReady = selectedRosterRows(visible, selected, true).map(row => row.id)
  const selectedReleaseReady = selectedRows.filter(rosterReleaseReady).map(row => row.id)
  const rosterColumns = useMemo<Column<DocumentRosterRow>[]>(() => [
    { key: 'sourceGroup', label: 'Source group', width: 210 }, { key: 'candidateCode', label: 'Profile code', width: 130 }, { key: 'candidateName', label: 'Employee', width: 220 }, { key: 'workLocation', label: 'Work location / institute', value: row => rosterLocationName(row, locations), width: 240 },
    { key: 'designation', label: 'Designation', value: row => row.values.positionTitle || 'Not supplied', width: 220 }, { key: 'joiningDate', label: 'Date of joining', value: row => row.values.joiningDate || 'Not supplied' }, { key: 'tenure', label: 'Contract end', value: row => row.values.contractEndDate || 'Not supplied' },
    { key: 'basic', label: 'Monthly basic', value: row => row.values.monthlyBasic || '' }, { key: 'gross', label: 'Monthly gross', value: row => row.values.monthlyGross || '' }, { key: 'readiness', label: 'Draft readiness', value: row => row.documentIssues.length ? row.documentIssues.join('; ') : 'Ready for draft', width: 250 },
    { key: 'released', label: 'Released offer', width: 180, value: row => row.releasedAt ? row.offerNumber || 'Released' : 'Not released' },
  ], [locations])
  const linkLocation = async () => {
    if (!canManageOffers || !linkLocationId || busy || !selectedRows.length) return
    setBusy(true); setError('')
    try {
      const r = await linkRosterWorkLocation(clientId, selectedRows, linkLocationId)
      if (!r.ok) throw new Error(r.error || 'Unable to link work location.')
      setLinkOpen(false); await load(); await onChanged(); setNotice(`${selectedRows.length} profiles linked to ${locations.find(location => location.id === linkLocationId)?.name || 'the selected work location'}.`)
    } catch (e) { setError(e instanceof Error ? e.message : 'Unable to link work location.') }
    finally { if (alive.current) setBusy(false) }
  }
  const generate = async (ids: number[]) => {
    if (busy || !ids.length) return
    setBusy(true); setError('')
    try { await downloadRosterDrafts(clientId, ids); setNotice(`${ids.length} unsigned draft letter${ids.length > 1 ? 's' : ''} generated.`) }
    catch (e) { setError(e instanceof Error ? e.message : 'Unable to generate draft letters.') }
    finally { if (alive.current) setBusy(false) }
  }
  const saveEdit = async () => {
    if (!edit || busy) return
    setBusy(true); setError('')
    try {
      const template = templates.find(item => item.id === edit.templateId)
      if (!template) throw new Error('Document template is unavailable. Refresh the employee list before editing.')
      const r = await editDocumentRoster(clientId, edit, editValues, template.fields)
      if (!r.ok) throw new Error(r.error)
      setEdit(null); await load(); setNotice('Document inputs saved. Candidate identity and Employee Master are unchanged.')
    }
    catch (e) { setError(e instanceof Error ? e.message : 'Unable to save document details.') }
    finally { if (alive.current) setBusy(false) }
  }
  return <><Drawer open title="Employee lists & offer letters" className="recruitment-document-roster" width="min(1380px, 98vw)" onClose={() => { if (!busy && !releaseIds) onClose() }} closable={!busy && !releaseIds} maskClosable={!busy && !releaseIds} footer={<Button disabled={busy || !!releaseIds} onClick={onClose}>Close</Button>}>
    <div className="document-roster-toolbar"><Space wrap><Select aria-label="Employee list group" style={{ minWidth: 220, maxWidth: 360 }} value={group} options={[{ value: '', label: 'All source groups' }, ...[...new Set(rows.map(r => r.sourceGroup))].map(value => ({ value, label: value }))]} onChange={setGroup} />
      <Select aria-label="Roster work location filter" showSearch optionFilterProp="label" className="document-roster-location-select" value={workLocationFilter} options={[{ value: 0, label: 'All permitted work locations' }, { value: -1, label: 'Not linked to a work location' }, ...locations.map(location => ({ value: location.id, label: location.name }))]} onChange={setWorkLocationFilter} />
      <Input.Search aria-label="Search employee list" placeholder="Search employee list" allowClear value={search} onChange={event => setSearch(event.target.value)} className="document-roster-search" />
      <Tag>{visible.length} profiles</Tag><Tag color="green">{ready.length} draft ready</Tag><Tag color="orange">{visible.length - ready.length} need details</Tag></Space>
      <Space wrap><Button icon={<ReloadOutlined />} disabled={busy} onClick={() => void load()}>Refresh</Button>{canImport && <Button type="primary" icon={<UploadOutlined />} disabled={busy} onClick={() => setImportOpen(true)}>Import employee list</Button>}<Button disabled={busy || !visible.length} onClick={() => setSelected(visible.slice(0, 200).map(row => row.id))}>Select all ({visible.length > 200 ? 'first 200' : visible.length})</Button><Button disabled={busy || !ready.length} onClick={() => setSelected(ready.slice(0, 200).map(row => row.id))}>Select ready ({ready.length > 200 ? 'first 200' : ready.length})</Button><Button disabled={busy || !selected.length} onClick={() => setSelected([])}>Clear selection</Button>
        {canManageOffers && <Button disabled={busy || !selectedRows.length || !locations.length} onClick={() => { setLinkLocationId(undefined); setLinkOpen(true) }}>Set work location ({selectedRows.length})</Button>}
        {canManageOffers && <Button disabled={busy} onClick={() => setReleaseIds([])}>Signature &amp; stamp</Button>}
        <Button icon={<DownloadOutlined />} disabled={busy || !selectedReady.length} loading={busy && !edit && !linkOpen} onClick={() => void generate(selectedReady)}>Download selected drafts ({selectedReady.length})</Button>
        {canRelease && <Button type="primary" disabled={busy || !selectedReleaseReady.length} onClick={() => setReleaseIds(selectedReleaseReady)}>Release selected ({selectedReleaseReady.length})</Button>}
      </Space></div>
    <Alert type="info" showIcon message="Review details, then download drafts or release letters" description="Draft PDFs are unsigned. Release letters together with an optional signature and stamp. Missing details remain pending." />
    {locationError && <Alert type="warning" showIcon message={locationError} />}
    {canRelease && selectedRows.some(row => !row.workLocationId) && <Alert type="warning" showIcon message="Link a work location before releasing the selected unlinked profiles." />}
    {error && <Alert closable type="error" showIcon message={error} onClose={() => setError('')} />}{notice && <Alert closable type="success" showIcon message={notice} onClose={() => setNotice('')} />}
    <div className="document-roster-table"><DataTable fillHeight hideSearch rows={baseVisible} columns={rosterColumns} onVisibleRowsChange={onVisibleRowsChange} loading={loading} getRowId={row => row.id} title="Employee list" exportFileName="employee-list-offer-letters" emptyText="No employee lists match these filters." rowSelection={{ selectedRowKeys: selectedRows.map(row => row.id), onChange: keys => setSelected(keys.map(Number)), getCheckboxProps: () => ({ disabled: busy }) }} actionsWidth={canRelease ? 265 : 190} actions={row => <Space><Button size="small" disabled={busy} onClick={() => { setEdit(row); setEditValues({ ...row.values }) }}>Details / Edit</Button><Button size="small" disabled={busy || !!row.documentIssues.length} onClick={() => void generate([row.id])}>Draft PDF</Button>{canRelease && <Button size="small" disabled={busy || !rosterReleaseReady(row)} title={!row.workLocationId ? 'Link a work location before release' : row.documentIssues.length ? 'Complete document details before release' : undefined} onClick={() => setReleaseIds([row.id])}>Release</Button>}</Space>} /></div>
  </Drawer>
  {importOpen && <RosterImportDrawer clientId={clientId} templates={templates} locations={locations} onClose={() => setImportOpen(false)} onSaved={async result => { setImportOpen(false); await load(); await onChanged(); setNotice(`${result.savedRows} list rows saved: ${result.createdProfiles} new profiles, ${result.reusedProfiles} existing profiles reused.${result.errors.length ? ' ' + result.errors.join(' ') : ''}`) }} />}
  <Drawer open={linkOpen} title="Set work location / institute" width="min(540px, 96vw)" onClose={() => { if (!busy) setLinkOpen(false) }} closable={!busy} maskClosable={!busy} footer={<Space><Button disabled={busy} onClick={() => setLinkOpen(false)}>Cancel</Button><Button type="primary" loading={busy} disabled={!linkLocationId || !selectedRows.length} onClick={() => void linkLocation()}>Link {selectedRows.length} profiles</Button></Space>}>
    {error && <Alert type="error" showIcon message={error} />}
    <p>Choose an existing work location for the selected profiles.</p><Form layout="vertical"><Form.Item label="Work location / institute" required><Select aria-label="Link roster work location" value={linkLocationId} showSearch optionFilterProp="label" placeholder="Choose a work location" disabled={busy} options={locations.map(location => ({ value: location.id, label: location.name }))} onChange={setLinkLocationId} /></Form.Item></Form><p className="document-roster-selected-summary">{selectedRows.map(row => row.candidateName).join(', ')}</p>
  </Drawer>
  {releaseIds && <RosterReleaseDrawer clientId={clientId} ids={releaseIds} canManageAsset={canManageOffers} canRelease={canRelease} onClose={() => setReleaseIds(null)} onReleased={async count => { setReleaseIds(null); await load(); await onChanged(); setNotice(`${count} offer letter${count > 1 ? 's' : ''} released and downloaded.`) }} />}
  <Drawer open={!!edit} title={edit ? `Document details · ${edit.candidateName}` : 'Document details'} width="min(850px, 96vw)" onClose={() => { if (!busy) setEdit(null) }} closable={!busy} maskClosable={!busy} footer={<Space><Button disabled={busy} onClick={() => setEdit(null)}>Cancel</Button><Button type="primary" loading={busy} disabled={!canImport && !canManageOffers} onClick={() => void saveEdit()}>Save details</Button></Space>}>
    {error && <Alert type="error" showIcon message={error} />}
    {edit && <><Alert type={edit.documentIssues.length ? 'warning' : 'success'} showIcon message={edit.documentIssues.length ? edit.documentIssues.join('; ') : 'Ready for unsigned draft'} description={`Source: ${edit.sourceFileName} · ${edit.sourceSheetName} · Row ${edit.sourceRow}`} /><Form layout="vertical" className="document-roster-fields">{templates.find(t => t.id === edit.templateId)?.fields.map(f => <Form.Item key={f.key} label={f.label} required={f.required} extra={edit.workLocationId && rosterMasterLocationField(f.key) ? 'Managed by Work Location' : f.type === 'percent' ? 'Enter 30% or 0.30 for thirty percent.' : undefined}><Input aria-label={`Document ${f.key}`} type={f.type === 'date' ? 'date' : 'text'} inputMode={['money', 'number', 'percent'].includes(f.type) ? 'decimal' : undefined} maxLength={4000} disabled={['candidateName', 'candidateEmail', 'candidatePhone', 'candidateCode'].includes(f.key) || !!edit.workLocationId && rosterMasterLocationField(f.key) || !canImport && !canManageOffers} value={editValues[f.key] || ''} onChange={e => setEditValues(v => ({ ...v, [f.key]: e.target.value }))} /></Form.Item>)}</Form></>}
  </Drawer></>
}

function RosterReleaseDrawer({ clientId, ids, canManageAsset, canRelease, onClose, onReleased }: { clientId: number; ids: number[]; canManageAsset: boolean; canRelease: boolean; onClose: () => void; onReleased: (count: number) => Promise<void> }) {
  const [asset, setAsset] = useState<RosterSigningAsset | null>(null), [applySignature, setApplySignature] = useState(false)
  const [loading, setLoading] = useState(true), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const alive = useRef(true)
  useEffect(() => {
    alive.current = true
    void getRosterSigningAsset(clientId).then(result => {
      if (!alive.current) return
      if (!result.ok) { setError(result.error || 'Unable to load the saved signature and stamp.'); return }
      setAsset(result.data); setApplySignature(result.data.available)
    }).catch(e => { if (alive.current) setError(e instanceof Error ? e.message : 'Unable to load the saved signature and stamp.') }).finally(() => { if (alive.current) setLoading(false) })
    return () => { alive.current = false }
  }, [clientId])
  const upload = async (file: File) => {
    if (busy || loading || !canManageAsset) return
    const invalid = signingAssetFileError(file)
    if (invalid) { setError(invalid); return }
    setBusy(true); setError('')
    try { const saved = await uploadRosterSigningAsset(clientId, file); if (alive.current) { setAsset(saved); setApplySignature(saved.available) } }
    catch (e) { if (alive.current) setError(e instanceof Error ? e.message : 'Unable to save the signature and stamp.') }
    finally { if (alive.current) setBusy(false) }
  }
  const remove = async () => {
    if (busy || !canManageAsset || !asset?.available) return
    setBusy(true); setError('')
    try {
      const result = await removeRosterSigningAsset(clientId)
      if (!result.ok) throw new Error(result.error || 'Unable to remove the saved image.')
      if (alive.current) { setAsset({ ...asset, available: false, fileName: '' }); setApplySignature(false) }
    } catch (e) { if (alive.current) setError(e instanceof Error ? e.message : 'Unable to remove the saved image.') }
    finally { if (alive.current) setBusy(false) }
  }
  const release = async () => {
    if (busy || loading || !canRelease || !ids.length || !asset || (applySignature && !asset.available)) return
    setBusy(true); setError('')
    try { await releaseRosterLetters(clientId, ids, applySignature); await onReleased(ids.length) }
    catch (e) { if (alive.current) setError(e instanceof Error ? e.message : 'Unable to release offer letters.') }
    finally { if (alive.current) setBusy(false) }
  }
  return <Drawer open title={ids.length ? `Release ${ids.length} offer letter${ids.length > 1 ? 's' : ''}` : 'Signature and stamp'} className="document-roster-release" width="min(640px, 96vw)" onClose={() => { if (!busy) onClose() }} closable={!busy} maskClosable={!busy} footer={<Space><Button disabled={busy} onClick={onClose}>{ids.length ? 'Cancel' : 'Close'}</Button>{canRelease && !!ids.length && <Button type="primary" icon={<DownloadOutlined />} loading={busy} disabled={loading || !asset || (applySignature && !asset.available)} onClick={() => void release()}>Release &amp; download ({ids.length})</Button>}</Space>}>
    <Alert type="info" showIcon message={ids.length ? 'Release all selected letters together' : 'Save a signature and stamp for this client'} description={ids.length ? 'Use the saved signature and stamp, or switch it off for plain letters. Draft PDFs remain available separately.' : 'Upload one combined image to use when releasing offer letters.'} />
    {error && <Alert type="error" showIcon message={error} />}
    <section className="document-roster-signature-section" aria-busy={loading || busy}>
      <h3>Signature and stamp</h3>
      <p>{canManageAsset ? 'Upload one combined PNG or JPEG image, up to 2 MB. It will be saved for this client.' : 'The saved signature and stamp belongs to this client.'}</p>
      {canManageAsset && <div className={busy || loading ? 'document-roster-upload-disabled' : undefined}><FileDropZone accept="image/png,image/jpeg,.png,.jpg,.jpeg" title="Upload signature and stamp" hint="PNG or JPEG · up to 2 MB" fileName={asset?.available ? asset.fileName : ''} onFile={file => void upload(file)} /></div>}
      {asset?.available && <Space wrap className="document-roster-asset-status"><Tag color="green">Saved for this client · {asset.fileName}</Tag>{canManageAsset && <Button size="small" disabled={busy} onClick={() => void remove()}>Remove saved image</Button>}</Space>}
      <div className="document-roster-signature-toggle"><Switch aria-label="Apply signature and stamp" checked={applySignature} loading={loading} disabled={busy || loading || !asset?.available} onChange={setApplySignature} /><span>Apply signature and stamp</span></div>
      {!loading && !asset?.available && <p>{canManageAsset ? 'No image saved. You can release plain letters or upload an image above.' : 'No image saved. An offer manager can upload one; you can release plain letters.'}</p>}
    </section>
  </Drawer>
}

function RosterImportDrawer({ clientId, templates, locations, onClose, onSaved }: { clientId: number; templates: DocumentTemplate[]; locations: WorkLocation[]; onClose: () => void; onSaved: (result: RosterImportResult) => Promise<void> }) {
  const [templateId, setTemplateId] = useState(templates[0]?.id || 0), [preset, setPreset] = useState('')
  const [file, setFile] = useState<File | null>(null), [sheets, setSheets] = useState<RosterSheet[]>([]), [preview, setPreview] = useState<RosterPreview | null>(null), [selected, setSelected] = useState<number[]>([])
  const [settings, setSettings] = useState<RosterImportSettings>({ clientId, templateId, workLocationId: null, sourceGroup: '', sheetName: '', headerRow: 4, mapping: {}, defaults: {}, sourceRows: [], previewToken: '' })
  const [busy, setBusy] = useState(false), [error, setError] = useState(''), [phase, setPhase] = useState('')
  const template = templates.find(t => t.id === templateId), sheet = sheets.find(s => s.name === settings.sheetName)
  const invalidate = (patch: Partial<RosterImportSettings>) => { setSettings(s => ({ ...s, ...patch })); setPreview(null); setSelected([]) }
  const inspect = async (upload: File, state = settings) => {
    setBusy(true); setError(''); setPreview(null); setSelected([]); setPhase('Reading worksheet columns...')
    try {
      const parsed = await uploadDocumentRoster<RosterSheet[]>('inspect', upload, { ...state, templateId })
      const chosen = parsed.find(s => s.name === state.sheetName) || parsed[0]
      setFile(upload); setSheets(parsed); setSettings(s => ({ ...s, ...state, templateId, sheetName: chosen?.name || '', mapping: suggestRosterMapping(template?.fields || [], chosen?.headers || []) }))
    } catch (e) { setError(e instanceof Error ? e.message : 'Unable to read the workbook.'); setSheets([]) }
    finally { setBusy(false); setPhase('') }
  }
  const buildPreview = async () => {
    if (!file || busy || !settings.sourceGroup.trim()) return
    setBusy(true); setError(''); setPreview(null); setPhase('Checking employee identities and document details...')
    try { const result = await uploadDocumentRoster<RosterPreview>('preview', file, { ...settings, templateId }); setPreview(result); setSelected(result.rows.filter(r => !r.importIssues.length).map(r => r.sourceRow)) }
    catch (e) { setError(e instanceof Error ? e.message : 'Unable to preview the list.') }
    finally { setBusy(false); setPhase('') }
  }
  const save = async () => {
    if (!file || !preview || busy || !selected.length) return
    setBusy(true); setError(''); setPhase(`Saving ${selected.length} reviewed profiles and source details...`)
    try { const result = await uploadDocumentRoster<RosterImportResult>('import', file, { ...settings, templateId, sourceRows: selected, previewToken: preview.previewToken }); await onSaved(result) }
    catch (e) { setError(e instanceof Error ? e.message : 'Unable to save the list. Re-previewing and retrying will check existing identities.') }
    finally { setBusy(false); setPhase('') }
  }
  return <Drawer open className="document-roster-import" title="Import employee list" width="min(1200px, 98vw)" onClose={() => { if (!busy) onClose() }} closable={!busy} maskClosable={!busy} footer={<Space><Button disabled={busy} onClick={onClose}>Cancel</Button><Button disabled={busy || !file || !sheets.length || !settings.sourceGroup.trim()} onClick={() => void buildPreview()}>Preview list</Button><Button type="primary" loading={busy} disabled={busy || !preview || !selected.length} onClick={() => void save()}>Save reviewed profiles ({selected.length})</Button></Space>}>
    <Alert type="info" showIcon message="Review source mapping once, then save" description="Email/phone identifies existing profiles. Re-upload updates the same source-group rows and retains completed details where the incoming source is blank. Monthly wages remain monthly document inputs; they are not treated as annual CTC. Missing tenure/designation/location can be completed later." />
    {error && <Alert type="error" showIcon message={error} />}{phase && <Alert type="info" showIcon message={phase} />}
    <Form layout="vertical" disabled={busy} className="document-roster-fields">
      <Form.Item label="Offer document template" required><Select aria-label="Roster document template" value={templateId} options={templates.map(t => ({ value: t.id, label: t.templateName }))} onChange={value => { setTemplateId(value); setPreset(''); setSheets([]); setFile(null); invalidate({ templateId: value, mapping: {}, defaults: {}, sourceGroup: '', sheetName: '' }) }} /></Form.Item>
      <Form.Item label="Input preset (optional)"><Select aria-label="Roster input preset" value={preset} options={[{ value: '', label: 'Choose / enter values manually' }, ...(template?.importPresets || []).map(p => ({ value: p.name, label: p.name }))]} onChange={value => { setPreset(value); const p = template?.importPresets.find(p => p.name === value); invalidate({ sourceGroup: p?.name || '', headerRow: p?.headerRow || 4, defaults: p?.defaults || {} }); setSheets([]); setFile(null) }} /></Form.Item>
      <Form.Item label="Source group" required><Input aria-label="Roster source group" maxLength={180} value={settings.sourceGroup} onChange={e => invalidate({ sourceGroup: e.target.value })} placeholder="Institute / office / source list" /></Form.Item>
      <Form.Item label="Work location / institute (optional)"><Select aria-label="Import roster work location" allowClear showSearch optionFilterProp="label" placeholder="Choose an existing work location" value={settings.workLocationId || undefined} options={locations.map(location => ({ value: location.id, label: location.name }))} onChange={value => invalidate({ workLocationId: value || null })} /></Form.Item>
      <Form.Item label="Header row"><InputNumber aria-label="Roster header row" min={1} max={100} value={settings.headerRow} onChange={v => { const next = { ...settings, headerRow: v || 1 }; invalidate({ headerRow: v || 1 }); if (file) void inspect(file, next) }} /></Form.Item>
    </Form>
    <FileDropZone accept=".xlsx" title="Upload employee workbook" hint="Choose the original employee list. The workbook is read without changing its cells or formulas." fileName={file?.name || ''} onFile={f => { if (!busy) void inspect(f) }} />
    {!!sheets.length && <><Form layout="vertical"><Form.Item label="Worksheet"><Select aria-label="Roster worksheet" disabled={busy} value={settings.sheetName} options={sheets.map(s => ({ value: s.name, label: s.name }))} onChange={value => { const s = sheets.find(s => s.name === value); invalidate({ sheetName: value, mapping: suggestRosterMapping(template?.fields || [], s?.headers || []) }) }} /></Form.Item></Form>
      <details className="document-roster-mapping"><summary>Review column mapping and default values</summary><DataTable rows={template?.fields || []} getRowId={f => f.key} hideSearch exportDisabled title="Source mapping" columns={[
        { key: 'label', label: 'Document field', width: 230, render: f => <span>{f.label}{f.required ? ' *' : ''}</span> },
        { key: 'source', label: 'Source column', render: f => <Select aria-label={`Roster column ${f.key}`} disabled={busy} style={{ width: '100%' }} value={settings.mapping[f.key] ?? -1} options={[{ value: -1, label: 'No source column / use default' }, ...(sheet?.headers || []).map((h, i) => ({ value: i, label: `${i + 1} · ${h || '(blank)'}` }))]} onChange={v => invalidate({ mapping: { ...settings.mapping, [f.key]: v } })} /> },
        { key: 'default', label: 'Default when no source column', render: f => <Input aria-label={`Roster default ${f.key}`} disabled={busy || (settings.mapping[f.key] ?? -1) >= 0} value={settings.defaults[f.key] ?? f.defaultValue ?? ''} onChange={e => invalidate({ defaults: { ...settings.defaults, [f.key]: e.target.value } })} /> },
      ]} /></details></>}
    {preview && <><Space wrap style={{ marginBlock: 12 }}><Tag color="blue">{preview.rows.length} source employees</Tag><Tag>{preview.rows.filter(r => r.candidateId).length} existing profiles</Tag><Tag color="orange">{preview.rows.filter(r => r.documentIssues.length).length} need document details</Tag></Space><DataTable rows={preview.rows} getRowId={r => r.sourceRow} exportDisabled title="Employee import preview" rowSelection={{ selectedRowKeys: selected, onChange: keys => setSelected(keys.map(Number)), getCheckboxProps: row => ({ disabled: busy || row.importIssues.length > 0 }) }} columns={[
      { key: 'sourceRow', label: 'Source row' }, { key: 'candidateName', label: 'Employee', width: 230 }, { key: 'workLocation', label: 'Work location' }, { key: 'positionTitle', label: 'Designation' }, { key: 'matchStatus', label: 'Profile match' }, { key: 'issues', label: 'Review', width: 300, value: r => r.importIssues.length ? r.importIssues.join('; ') : r.documentIssues.length ? 'Draft pending: ' + r.documentIssues.join('; ') : 'Ready for draft' },
    ]} /></>}
  </Drawer>
}
