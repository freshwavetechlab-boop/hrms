import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Card, Checkbox, Col, Form, Input, InputNumber, List, Popconfirm, Progress, Row, Select, Space, Switch, Tabs, Tag, Tooltip, Typography } from 'antd'
import { ApiOutlined, CheckCircleOutlined, DeleteOutlined, EditOutlined, PlusOutlined, QuestionCircleOutlined, SafetyCertificateOutlined } from '@ant-design/icons'
import DataTable from './DataTable'
import RecruitmentMasterSelect from './RecruitmentMasterSelect'
import SearchSelect, { selectOptions } from './SearchSelect'
import { deleteAtsProfile, deleteRecruitmentAiScoringSettings, deleteRecruitmentSkill, getAtsCriterionCatalog, getAtsProfiles, getRecruitmentAiScoringSettings, getRecruitmentSkills, saveAtsProfile, saveRecruitmentAiScoringSettings, saveRecruitmentSkill, testRecruitmentAiScoringSettings } from '../services/recruitmentTalentService'
import type { Client, Drop, RecruitmentAiScoringSettings, RecruitmentAtsScoringCriterion, RecruitmentAtsScoringProfile, RecruitmentSkill } from '../types/payroll'
import RecruitmentEditorDrawer from './RecruitmentEditorDrawer'

const criterionDefinitions: RecruitmentAtsScoringCriterion[] = [
  { id: 0, scoringProfileId: 0, criterionCode: 'requiredSkills', criterionLabel: 'Required skills', evaluationType: 'SkillMatch', weight: 35, displayOrder: 10, isActive: true },
  { id: 0, scoringProfileId: 0, criterionCode: 'preferredSkills', criterionLabel: 'Preferred skills', evaluationType: 'SkillMatch', weight: 10, displayOrder: 20, isActive: true },
  { id: 0, scoringProfileId: 0, criterionCode: 'experience', criterionLabel: 'Relevant experience', evaluationType: 'ExperienceRange', weight: 20, displayOrder: 30, isActive: true },
  { id: 0, scoringProfileId: 0, criterionCode: 'qualification', criterionLabel: 'Qualification', evaluationType: 'TextMatch', weight: 10, displayOrder: 40, isActive: true },
  { id: 0, scoringProfileId: 0, criterionCode: 'certifications', criterionLabel: 'Certifications', evaluationType: 'TextMatch', weight: 5, displayOrder: 50, isActive: true },
  { id: 0, scoringProfileId: 0, criterionCode: 'roleSimilarity', criterionLabel: 'Role similarity', evaluationType: 'TokenSimilarity', weight: 10, displayOrder: 60, isActive: true },
  { id: 0, scoringProfileId: 0, criterionCode: 'location', criterionLabel: 'Location', evaluationType: 'LocationMatch', weight: 5, displayOrder: 70, isActive: true },
  { id: 0, scoringProfileId: 0, criterionCode: 'noticePeriod', criterionLabel: 'Notice period', evaluationType: 'NoticePeriod', weight: 5, displayOrder: 80, isActive: true }
]

const profile0: RecruitmentAtsScoringProfile = {
  id: 0, clientId: 0, clientName: '', profileName: 'Default ATS profile', positionCategory: '', scoringMethod: 'Hybrid',
  minimumShortlistScore: 60, autoScoreOnResumeUpload: true, allowManualOverride: true,
  parserProvider: 'BuiltIn', scoringProvider: 'BuiltInLocal', modelName: 'Deterministic-v2 + bge-micro-v2',
  enableSemanticMatching: true, semanticMinimumSimilarity: .72, enableAiScoring: false,
  versionNumber: 1, isDefault: true, isActive: true, criteria: criterionDefinitions.map(row => ({ ...row }))
}
const skill0: RecruitmentSkill = { id: 0, clientId: 0, clientName: '', skillCode: '', skillName: '', category: '', aliases: [], isActive: true }
const ai0 = (clientId = 0): RecruitmentAiScoringSettings => ({ id: 0, clientId, clientName: '', enableAiScoring: false, providerCode: 'Gemini', modelName: 'gemini-3.5-flash', aiBlendWeight: 20, minimumConfidence: .65, maximumResumeCharacters: 40000, requestTimeoutSeconds: 45, hasApiKey: false, apiKey: '', healthStatus: 'NotTested', lastHealthMessage: '', lastTestedAt: null, isActive: true })

