import { useState } from 'react'
import { CopyOutlined, LinkOutlined, ReloadOutlined, StopOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Empty, List, Popconfirm, Space, Tag, Tooltip, Typography, message } from 'antd'
import {
  createCurrentStageCandidateAction, getRecruitmentCandidateActions, revokeRecruitmentCandidateAction,
} from '../services/recruitmentOrchestrationService'
import type { RecruitmentCandidateActionSession } from '../types/recruitmentOrchestration'

type Props = {
  applicationId: number
  candidateName: string
  stageName: string
  stageType: string
  compact?: boolean
}

export default function RecruitmentCandidateActionManager({ applicationId, candidateName, stageName, stageType, compact = true }: Props) {
  const [open, setOpen] = useState(false)
  const [rows, setRows] = useState<RecruitmentCandidateActionSession[]>([])
  const [loading, setLoading] = useState(false)
  const [creating, setCreating] = useState(false)
  const stageAction = candidateActionStage(stageName, stageType)

  const load = async () => {
    setLoading(true)
    setRows(await getRecruitmentCandidateActions(applicationId))
    setLoading(false)
  }
  const show = () => { setOpen(true); void load() }
  const create = async () => {
    setCreating(true)
    const response = await createCurrentStageCandidateAction(applicationId)
    setCreating(false)
    if (!response.ok || !response.data) return
    await copy(response.data.actionToken)
    await load()
  }
  const revoke = async (id: number) => {
    const response = await revokeRecruitmentCandidateAction(id)
    if (response.ok) { message.success('Candidate link revoked.'); await load() }
  }
  const copy = async (token: string) => {
    if (!token) return message.warning('This link token is unavailable. Generate a new link.')
    const url = `${window.location.origin}/candidate-action/${encodeURIComponent(token)}`
    try { await navigator.clipboard.writeText(url); message.success('Secure candidate link copied.') }
    catch { window.prompt('Copy secure candidate link', url) }
  }

  return <>
    <Button size={compact ? 'small' : 'middle'} icon={<LinkOutlined />} onClick={show}>Candidate link</Button>
    <Drawer rootClassName="candidate-action-drawer" title={`Candidate actions · ${candidateName}`} width="min(680px, calc(100vw - 16px))" open={open} destroyOnClose onClose={() => setOpen(false)}
      extra={<Space><Button icon={<ReloadOutlined />} loading={loading} onClick={() => void load()}>Refresh</Button><Tooltip title={stageAction.enabled ? undefined : stageAction.description}><span><Button type="primary" icon={<LinkOutlined />} disabled={!stageAction.enabled} loading={creating} onClick={() => void create()}>{stageAction.buttonLabel}</Button></span></Tooltip></Space>}>
      <Alert showIcon type={stageAction.enabled ? 'info' : 'warning'} message={stageAction.message}
        description={stageAction.description} />
      <List className="candidate-action-list" loading={loading} dataSource={rows}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No candidate action links yet." /> }}
        renderItem={row => {
          const active = row.status === 'Open' && !row.revokedAtUtc && new Date(row.expiresAtUtc).getTime() > Date.now()
          return <List.Item actions={[
            active && row.actionToken ? <Button key="copy" size="small" icon={<CopyOutlined />} onClick={() => void copy(row.actionToken)}>Copy link</Button> : null,
            active ? <Popconfirm key="revoke" title="Revoke this candidate link?" onConfirm={() => void revoke(row.id)}><Button size="small" danger icon={<StopOutlined />}>Revoke</Button></Popconfirm> : null,
          ].filter(Boolean)}>
            <List.Item.Meta title={<Space wrap><Typography.Text strong>{purposeLabel(row.purposeCode)}</Typography.Text><Tag color={active ? 'green' : row.status === 'Completed' ? 'blue' : 'default'}>{active ? 'Open' : row.status}</Tag></Space>}
              description={<Space direction="vertical" size={0}><span>{row.instructions || 'Candidate action'}</span><small>Expires {new Date(row.expiresAtUtc).toLocaleString('en-IN')} · uses {row.useCount}/{row.maximumUses}</small></Space>} />
          </List.Item>
        }} />
    </Drawer>
  </>
}

function purposeLabel(code: string) {
  return code === 'OFFER_RESPONSE' ? 'Offer response' : code === 'DOCUMENT_REQUEST' ? 'Document request' : 'Profile update'
}

function candidateActionStage(stageName: string, stageType: string) {
  const normalized = stageType.trim().toLowerCase()
  if (normalized === 'offer') return {
    enabled: true,
    buttonLabel: 'Generate offer link',
    message: `Offer response · ${stageName}`,
    description: 'Generate a secure response link after the offer has been released. The link expires automatically and can be revoked here.',
  }
  if (normalized === 'externalform') return {
    enabled: true,
    buttonLabel: 'Generate profile link',
    message: `Candidate profile form · ${stageName}`,
    description: 'Generate a secure link for the published form configured on this stage. Completed values are saved against this application.',
  }
  if (normalized === 'documents' || normalized === 'preonboarding') return {
    enabled: true,
    buttonLabel: 'Generate document link',
    message: `Candidate document request · ${stageName}`,
    description: 'Generate a secure link for the published upload form configured on this stage. The candidate can submit the requested documents there.',
  }
  return {
    enabled: false,
    buttonLabel: 'No link required',
    message: `${stageName} is an internal stage`,
    description: 'Candidate links are only used for External form, Documents, Pre-onboarding and Offer stages. Move the candidate to one of those stages, or use Pipeline > Manage pipeline to configure the intended candidate-facing stage.',
  }
}
