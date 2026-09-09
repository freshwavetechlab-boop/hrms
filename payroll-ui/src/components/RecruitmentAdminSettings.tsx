import { useEffect, useState } from 'react'
import { Alert, Button, Drawer, Form, Input, Popconfirm, Select, Space, Tooltip, message } from 'antd'
import { DeleteOutlined, EditOutlined, PlusOutlined } from '@ant-design/icons'
import DataTable from './DataTable'
import SearchSelect, { selectOptions } from './SearchSelect'
import { getClients } from '../services/payrollService'
import { deleteRecruitmentAdminConfiguration, getRecruitmentAdminSetup, saveRecruitmentTemplate } from '../services/settingsService'
import type { Client, RecruitmentAdminSetup, RecruitmentTemplate } from '../types/payroll'
import './RecruitmentAdminExperience.css'

const template0: RecruitmentTemplate = { id: 0, clientId: 0, clientName: '', templateType: 'Job Description', templateCode: '', templateName: '', subjectTemplate: '', bodyTemplate: '', isHtml: true, isActive: true }
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
  const [setup, setSetup] = useState<RecruitmentAdminSetup>({ settings: [], masters: [], consultants: [], vendors: [], assignmentRules: [], slaRules: [], documentChecklist: [], approvalMappings: [], templates: [] })
  const [clients, setClients] = useState<Client[]>([])
  const [drawer, setDrawer] = useState('')
  const [formError, setFormError] = useState('')
  const [savingConfiguration, setSavingConfiguration] = useState(false)
  const [template, setTemplate] = useState(template0)
  const load = async () => setSetup(await getRecruitmentAdminSetup())
  useEffect(() => { void Promise.all([getRecruitmentAdminSetup(), getClients()]).then(([a, b]) => { setSetup(a); setClients(b) }) }, [])
  const scopedClients = initialClientId ? clients.filter(client => client.id === initialClientId) : clients
  const clientOptions = selectOptions(scopedClients.map(c => ({ value: c.id, label: c.name })), 'Select client', 0)
  const visibleTemplates = initialClientId ? setup.templates.filter(row => row.clientId === initialClientId) : setup.templates
  useEffect(() => { setFormError('') }, [drawer])
  const save = async () => {
    const error = validateTemplate(template)
    if (error) { setFormError(error); message.warning(error); return }
    setFormError('')
    setSavingConfiguration(true)
    try {
      const response = await saveRecruitmentTemplate(template)
      if (response.ok) { setDrawer(''); await load(); onSaved?.() }
      else setFormError(response.error || 'The configuration could not be saved. Review the highlighted details and try again.')
    } finally { setSavingConfiguration(false) }
  }
  const remove = async (kind: string, id: number) => {
    const response = await deleteRecruitmentAdminConfiguration(kind, id)
    if (response.ok) { await load(); onSaved?.() }
  }
  return <section className="recruitment-admin" data-testid="hiring-template-manager">
    <div className="recruitment-admin-content"><GridTable kind="templates" remove={remove} title="Hiring templates" text="Reusable stage automation, offer, interview and process-document templates for this client." action="Add template" rows={visibleTemplates} add={() => { setTemplate({ ...template0, clientId: initialClientId }); setDrawer('template') }} edit={row => { setTemplate(row); setDrawer('template') }} columns={[{ key: 'clientName', label: 'Client' }, { key: 'templateType', label: 'Type' }, { key: 'templateCode', label: 'Code' }, { key: 'templateName', label: 'Template' }, { key: 'isActive', label: 'Status', render: (r: any) => r.isActive ? 'Active' : 'Inactive' }]} /></div>
    <Drawer className="settings-master-drawer recruitment-admin-drawer" title={drawerTitle(drawer)} open={!!drawer} width={840} onClose={() => !savingConfiguration && setDrawer('')} destroyOnClose
      footer={<div className="recruitment-admin-drawer-footer"><Button disabled={savingConfiguration} onClick={() => setDrawer('')}>Cancel</Button><Button type="primary" loading={savingConfiguration} onClick={() => void save()}>{drawerSubmitText(drawer)}</Button></div>}>
      <Form component="div" layout="vertical" className="settings-quick-form recruitment-admin-form">
        {formError && <Alert className="recruitment-form-error" showIcon closable type="error" message="Complete the required details" description={formError} onClose={() => setFormError('')} />}
        {drawer === 'template' && <><CommonClient value={template.clientId} set={clientId => setTemplate({ ...template, clientId })} options={clientOptions} disabled={initialClientId > 0} /><Form.Item label="Template type"><Select value={template.templateType} onChange={v => setTemplate({ ...template, templateType: v })} options={['Job Description', 'Interview Feedback', 'Offer Letter', 'Process Document', 'MoM', 'Score Annexure', 'HR Proposal', 'Joining Intimation', 'Candidate Pack', 'Rejection Letter', 'Consultant Email', 'Interview Invitation', 'Reminder', 'Offer Approval', 'Joining Instructions'].map(v => ({ value: v, label: v }))} /></Form.Item><Form.Item label="Code"><Input value={template.templateCode} onChange={e => setTemplate({ ...template, templateCode: e.target.value.toUpperCase().replace(/\s+/g, '_') })} /></Form.Item><Form.Item label="Name"><Input value={template.templateName} onChange={e => setTemplate({ ...template, templateName: e.target.value })} /></Form.Item><Form.Item label="Subject"><Input value={template.subjectTemplate} onChange={e => setTemplate({ ...template, subjectTemplate: e.target.value })} /></Form.Item><Form.Item label="Body" extra={template.templateType === 'Offer Letter' ? 'Supported placeholders: {{candidateName}}, {{candidateFirstName}}, {{positionTitle}}, {{clientName}}, {{currency}}, {{formattedCtc}}, {{joiningDate}}, {{expiryDate}}, {{offerDate}}, {{offerNumber}}, {{remarks}}.' : ['Process Document', 'MoM', 'Score Annexure', 'HR Proposal', 'Joining Intimation', 'Candidate Pack'].includes(template.templateType) ? 'Use {{name}} or #NAME#. Selection MoM also supports: approvalDate, shortlistedCount, presentCount, panelMembersList, candidateAttendanceTable, candidateResultTable, scoreAnnexureTable and panelSignatureBlock.' : undefined}><Input.TextArea rows={8} value={template.bodyTemplate} onChange={e => setTemplate({ ...template, bodyTemplate: e.target.value })} /></Form.Item></>}
        {drawer === 'template' && template.templateType === 'MoM' && <Alert showIcon type="info" message="Selection committee MoM" description="Use the normalized shortlist, panel roles, interview result and score data; candidate rows are never stored in the template." action={<Space><Button size="small" onClick={() => setTemplate({ ...template, bodyTemplate: selectionCommitteeMomTemplate })}>Insert generic layout</Button><Button size="small" type="primary" onClick={() => setTemplate({ ...template, bodyTemplate: uidaiSelectionCommitteeMomTemplate })}>Insert UIDAI layout</Button></Space>} />}
      </Form>
    </Drawer>
  </section>
}

