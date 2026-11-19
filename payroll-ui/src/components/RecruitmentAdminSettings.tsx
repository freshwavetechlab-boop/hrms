import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Drawer, Form, Input, Modal, Popconfirm, Select, Space, Tooltip, message } from 'antd'
import { CopyOutlined, DeleteOutlined, EditOutlined, PlusOutlined } from '@ant-design/icons'
import DataTable from './DataTable'
import RecruitmentOfferClientSelect from './RecruitmentOfferClientSelect'
import { useAuthSession } from './AuthGate'
import { isSelectedClientUser } from '../utils/clientAccess'
import { getClients } from '../services/payrollService'
import { createSimilarRecruitmentTemplate, deleteRecruitmentAdminConfiguration, getRecruitmentHiringTemplates, saveRecruitmentTemplate } from '../services/settingsService'
import type { Client, RecruitmentTemplate } from '../types/payroll'
import './RecruitmentAdminExperience.css'
import RecruitmentTemplateBody from './RecruitmentTemplateBody'
import { documentLayoutMarker, readDocumentLayout } from '../utils/recruitmentDocumentLayout'

const template0: RecruitmentTemplate = { id: 0, clientId: 0, clientName: '', templateType: 'Job Description', templateCode: '', templateName: '', subjectTemplate: '', bodyTemplate: '', isHtml: true, isActive: true }
type SimilarTemplateDraft = { sourceClientId: number; sourceTemplateId: number; targetClientId: number; templateName: string; templateCode: string }
const copiedTemplateName = (name: string) => `${name.slice(0, 175)} copy`
const copiedTemplateCode = (value: string) => `${value.slice(0, 75)}_COPY`
const selectionCommitteeMomTemplate = `MINUTES OF MEETING OF THE SELECTION COMMITTEE
FOR HIRING FOR THE POSITION OF #POSITION_NAME#
FOR #CLIENT_NAME#, BAND '#PAYBANDLEVEL#', AT LOCATION #LOCATION# UNDER #DIVISION# HELD ON #INTERVIEWDATE#

1. With the approval of the Competent Authority vide communication dated #APPROVALDATE#, the hiring process for the position of #POSITION_NAME# (Band #PAYBANDLEVEL#) at #LOCATION# under #DIVISION# through the empanelled Agency was initiated.

2. As per the prevailing HR Policy, the approved Assessment Panel composition for this position was applied.

3. Accordingly, the Selection Committee comprising the following members was constituted:
#PANELMEMBERSLIST#

4. The interview for the position of #POSITION_NAME# at #LOCATION# was conducted on #INTERVIEWDATE#. Out of #SHORTLISTEDCOUNT# shortlisted candidates, #PRESENTCOUNT# candidates were present:
#CANDIDATEATTENDANCETABLE#

5. The Selection Committee assessed the candidates using the configured competencies, panel feedback and overall suitability for the role.

6. Based on the overall assessment and deliberations, the Selection Committee recommended:
#CANDIDATERESULTTABLE#

Submitted.

#PANELSIGNATUREBLOCK#

ANNEXURE A

#SCOREANNEXURETABLE#`
const uidaiSelectionCommitteeMomTemplate = `MINUTES OF MEETING OF THE SELECTION COMMITTEE
FOR HIRING FOR THE POSITION OF #POSITION_NAME#
FOR AADHAAR SEVA KENDRA, BAND '#PAYBANDLEVEL#', AT LOCATION #LOCATION# UNDER #DIVISION# HELD ON #INTERVIEWDATE#

1. With the approval of the Competent Authority vide email dated #APPROVALDATE#, the hiring process for the position of #POSITION_NAME# (Band #PAYBANDLEVEL#) for Aadhaar Seva Kendra (ASK) at #LOCATION# under #DIVISION#, through the empanelled Agency was initiated.

2. As per the prevailing HR Policy vide letter No. HQ-12017/4/2020-HR-HQ (Comp. No. 2357) dated 03.02.2026 for ASK Centre Managers deployed in UIDAI through service providers, the composition of the Assessment Panel for positions at Band '#PAYBANDLEVEL#' is as under:
a) Deputy Director General of respective Regional Office or Director of respective Regional Office nominated by Deputy Director General - Chairperson
b) One Director from another Regional Office, nominated by Deputy Director General of respective Regional Office - Member
c) Subject Matter Expert (SME)/Domain Expert provided by Agency
d) Deputy Director, E&U-I, UIDAI Head Office may be co-opted as member of committee, subject to availability.

3. Accordingly, the Selection Committee comprising the following members was constituted:
#PANELMEMBERSLIST#

4. The interview for the position of #POSITION_NAME# at #LOCATION# was conducted on #INTERVIEWDATE#. Out of #SHORTLISTEDCOUNT# shortlisted candidates, #PRESENTCOUNT# candidates were present for the interview, as under:
#CANDIDATEATTENDANCETABLE#

5. The Selection Committee assessed the candidates based on their experience, qualification, problem-solving ability, communication skills, and overall suitability for the role.

6. Based on the overall assessment and deliberations, the Selection Committee has recommended the following:
#CANDIDATERESULTTABLE#

Submitted.

#PANELSIGNATUREBLOCK#

ANNEXURE A

#SCOREANNEXURETABLE#`
export default function RecruitmentHiringTemplateManager({ initialClientId = 0, onSaved }: { initialClientId?: number; onSaved?: () => void }) {
  const session = useAuthSession()
  const scopeUser = session?.originalUser || session?.user
  const boundClientId = isSelectedClientUser(scopeUser) ? 0 : Number(scopeUser?.clientId || 0)
  const selectedClientId = boundClientId || initialClientId
  const [templates, setTemplates] = useState<RecruitmentTemplate[]>([])
  const [clients, setClients] = useState<Client[]>([])
  const [loadingTemplates, setLoadingTemplates] = useState(false)
  const [loadError, setLoadError] = useState('')
  const [drawer, setDrawer] = useState('')
  const [formError, setFormError] = useState('')
  const [savingConfiguration, setSavingConfiguration] = useState(false)
  const [template, setTemplate] = useState(template0)
  const [templateHint, setTemplateHint] = useState('')
  const [similar, setSimilar] = useState<SimilarTemplateDraft | null>(null)
  const [sourceTemplates, setSourceTemplates] = useState<RecruitmentTemplate[]>([])
  const [sourceLoading, setSourceLoading] = useState(false)
  const [copying, setCopying] = useState(false)
  const [copyError, setCopyError] = useState('')
  const [sourceError, setSourceError] = useState('')
  const openedTemplate = useRef('')
  const loadRequest = useRef(0)
  const loadAbort = useRef<AbortController | null>(null)
  const sourceRequest = useRef(0)
  const sourceAbort = useRef<AbortController | null>(null)
  const load = async () => {
    const request = ++loadRequest.current
    loadAbort.current?.abort()
    const controller = new AbortController(); loadAbort.current = controller
    setLoadingTemplates(true); setLoadError('')
    const result = await getRecruitmentHiringTemplates(selectedClientId, controller.signal)
    if (request !== loadRequest.current || controller.signal.aborted) return
    setTemplates(result.ok ? result.data : []); setLoadingTemplates(false)
    if (!result.ok) setLoadError(result.error || 'Hiring templates could not be loaded.')
  }
  useEffect(() => { let active = true; void getClients(false, true).then(rows => { if (active) setClients(rows) }); return () => { active = false } }, [])
  useEffect(() => { setTemplates([]); void load(); return () => { ++loadRequest.current; loadAbort.current?.abort() } }, [selectedClientId])
  useEffect(() => () => { ++sourceRequest.current; sourceAbort.current?.abort() }, [])
  const clientOptions = clients.map(c => ({ value: c.id, label: c.name }))
  const visibleTemplates = selectedClientId ? templates.filter(row => row.clientId === selectedClientId || row.clientId === 0) : templates
  const loadSources = async (sourceClientId: number, source?: RecruitmentTemplate) => {
    const request = ++sourceRequest.current
    sourceAbort.current?.abort()
    const controller = new AbortController(); sourceAbort.current = controller
    setSourceTemplates([]); setSourceLoading(true); setSourceError(''); setCopyError('')
    setSimilar(current => current ? { ...current, sourceClientId, sourceTemplateId: 0, templateName: '', templateCode: '' } : null)
    const result = await getRecruitmentHiringTemplates(sourceClientId, controller.signal)
    if (request !== sourceRequest.current || controller.signal.aborted) return
    setSourceLoading(false)
    if (!result.ok) { setSourceError(result.error || 'Source hiring templates could not be loaded.'); return }
    setSourceTemplates(result.data)
    const selected = source && result.data.find(row => row.id === source.id)
    if (selected) setSimilar(current => current ? { ...current, sourceTemplateId: selected.id, templateName: copiedTemplateName(selected.templateName), templateCode: copiedTemplateCode(selected.templateCode) } : null)
    else if (source) setSourceError('This source template is no longer available in your permitted clients.')
  }
  const openSimilar = (source?: RecruitmentTemplate) => {
    const sourceClientId = source?.clientId || boundClientId || 0
    setSimilar({ sourceClientId, sourceTemplateId: 0, targetClientId: selectedClientId, templateName: '', templateCode: '' })
    void loadSources(sourceClientId, source)
  }
  const closeSimilar = () => {
    if (copying) return
    ++sourceRequest.current; sourceAbort.current?.abort()
    setSimilar(null); setSourceTemplates([]); setSourceError(''); setCopyError(''); setSourceLoading(false)
  }
  const copyTemplate = async () => {
    if (!similar || sourceLoading || !sourceTemplates.some(row => row.id === similar.sourceTemplateId) || !similar.targetClientId || !similar.templateName.trim() || !similar.templateCode.trim()) return
    setCopying(true); setCopyError('')
    try {
      const result = await createSimilarRecruitmentTemplate(similar.sourceTemplateId, { targetClientId: similar.targetClientId, templateName: similar.templateName.trim(), templateCode: similar.templateCode.trim() })
      if (!result.ok || !result.data?.id) { setCopyError(result.error || 'The hiring template could not be copied.'); return }
      ++sourceRequest.current; sourceAbort.current?.abort()
      setSimilar(null); setSourceTemplates([])
      setTemplate(result.data); setFormError(''); setTemplateHint('This is an independent copy. Review the client-specific content, then save your changes. Existing pipeline and offer mappings are unchanged.'); setDrawer('template')
      await load(); onSaved?.()
    } finally { setCopying(false) }
  }
  useEffect(() => {
    const query = new URLSearchParams(window.location.search)
    const id = Number(query.get('templateId') || 0)
    const createMom = query.get('newTemplate') === 'MoM'
    const key = `${initialClientId}:${id}:${createMom}`
    if (openedTemplate.current === key || (!id && !createMom)) return
    if (id) {
      const row = templates.find(item => item.id === id && (!selectedClientId || item.clientId === selectedClientId || item.clientId === 0))
      if (!row) return
      setTemplate(row.clientId === 0 && initialClientId ? { ...row, id: 0, clientId: initialClientId, templateCode: `${row.templateCode}_${initialClientId}` } : row)
      setTemplateHint(row.clientId === 0 ? 'Save a client template, then assign it to the MoM requirement in Pipeline design. Prepared and signed MoM versions retain their saved content.' : 'Changes apply when preparing a new MoM version. Prepared and signed versions retain their saved content.')
    } else {
      setTemplate({ ...template0, clientId: initialClientId, templateType: 'MoM', templateCode: 'JOB_MOM', templateName: 'Job selection MoM', isHtml: false, subjectTemplate: 'Minutes of Meeting — {{positionName}}', bodyTemplate: selectionCommitteeMomTemplate })
      setTemplateHint('This job uses the standard MoM. Save your custom template, then select it in the MoM document requirement under Pipeline design. Existing MoM versions retain their content.')
    }
    openedTemplate.current = key
    setDrawer('template')
  }, [initialClientId, selectedClientId, templates])
  useEffect(() => { setFormError('') }, [drawer])
  const save = async () => {
    const error = validateTemplate(template)
    if (error) { setFormError(error); message.warning(error); return }
    setFormError('')
    setSavingConfiguration(true)
    try {
      const response = await saveRecruitmentTemplate(template)
      if (response.ok) { setDrawer(''); setTemplateHint(''); await load(); onSaved?.() }
      else setFormError(response.error || 'The configuration could not be saved. Review the highlighted details and try again.')
    } finally { setSavingConfiguration(false) }
  }
  const remove = async (kind: string, id: number) => {
    const response = await deleteRecruitmentAdminConfiguration(kind, id)
    if (response.ok) { await load(); onSaved?.() }
  }
  return <section className="recruitment-admin" data-testid="hiring-template-manager">
    <div className="recruitment-admin-content">
      {loadError && <Alert showIcon type="error" message="Hiring templates could not be loaded" description={loadError} action={<Button onClick={() => void load()}>Retry</Button>} />}
      <GridTable kind="templates" remove={remove} title="Hiring templates" text="Reusable stage automation, offer, interview and process-document templates for this client." action="Add template" rows={visibleTemplates} loading={loadingTemplates} copy={openSimilar} copying={copying} add={() => { setTemplate({ ...template0, clientId: selectedClientId }); setTemplateHint(''); setDrawer('template') }} edit={row => { setTemplate(row.clientId === 0 && selectedClientId ? { ...row, id: 0, clientId: selectedClientId, templateCode: `${row.templateCode}_${selectedClientId}` } : row); setTemplateHint(''); setDrawer('template') }} columns={[{ key: 'clientName', label: 'Client', value: (row: RecruitmentTemplate) => row.clientId ? row.clientName || clients.find(client => client.id === row.clientId)?.name || '' : 'Reusable across permitted clients' }, { key: 'templateType', label: 'Type' }, { key: 'templateCode', label: 'Code' }, { key: 'templateName', label: 'Template' }, { key: 'isActive', label: 'Status', render: (r: any) => r.isActive ? 'Active' : 'Inactive' }]} />
    </div>
    <Drawer className="settings-master-drawer recruitment-admin-drawer" title={drawerTitle(drawer)} open={!!drawer} width={840} onClose={() => { if (!savingConfiguration) { setDrawer(''); setTemplateHint('') } }} destroyOnClose
      footer={<div className="recruitment-admin-drawer-footer"><Button disabled={savingConfiguration} onClick={() => setDrawer('')}>Cancel</Button><Button type="primary" loading={savingConfiguration} onClick={() => void save()}>{drawerSubmitText(drawer)}</Button></div>}>
      <Form component="div" layout="vertical" className="settings-quick-form recruitment-admin-form">
        {templateHint && <Alert showIcon type="info" message={templateHint} />}
        {formError && <Alert className="recruitment-form-error" showIcon closable type="error" message="Complete the required details" description={formError} onClose={() => setFormError('')} />}
        {drawer === 'template' && <><CommonClient value={template.clientId} set={clientId => setTemplate({ ...template, clientId })} options={clientOptions} disabled={Boolean(boundClientId) || template.id > 0} /><Form.Item label="Template type"><Select value={template.templateType} onChange={v => setTemplate({ ...template, templateType: v })} options={['Job Description', 'Interview Feedback', 'Offer Letter', 'Process Document', 'MoM', 'Score Annexure', 'HR Proposal', 'Joining Intimation', 'Candidate Pack', 'Rejection Letter', 'Consultant Email', 'Interview Invitation', 'Reminder', 'Offer Approval', 'Joining Instructions'].map(v => ({ value: v, label: v }))} /></Form.Item><Form.Item label="Code"><Input value={template.templateCode} onChange={e => setTemplate({ ...template, templateCode: e.target.value.toUpperCase().replace(/\s+/g, '_') })} /></Form.Item><Form.Item label="Name"><Input value={template.templateName} onChange={e => setTemplate({ ...template, templateName: e.target.value })} /></Form.Item><Form.Item label="Subject"><Input value={template.subjectTemplate} onChange={e => setTemplate({ ...template, subjectTemplate: e.target.value })} /></Form.Item><RecruitmentTemplateBody template={template} onChange={setTemplate} onError={setFormError} /></>}
        {drawer === 'template' && template.templateType === 'MoM' && <Alert showIcon type="info" message="Selection committee MoM" description="Use the normalized shortlist, panel roles, interview result and score data; candidate rows are never stored in the template." action={<Space><Button size="small" onClick={() => setTemplate({ ...template, bodyTemplate: selectionCommitteeMomTemplate })}>Insert generic layout</Button><Button size="small" type="primary" onClick={() => setTemplate({ ...template, bodyTemplate: uidaiSelectionCommitteeMomTemplate })}>Insert UIDAI layout</Button></Space>} />}
      </Form>
    </Drawer>
    <Modal className="hiring-template-copy-modal" title="Create similar hiring template" open={Boolean(similar)} onCancel={closeSimilar} width={640} destroyOnClose footer={<Space><Button disabled={copying} onClick={closeSimilar}>Cancel</Button><Button type="primary" loading={copying} data-testid="hiring-template-copy-submit" disabled={!similar || sourceLoading || !sourceTemplates.some(row => row.id === similar.sourceTemplateId) || !similar.targetClientId || !similar.templateName.trim() || !similar.templateCode.trim()} onClick={() => void copyTemplate()}>Create copy</Button></Space>}>
      {similar && <Form component="div" layout="vertical" className="hiring-template-copy-form">
        <p>Create an independent template from an existing client or a reusable template.</p>
        <div className="hiring-template-copy-source">
          <Form.Item label="Source client"><RecruitmentOfferClientSelect includeSetup aria-label="Hiring template source client" data-testid="hiring-template-copy-source-client" disabled={copying || Boolean(boundClientId)} allowClear={!boundClientId} loading={!clients.length} value={similar.sourceClientId || undefined} placeholder="All permitted source clients and reusable templates" options={clientOptions} onChange={value => void loadSources(Number(value || 0))} /></Form.Item>
          <Form.Item label="Source hiring template" required><Select aria-label="Source hiring template" data-testid="hiring-template-copy-source-template" showSearch optionFilterProp="label" allowClear loading={sourceLoading} disabled={copying || sourceLoading} value={similar.sourceTemplateId || undefined} placeholder={sourceLoading ? 'Loading source templates...' : 'Choose a saved hiring template'} notFoundContent={sourceLoading ? 'Loading source templates...' : 'No saved templates in this client scope.'} options={sourceTemplates.map(row => ({ value: row.id, label: `${row.templateName} · ${row.templateType} · ${row.clientId ? row.clientName || clients.find(client => client.id === row.clientId)?.name || 'Client template' : 'Reusable template'}` }))} onChange={id => { const source = sourceTemplates.find(row => row.id === id); setCopyError(''); setSimilar({ ...similar, sourceTemplateId: source?.id || 0, templateName: source ? copiedTemplateName(source.templateName) : '', templateCode: source ? copiedTemplateCode(source.templateCode) : '' }) }} /></Form.Item>
          {sourceError && <Alert showIcon type="error" message={sourceError} action={<Button size="small" onClick={() => void loadSources(similar.sourceClientId)}>Retry</Button>} />}
        </div>
        <Form.Item label="Target client" required><RecruitmentOfferClientSelect includeSetup aria-label="Hiring template target client" data-testid="hiring-template-copy-target-client" disabled={copying || Boolean(boundClientId)} allowClear={!boundClientId} loading={!clients.length} value={similar.targetClientId || undefined} placeholder="Choose the client receiving the copy" options={clientOptions} onChange={value => { setCopyError(''); setSimilar({ ...similar, targetClientId: Number(value || 0) }) }} /></Form.Item>
        <div className="hiring-template-copy-identity"><Form.Item label="New template name" required><Input aria-label="Copied hiring template name" maxLength={180} disabled={copying || !similar.sourceTemplateId} value={similar.templateName} onChange={event => { setCopyError(''); setSimilar({ ...similar, templateName: event.target.value }) }} /></Form.Item><Form.Item label="New template code" required><Input aria-label="Copied hiring template code" maxLength={80} disabled={copying || !similar.sourceTemplateId} value={similar.templateCode} onChange={event => { setCopyError(''); setSimilar({ ...similar, templateCode: event.target.value.toUpperCase().replace(/\s+/g, '_') }) }} /></Form.Item></div>
        {copyError && <Alert showIcon type="error" message="The template could not be copied" description={copyError} />}
      </Form>}
    </Modal>
  </section>
}

