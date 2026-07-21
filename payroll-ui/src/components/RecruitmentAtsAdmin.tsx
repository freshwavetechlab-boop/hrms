import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Card, Checkbox, Col, Form, Input, InputNumber, List, Modal, Progress, Row, Select, Space, Switch, Tabs, Tag, Tooltip, Typography } from 'antd'
import { CheckCircleOutlined, EditOutlined, PlusOutlined, QuestionCircleOutlined } from '@ant-design/icons'
import DataTable from './DataTable'
import RecruitmentMasterSelect from './RecruitmentMasterSelect'
import SearchSelect, { selectOptions } from './SearchSelect'
import { getAtsCriterionCatalog, getAtsProfiles, getRecruitmentSkills, saveAtsProfile, saveRecruitmentSkill } from '../services/recruitmentTalentService'
import type { Client, Drop, RecruitmentAtsScoringCriterion, RecruitmentAtsScoringProfile, RecruitmentSkill } from '../types/payroll'

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
  id: 0, clientId: 0, clientName: '', profileName: 'Default ATS profile', positionCategory: '', scoringMethod: 'RuleBased',
  minimumShortlistScore: 60, autoScoreOnResumeUpload: true, allowManualOverride: true,
  parserProvider: 'BuiltIn', scoringProvider: 'BuiltIn', modelName: 'Deterministic-v1',
  versionNumber: 1, isDefault: true, isActive: true, criteria: criterionDefinitions.map(row => ({ ...row }))
}
const skill0: RecruitmentSkill = { id: 0, clientId: 0, clientName: '', skillCode: '', skillName: '', category: '', aliases: [], isActive: true }

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
  const [saving, setSaving] = useState(false)
  const load = async () => {
    const [profileRows, skillRows, catalogRows] = await Promise.all([getAtsProfiles(), getRecruitmentSkills(), getAtsCriterionCatalog()])
    setProfiles(profileRows)
    setSkills(skillRows)
    if (catalogRows.length) setCriterionCatalog(catalogRows)
  }
  useEffect(() => { void load() }, [])
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
  return <>
    <Tabs items={[
      {
        key: 'profiles', label: 'Scoring profiles', children:
          <Card size="small" title="Explainable ATS scoring" extra={<Button type="primary" icon={<PlusOutlined />} onClick={() => setProfile(editProfile({ ...profile0, criteria: criterionCatalog.map(row => ({ ...row })) }, criterionCatalog))}>Add profile</Button>}>
            <Alert type="info" showIcon message="Explainable decision support" description="Every score keeps a versioned position snapshot, criterion breakdown and evidence. Human confirmation can be enforced separately on each ATS pipeline stage before progression or rejection." style={{ marginBottom: 16 }} />
            <DataTable rows={profiles} actions={row => <Button size="small" icon={<EditOutlined />} onClick={() => setProfile(editProfile(row, criterionCatalog))}>Edit</Button>} columns={[
              { key: 'clientName', label: 'Client' },
              { key: 'profileName', label: 'Profile' },
              { key: 'positionCategory', label: 'Position category', render: row => row.positionCategory || 'All categories' },
              { key: 'minimumShortlistScore', label: 'Shortlist score', render: row => <Tag color="blue">{row.minimumShortlistScore}%</Tag> },
              { key: 'criteria', label: 'Criteria', render: row => `${row.criteria?.filter(item => item.isActive).length ?? 0} active` },
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
            <DataTable rows={skills} actions={row => <Button size="small" icon={<EditOutlined />} onClick={() => setSkill(row)}>Edit</Button>} columns={[
              { key: 'clientName', label: 'Scope' }, { key: 'skillCode', label: 'Code' }, { key: 'skillName', label: 'Skill' },
              { key: 'category', label: 'Category' }, { key: 'aliases', label: 'Aliases', render: row => row.aliases?.join(', ') || '-' },
              { key: 'isActive', label: 'Status', render: row => <Tag color={row.isActive ? 'green' : 'default'}>{row.isActive ? 'Active' : 'Inactive'}</Tag> }
            ]} />
          </Card>
      }
    ]} />

    <Modal open={!!profile} title={profile?.id ? `Edit ATS profile - v${profile.versionNumber}` : 'Create ATS scoring profile'} onCancel={() => setProfile(null)} onOk={() => void saveProfile()} okText="Save profile" confirmLoading={saving} okButtonProps={{ disabled: Boolean(profileError) }} width={980} destroyOnClose>
      {profile && <Form layout="vertical">
        <Row gutter={16}>
          <Col xs={24} md={8}><Form.Item label="Client" required extra={profile.id > 0 ? 'Client ownership is fixed after creation. Create a new profile for another client.' : undefined}>{profile.id > 0 ? <Input disabled value={clients.find(row => row.id === profile.clientId)?.name || profile.clientName || `Client #${profile.clientId}`} /> : <SearchSelect value={profile.clientId} onChange={value => setProfile({ ...profile, clientId: Number(value) })} options={clientOptions} />}</Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="Profile name" required><Input value={profile.profileName} onChange={event => setProfile({ ...profile, profileName: event.target.value })} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="Position category"><RecruitmentMasterSelect masterType="Position Category" clientId={profile.clientId} clientName={clients.find(row => row.id === profile.clientId)?.name} value={profile.positionCategory} values={positionCategories(profile.clientId)} dropdowns={dropdowns} onDropdownsChange={onDropdownsChange} onChange={positionCategory => setProfile({ ...profile, positionCategory })} emptyLabel="All categories" testId="ats-position-category" /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="Minimum shortlist score"><InputNumber style={{ width: '100%' }} min={0} max={100} precision={2} addonAfter="%" value={profile.minimumShortlistScore} onChange={value => setProfile({ ...profile, minimumShortlistScore: Number(value ?? 0) })} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="Model name"><Input value={profile.modelName} onChange={event => setProfile({ ...profile, modelName: event.target.value })} /></Form.Item></Col>
          <Col xs={24}><Alert type="info" showIcon message="Built-in explainable ATS engine" description="Resume parsing and deterministic rule-based scoring are selected automatically. Configure the shortlist threshold and criterion weights below." /></Col>
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
          <Checkbox checked={profile.allowManualOverride} onChange={event => setProfile({ ...profile, allowManualOverride: event.target.checked })}>Allow reason-based manual score override</Checkbox>
          <Checkbox checked={profile.isDefault} onChange={event => setProfile({ ...profile, isDefault: event.target.checked })}>Default profile for this client</Checkbox>
          <Checkbox checked={profile.isActive} onChange={event => setProfile({ ...profile, isActive: event.target.checked })}>Active</Checkbox>
        </Space></Form.Item>
        {profileError && <Alert type="error" showIcon message={profileError} />}
      </Form>}
    </Modal>

    <Modal open={!!skill} title="Recruitment skill" onCancel={() => setSkill(null)} onOk={() => void saveSkill()} okButtonProps={{ disabled: !skill?.skillName.trim() }}>
      {skill && <Form layout="vertical">
        <Form.Item label="Client scope"><SearchSelect disabled={skill.id > 0} value={skill.clientId} onChange={value => setSkill({ ...skill, clientId: Number(value) })} options={selectOptions(clients.map(row => ({ value: row.id, label: row.name })), 'Global', 0)} /></Form.Item>
        <Form.Item label="Skill name" required><Input value={skill.skillName} onChange={event => setSkill({ ...skill, skillName: event.target.value })} /></Form.Item>
        <Form.Item label="Code"><Input value={skill.skillCode} onChange={event => setSkill({ ...skill, skillCode: event.target.value.toUpperCase() })} /></Form.Item>
        <Form.Item label="Category"><Input value={skill.category} onChange={event => setSkill({ ...skill, category: event.target.value })} /></Form.Item>
        <Form.Item label="Aliases" extra="Type an alias and press Enter. Comma-separated paste is also supported."><Select mode="tags" tokenSeparators={[',']} value={skill.aliases ?? []} onChange={(aliases: string[]) => setSkill({ ...skill, aliases: aliases.map(value => value.trim()).filter(Boolean) })} /></Form.Item>
        <Form.Item><Checkbox checked={skill.isActive} onChange={event => setSkill({ ...skill, isActive: event.target.checked })}>Active</Checkbox></Form.Item>
      </Form>}
    </Modal>
  </>
}