function GridTable<T extends { id: number }>(p: { kind: string; title: string; text: string; action: string; rows: T[]; columns: any[]; add: () => void; edit: (row: T) => void; remove: (kind: string, id: number) => Promise<void> }) {
  return <div className="hrms-list-surface" aria-label={p.title} data-surface-description={p.text}>
    <DataTable rows={p.rows} columns={p.columns} actionsWidth={96} hideInactiveClear
      primaryAction={<Button className="recruitment-list-primary-action" type="primary" icon={<PlusOutlined />} onClick={p.add}>{p.action}</Button>}
      actions={row => <Space size={2}>
      <Tooltip title="Edit"><Button size="small" type="text" aria-label={`Edit ${p.title}`} icon={<EditOutlined />} onClick={() => p.edit(row)} /></Tooltip>
      <Popconfirm title="Delete this configuration?" description="Referenced or transactional setup is protected by the server." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => p.remove(p.kind, row.id)}>
        <Tooltip title="Delete"><Button size="small" type="text" danger aria-label={`Delete ${p.title}`} icon={<DeleteOutlined />} /></Tooltip>
      </Popconfirm>
    </Space>} />
  </div>
}
function CommonClient(p: { value: number; set: (value: number) => void; options: { value: string | number; label: string }[]; disabled?: boolean }) {
  return <Form.Item label="Client" required><SearchSelect disabled={p.disabled} value={p.value} onChange={v => p.set(Number(v))} options={p.options} /></Form.Item>
}
function validateTemplate(template: RecruitmentTemplate) {
  if (!template.clientId) return 'Select the client for this template.'
  if (!template.templateCode.trim() || !template.templateName.trim()) return 'Enter both template code and template name.'
  if (!template.bodyTemplate.trim()) return 'Enter the template content.'
  return ''
}
function drawerTitle(drawer: string) {
  return <div className="settings-drawer-title"><span>Pipeline automation</span><h3>{drawer === 'template' ? 'Hiring template' : ''}</h3><p>Reusable hiring communication and document content.</p></div>
}
function drawerSubmitText(drawer: string) { return drawer === 'template' ? 'Save template' : 'Save configuration' }