function GridTable<T extends { id: number }>(p: { kind: string; title: string; text: string; action: string; rows: T[]; columns: any[]; loading?: boolean; copying?: boolean; copy: (row?: T) => void; add: () => void; edit: (row: T) => void; remove: (kind: string, id: number) => Promise<void> }) {
  return <div className="hrms-list-surface" aria-label={p.title} data-surface-description={p.text}>
    <DataTable rows={p.rows} columns={p.columns} actionsWidth={132} hideInactiveClear loading={p.loading}
      primaryAction={<Space wrap><Button data-testid="hiring-template-create-similar-top" icon={<CopyOutlined />} disabled={p.copying} onClick={() => p.copy()}>Create similar</Button><Button className="recruitment-list-primary-action" type="primary" icon={<PlusOutlined />} onClick={p.add}>{p.action}</Button></Space>}
      actions={row => <Space size={2}>
      <Tooltip title="Create similar"><Button size="small" type="text" aria-label={`Create similar ${p.title}`} icon={<CopyOutlined />} onClick={() => p.copy(row)} /></Tooltip>
      <Tooltip title="Edit"><Button size="small" type="text" aria-label={`Edit ${p.title}`} icon={<EditOutlined />} onClick={() => p.edit(row)} /></Tooltip>
      <Popconfirm title="Delete this configuration?" description="Referenced or transactional setup is protected by the server." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => p.remove(p.kind, row.id)}>
        <Tooltip title="Delete"><Button size="small" type="text" danger aria-label={`Delete ${p.title}`} icon={<DeleteOutlined />} /></Tooltip>
      </Popconfirm>
    </Space>} />
  </div>
}
function CommonClient(p: { value: number; set: (value: number) => void; options: { value: number; label: string }[]; disabled?: boolean }) {
  return <Form.Item label="Client" required><RecruitmentOfferClientSelect includeSetup aria-label="Hiring template client" disabled={p.disabled} value={p.value || undefined} onChange={v => p.set(Number(v || 0))} options={p.options} placeholder="Select client" /></Form.Item>
}
function validateTemplate(template: RecruitmentTemplate) {
  if (!template.clientId) return 'Select the client for this template.'
  if (!template.templateCode.trim() || !template.templateName.trim()) return 'Enter both template code and template name.'
  if (!template.bodyTemplate.trim()) return 'Enter the template content.'
  if (template.bodyTemplate.trimStart().startsWith(documentLayoutMarker)) {
    try { readDocumentLayout(template.bodyTemplate) } catch (e) { return e instanceof Error ? e.message : 'Review the document layout JSON.' }
  }
  return ''
}
function drawerTitle(drawer: string) {
  return <div className="settings-drawer-title"><span>Pipeline automation</span><h3>{drawer === 'template' ? 'Hiring template' : ''}</h3><p>Reusable hiring communication and document content.</p></div>
}
function drawerSubmitText(drawer: string) { return drawer === 'template' ? 'Save template' : 'Save configuration' }