const editProfile = (profile: RecruitmentAtsScoringProfile, catalog: RecruitmentAtsScoringCriterion[] = criterionDefinitions): RecruitmentAtsScoringProfile => {
  const existing = profile.criteria ?? []
  return {
    ...profile,
    criteria: catalog.map(definition => {
      const configured = existing.find(row => row.criterionCode === definition.criterionCode)
      return configured ? { ...configured, criterionLabel: definition.criterionLabel, evaluationType: definition.evaluationType, displayOrder: definition.displayOrder } : { ...definition, scoringProfileId: profile.id, weight: 0, isActive: false }
    })
  }
}

const evaluationDescriptions: Record<string, string> = {
  SkillMatch: 'Matches the configured job skills against normalized resume terms and aliases.',
  ExperienceRange: 'Compares candidate experience with the position experience range.',
  TextMatch: 'Finds configured qualification or certification evidence in the resume.',
  TokenSimilarity: 'Compares the role title with resume and candidate-profile terminology.',
  LocationMatch: 'Compares candidate and position locations.',
  NoticePeriod: 'Scores availability using the candidate notice period.'
}

export default function RecruitmentAtsAdmin({ clients, dropdowns, onDropdownsChange }: { clients: Client[]; dropdowns: Drop[]; onDropdownsChange: (rows: Drop[]) => void }) {
  const [profiles, setProfiles] = useState<RecruitmentAtsScoringProfile[]>([])
  const [skills, setSkills] = useState<RecruitmentSkill[]>([])
  const [criterionCatalog, setCriterionCatalog] = useState<RecruitmentAtsScoringCriterion[]>(criterionDefinitions)
  const [profile, setProfile] = useState<RecruitmentAtsScoringProfile | null>(null)
  const [skill, setSkill] = useState<RecruitmentSkill | null>(null)
  const [aiClientId, setAiClientId] = useState(0)
  const [aiSettings, setAiSettings] = useState<RecruitmentAiScoringSettings>(ai0())
  const [aiSaving, setAiSaving] = useState(false)
  const [aiTesting, setAiTesting] = useState(false)
  const [saving, setSaving] = useState(false)
  const load = async () => {
    const [profileRows, skillRows, catalogRows] = await Promise.all([getAtsProfiles(), getRecruitmentSkills(), getAtsCriterionCatalog()])
    setProfiles(profileRows)
    setSkills(skillRows)
    if (catalogRows.length) setCriterionCatalog(catalogRows)
  }
  useEffect(() => { void load() }, [])
  useEffect(() => {
    if (aiClientId || !clients.length) return
    const firstClientId = clients[0].id
    setAiClientId(firstClientId)
    void getRecruitmentAiScoringSettings(firstClientId).then(setAiSettings)
  }, [clients, aiClientId])
  const clientOptions = selectOptions(clients.map(row => ({ value: row.id, label: row.name })), 'Select client', 0)
  const positionCategories = (clientId: number) => Array.from(new Set(dropdowns.filter(row => row.isActive && row.type === 'Position Category' && (row.clientId === 0 || row.clientId === clientId)).map(row => row.value).filter(Boolean)))
  const activeWeight = useMemo(() => profile?.criteria.filter(row => row.isActive).reduce((sum, row) => sum + Number(row.weight || 0), 0) ?? 0, [profile])
  const profileError = !profile
    ? ''
    : profile.clientId <= 0
      ? 'Select a client.'
      : !profile.profileName.trim()
        ? 'Enter a profile name.'
        : !profile.criteria.some(row => row.isActive)
          ? 'Keep at least one scoring criterion active.'
          : Math.abs(activeWeight - 100) > 0.001
            ? 'Active criteria must total exactly 100%.'
            : ''

  const patchCriterion = (code: string, patch: Partial<RecruitmentAtsScoringCriterion>) => {
    if (!profile) return
    setProfile({ ...profile, criteria: profile.criteria.map(row => row.criterionCode === code ? { ...row, ...patch } : row) })
  }
  const saveProfile = async () => {
    if (!profile || profileError) return
    setSaving(true)
    try {
      const response = await saveAtsProfile(profile)
      if (response.ok) { setProfile(null); await load() }
    } finally { setSaving(false) }
  }
  const saveSkill = async () => {
    if (!skill) return
    const response = await saveRecruitmentSkill(skill)
    if (response.ok) { setSkill(null); await load() }
  }
  const selectAiClient = async (clientId: number) => {
    setAiClientId(clientId)
    setAiSettings(await getRecruitmentAiScoringSettings(clientId))
  }
  const saveAi = async () => {
    if (!aiSettings.clientId) return
    setAiSaving(true)
    try {
      const response = await saveRecruitmentAiScoringSettings(aiSettings)
      if (response.ok && response.data) setAiSettings({ ...response.data, apiKey: '' })
    } finally { setAiSaving(false) }
  }
  const testAi = async () => {
    if (!aiSettings.clientId) return
    setAiTesting(true)
    try {
      const response = await testRecruitmentAiScoringSettings(aiSettings.clientId)
      if (response.ok && response.data) setAiSettings({ ...response.data, apiKey: '' })
    } finally { setAiTesting(false) }
  }
  const removeAi = async () => {
    if (!aiSettings.clientId) return
    const response = await deleteRecruitmentAiScoringSettings(aiSettings.clientId)
    if (response.ok) setAiSettings(ai0(aiSettings.clientId))
  }
  const removeProfile = async (id: number) => {
    const response = await deleteAtsProfile(id)
    if (response.ok) await load()
  }
  const removeSkill = async (id: number) => {
    const response = await deleteRecruitmentSkill(id)
    if (response.ok) await load()
  }
  return <>
    <Tabs items={[
      {
        key: 'profiles', label: 'Scoring profiles', children:
          <Card size="small" title="Explainable ATS scoring" extra={<Button type="primary" icon={<PlusOutlined />} onClick={() => setProfile(editProfile({ ...profile0, criteria: criterionCatalog.map(row => ({ ...row })) }, criterionCatalog))}>Add profile</Button>}>
            <Alert type="info" showIcon message="Explainable decision support" description="Every score keeps a versioned position snapshot, criterion breakdown and evidence. Human confirmation can be enforced separately on each ATS pipeline stage before progression or rejection." style={{ marginBottom: 16 }} />
            <DataTable rows={profiles} actions={row => <Space size={4}><Button size="small" icon={<EditOutlined />} onClick={() => setProfile(editProfile(row, criterionCatalog))}>Edit</Button><Popconfirm title="Delete ATS profile?" description="Historical scores stay intact. Active pipeline references must be removed first." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => void removeProfile(row.id)}><Button size="small" danger icon={<DeleteOutlined />} aria-label={`Delete ${row.profileName}`} /></Popconfirm></Space>} columns={[
              { key: 'clientName', label: 'Client' },
              { key: 'profileName', label: 'Profile' },
              { key: 'positionCategory', label: 'Position category', render: row => row.positionCategory || 'All categories' },
              { key: 'minimumShortlistScore', label: 'Shortlist score', render: row => <Tag color="blue">{row.minimumShortlistScore}%</Tag> },
              { key: 'criteria', label: 'Criteria', render: row => `${row.criteria?.filter(item => item.isActive).length ?? 0} active` },
              { key: 'enableSemanticMatching', label: 'Semantic', render: row => row.enableSemanticMatching ? <Tag color="purple">Local vectors</Tag> : '-' },
              { key: 'enableAiScoring', label: 'AI', render: row => row.enableAiScoring ? <Tag color="geekblue">Enabled</Tag> : 'Off' },
              { key: 'modelName', label: 'Model' },
              { key: 'versionNumber', label: 'Version' },
              { key: 'isDefault', label: 'Default', render: row => row.isDefault ? <Tag color="green">Default</Tag> : '-' },
              { key: 'isActive', label: 'Status', render: row => <Tag color={row.isActive ? 'green' : 'default'}>{row.isActive ? 'Active' : 'Inactive'}</Tag> }
            ]} />
          </Card>
      },
      {
        key: 'skills', label: 'Skill dictionary', children:
          <Card size="small" title="Skill dictionary and aliases" extra={<Button type="primary" icon={<PlusOutlined />} onClick={() => setSkill({ ...skill0 })}>Add skill</Button>}>
            <Typography.Paragraph type="secondary">Aliases normalize different resume terms to one skill without duplicating candidate data.</Typography.Paragraph>
            <DataTable rows={skills} actions={row => <Space size={4}><Button size="small" icon={<EditOutlined />} onClick={() => setSkill(row)}>Edit</Button><Popconfirm title="Delete skill?" description="Aliases are deleted; historical resume/JD evidence retains the skill name." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => void removeSkill(row.id)}><Button size="small" danger icon={<DeleteOutlined />} aria-label={`Delete ${row.skillName}`} /></Popconfirm></Space>} columns={[
              { key: 'clientName', label: 'Scope' }, { key: 'skillCode', label: 'Code' }, { key: 'skillName', label: 'Skill' },
              { key: 'category', label: 'Category' }, { key: 'aliases', label: 'Aliases', render: row => row.aliases?.join(', ') || '-' },
              { key: 'isActive', label: 'Status', render: row => <Tag color={row.isActive ? 'green' : 'default'}>{row.isActive ? 'Active' : 'Inactive'}</Tag> }
            ]} />
          </Card>
      },
      {
        key: 'ai', label: 'AI scoring setup', children:
          <Card size="small" title={<Space><ApiOutlined />Optional Gemini analysis</Space>} extra={<Tag color={aiSettings.healthStatus === 'Healthy' ? 'green' : aiSettings.healthStatus === 'Unhealthy' ? 'red' : 'default'}>{aiSettings.healthStatus || 'Not tested'}</Tag>}>
            <Alert type="info" showIcon icon={<SafetyCertificateOutlined />} message="Bounded, encrypted and optional" description="Local semantic vector scoring works without an API key. When enabled, Gemini validates only job-relevant evidence and can contribute at most 30% of the final score. Provider failure never removes the local ATS result." style={{ marginBottom: 16 }} />
            <Form component="div" layout="vertical">
              <Row gutter={16}>
                <Col xs={24} md={8}><Form.Item label="Client" required><SearchSelect value={aiClientId} onChange={value => void selectAiClient(Number(value))} options={clientOptions} /></Form.Item></Col>
                <Col xs={24} md={8}><Form.Item label="Provider"><Input value="Gemini" disabled /></Form.Item></Col>
                <Col xs={24} md={8}><Form.Item label="Model"><Input data-testid="ai-scoring-model" value={aiSettings.modelName} onChange={event => setAiSettings({ ...aiSettings, modelName: event.target.value })} /></Form.Item></Col>
                <Col xs={24} md={12}><Form.Item label="API key" extra={aiSettings.hasApiKey ? 'An encrypted key is already saved. Leave blank to keep it.' : 'Saved with ASP.NET Data Protection; the key is never returned to the browser.'}><Input.Password data-testid="ai-scoring-api-key" autoComplete="new-password" value={aiSettings.apiKey || ''} placeholder={aiSettings.hasApiKey ? 'Configured securely' : 'Paste Gemini API key'} onChange={event => setAiSettings({ ...aiSettings, apiKey: event.target.value })} /></Form.Item></Col>
                <Col xs={24} md={4}><Form.Item label="AI contribution"><InputNumber style={{ width: '100%' }} min={0} max={30} precision={0} addonAfter="%" value={aiSettings.aiBlendWeight} onChange={value => setAiSettings({ ...aiSettings, aiBlendWeight: Number(value ?? 0) })} /></Form.Item></Col>
                <Col xs={24} md={4}><Form.Item label="Min confidence"><InputNumber style={{ width: '100%' }} min={0} max={1} step={.05} precision={2} value={aiSettings.minimumConfidence} onChange={value => setAiSettings({ ...aiSettings, minimumConfidence: Number(value ?? 0) })} /></Form.Item></Col>
                <Col xs={24} md={4}><Form.Item label="Timeout"><InputNumber style={{ width: '100%' }} min={10} max={120} addonAfter="sec" value={aiSettings.requestTimeoutSeconds} onChange={value => setAiSettings({ ...aiSettings, requestTimeoutSeconds: Number(value ?? 45) })} /></Form.Item></Col>
                <Col xs={24}><Space direction="vertical">
                  <Switch data-testid="enable-ai-scoring" checked={aiSettings.enableAiScoring} checkedChildren="AI scoring enabled" unCheckedChildren="AI scoring disabled" onChange={enableAiScoring => setAiSettings({ ...aiSettings, enableAiScoring })} />
                  <Typography.Text type="secondary">Each ATS profile must also opt in. This client switch is the master kill switch.</Typography.Text>
                </Space></Col>
              </Row>
              {aiSettings.lastHealthMessage && <Alert style={{ marginTop: 16 }} type={aiSettings.healthStatus === 'Healthy' ? 'success' : 'warning'} showIcon message={aiSettings.lastHealthMessage} />}
              <Space style={{ marginTop: 16 }}><Button type="primary" loading={aiSaving} disabled={!aiSettings.clientId || (aiSettings.enableAiScoring && !aiSettings.hasApiKey && !aiSettings.apiKey?.trim())} onClick={() => void saveAi()}>Save AI setup</Button><Button loading={aiTesting} disabled={!aiSettings.hasApiKey || Boolean(aiSettings.apiKey?.trim())} onClick={() => void testAi()}>Test saved connection</Button>{aiSettings.id > 0 && <Popconfirm title="Remove AI scoring setup?" description="The encrypted API key and client AI settings will be deleted. Local semantic scoring remains available." okText="Remove" okButtonProps={{ danger: true }} onConfirm={() => void removeAi()}><Button danger icon={<DeleteOutlined />}>Remove setup</Button></Popconfirm>}</Space>
            </Form>
          </Card>
      }
    ]} />

    <RecruitmentEditorDrawer open={!!profile} eyebrow="ATS configuration"
      title={profile?.id ? `Edit ATS profile - v${profile.versionNumber}` : 'Create ATS scoring profile'}
      description="Configure explainable scoring, shortlist thresholds and evidence weights for one client."
      onClose={() => { if (!saving) setProfile(null) }} onSubmit={() => void saveProfile()} submitText="Save profile"
      submitLoading={saving} submitDisabled={Boolean(profileError)} width="min(1040px, 96vw)" destroyOnClose>
      {profile && <Form component="div" layout="vertical">
        <Row gutter={16}>
          <Col xs={24} md={8}><Form.Item label="Client" required extra={profile.id > 0 ? 'Client ownership is fixed after creation. Create a new profile for another client.' : undefined}>{profile.id > 0 ? <Input disabled value={clients.find(row => row.id === profile.clientId)?.name || profile.clientName || `Client #${profile.clientId}`} /> : <SearchSelect value={profile.clientId} onChange={value => setProfile({ ...profile, clientId: Number(value) })} options={clientOptions} />}</Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="Profile name" required><Input value={profile.profileName} onChange={event => setProfile({ ...profile, profileName: event.target.value })} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="Position category"><RecruitmentMasterSelect masterType="Position Category" clientId={profile.clientId} clientName={clients.find(row => row.id === profile.clientId)?.name} value={profile.positionCategory} values={positionCategories(profile.clientId)} dropdowns={dropdowns} onDropdownsChange={onDropdownsChange} onChange={positionCategory => setProfile({ ...profile, positionCategory })} emptyLabel="All categories" testId="ats-position-category" /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="Minimum shortlist score"><InputNumber style={{ width: '100%' }} min={0} max={100} precision={2} addonAfter="%" value={profile.minimumShortlistScore} onChange={value => setProfile({ ...profile, minimumShortlistScore: Number(value ?? 0) })} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="Model name"><Input value={profile.modelName} onChange={event => setProfile({ ...profile, modelName: event.target.value })} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="Semantic match threshold" extra="Higher is stricter. 0.72 is the calibrated default."><InputNumber disabled={!profile.enableSemanticMatching} style={{ width: '100%' }} min={.5} max={.95} step={.01} precision={2} value={profile.semanticMinimumSimilarity} onChange={value => setProfile({ ...profile, semanticMinimumSimilarity: Number(value ?? .72) })} /></Form.Item></Col>
          <Col xs={24}><Alert type="info" showIcon message="Explainable hybrid ATS engine" description="Exact and alias evidence remains authoritative. The local BGE vector model can recover meaning-equivalent terms; optional Gemini analysis is bounded by the client configuration." /></Col>
        </Row>

        <Card size="small" title="Scoring criteria" extra={<Space><Typography.Text strong>{activeWeight.toFixed(2)}%</Typography.Text><Progress type="circle" size={38} percent={Math.min(100, Math.max(0, activeWeight))} status={Math.abs(activeWeight - 100) <= 0.001 ? 'success' : 'exception'} format={() => ''} /></Space>}>
          <List dataSource={profile.criteria.slice().sort((a, b) => a.displayOrder - b.displayOrder)} renderItem={criterion => <List.Item key={criterion.criterionCode}>
            <Row gutter={16} align="middle" style={{ width: '100%' }}>
              <Col xs={24} md={2}><Switch checked={criterion.isActive} onChange={isActive => patchCriterion(criterion.criterionCode, { isActive })} /></Col>
              <Col xs={24} md={8}>
                <Space><Typography.Text strong={criterion.isActive} type={!criterion.isActive ? 'secondary' : undefined}>{criterion.criterionLabel}</Typography.Text><Tooltip title={evaluationDescriptions[criterion.evaluationType]}><QuestionCircleOutlined /></Tooltip></Space>
                <div><Typography.Text type="secondary" style={{ fontSize: 12 }}>{criterion.criterionCode}</Typography.Text></div>
              </Col>
              <Col xs={24} md={7}><Tag>{criterion.evaluationType.replace(/([A-Z])/g, ' $1').trim()}</Tag></Col>
              <Col xs={24} md={7}><InputNumber disabled={!criterion.isActive} style={{ width: '100%' }} min={0} max={100} precision={2} addonAfter="%" value={criterion.weight} onChange={weight => patchCriterion(criterion.criterionCode, { weight: Number(weight ?? 0) })} /></Col>
            </Row>
          </List.Item>} />
          {Math.abs(activeWeight - 100) > 0.001 && <Alert type="warning" showIcon message={`Adjust active weights by ${(100 - activeWeight).toFixed(2)}% to reach 100%.`} />}
          {Math.abs(activeWeight - 100) <= 0.001 && <Alert type="success" showIcon icon={<CheckCircleOutlined />} message="Scoring weights are balanced at 100%." />}
        </Card>

        <Form.Item style={{ marginTop: 16 }}><Space direction="vertical">
          <Checkbox checked={profile.autoScoreOnResumeUpload} onChange={event => setProfile({ ...profile, autoScoreOnResumeUpload: event.target.checked })}>Parse and re-score active applications when the selected resume changes</Checkbox>
          <Checkbox checked={profile.enableSemanticMatching} onChange={event => setProfile({ ...profile, enableSemanticMatching: event.target.checked })}>Enable private local semantic vector matching (no external API)</Checkbox>
          <Checkbox checked={profile.enableAiScoring} onChange={event => setProfile({ ...profile, enableAiScoring: event.target.checked })}>Enable optional Gemini analysis for this profile</Checkbox>
          <Checkbox checked={profile.allowManualOverride} onChange={event => setProfile({ ...profile, allowManualOverride: event.target.checked })}>Allow reason-based manual score override</Checkbox>
          <Checkbox checked={profile.isDefault} onChange={event => setProfile({ ...profile, isDefault: event.target.checked })}>Default profile for this client</Checkbox>
          <Checkbox checked={profile.isActive} onChange={event => setProfile({ ...profile, isActive: event.target.checked })}>Active</Checkbox>
        </Space></Form.Item>
        {profileError && <Alert type="error" showIcon message={profileError} />}
      </Form>}
    </RecruitmentEditorDrawer>

    <RecruitmentEditorDrawer open={!!skill} eyebrow="ATS skill dictionary" title={skill?.id ? 'Edit recruitment skill' : 'Add recruitment skill'}
      description="Normalize resume terminology with a canonical skill and searchable aliases."
      onClose={() => setSkill(null)} onSubmit={() => void saveSkill()} submitText={skill?.id ? 'Save changes' : 'Add skill'}
      submitDisabled={!skill?.skillName.trim()} width="min(680px, 96vw)">
      {skill && <Form component="div" layout="vertical">
        <Form.Item label="Client scope"><SearchSelect disabled={skill.id > 0} value={skill.clientId} onChange={value => setSkill({ ...skill, clientId: Number(value) })} options={selectOptions(clients.map(row => ({ value: row.id, label: row.name })), 'Global', 0)} /></Form.Item>
        <Form.Item label="Skill name" required><Input value={skill.skillName} onChange={event => setSkill({ ...skill, skillName: event.target.value })} /></Form.Item>
        <Form.Item label="Code"><Input value={skill.skillCode} onChange={event => setSkill({ ...skill, skillCode: event.target.value.toUpperCase() })} /></Form.Item>
        <Form.Item label="Category"><Input value={skill.category} onChange={event => setSkill({ ...skill, category: event.target.value })} /></Form.Item>
        <Form.Item label="Aliases" extra="Type an alias and press Enter. Comma-separated paste is also supported."><Select mode="tags" tokenSeparators={[',']} value={skill.aliases ?? []} onChange={(aliases: string[]) => setSkill({ ...skill, aliases: aliases.map(value => value.trim()).filter(Boolean) })} /></Form.Item>
        <Form.Item><Checkbox checked={skill.isActive} onChange={event => setSkill({ ...skill, isActive: event.target.checked })}>Active</Checkbox></Form.Item>
      </Form>}
    </RecruitmentEditorDrawer>
  </>
}
