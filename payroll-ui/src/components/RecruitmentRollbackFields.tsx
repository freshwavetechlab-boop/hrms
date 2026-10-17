import { Card, Form, Select, Switch } from 'antd'
import { RollbackOutlined } from '@ant-design/icons'
import type { RecruitmentPipelineStage, RecruitmentPipelineVersion } from '../types/recruitmentOrchestration'

type Rule = NonNullable<RecruitmentPipelineVersion['allRejectedRollback']>
export default function RecruitmentRollbackFields({ value = { enabled: false, targetStageCode: '' }, stages, disabled, onChange }: { value?: Rule; stages: RecruitmentPipelineStage[]; disabled: boolean; onChange: (value: Rule) => void }) {
  const positionStages = stages.filter(stage => stage.isActive && stage.cardScope === 'Position')
  const targets = positionStages.filter(stage => !stage.isTerminal && !['Rejected', 'Withdrawn'].includes(stage.stageType) && positionStages.some(other => other.displayOrder > stage.displayOrder))
  return <Card size="small" title={<><RollbackOutlined /> Automatic rollback</>} data-testid="pipeline-rollback-rules">
    <Form.Item label="Return the job when every linked candidate is unsuccessful"><Switch aria-label="Enable automatic rollback" checked={value.enabled} disabled={disabled} onChange={enabled => onChange({ ...value, enabled })} /></Form.Item>
    {value.enabled && <Form.Item label="Return to stage" required><Select aria-label="Rollback destination" disabled={disabled} value={value.targetStageCode || undefined} placeholder="Choose an earlier hiring stage" options={targets.map(stage => ({ value: stage.stageCode, label: stage.stageName }))} onChange={targetStageCode => onChange({ ...value, targetStageCode })} /></Form.Item>}
    <p style={{ margin: 0, color: '#667085', fontSize: 12 }}>Applies after all linked candidates are rejected, withdrawn, no-show or have an unsuccessful offer. A remaining active or joined candidate prevents rollback. Each return keeps its history and original SLA anchor. Save and publish to apply.</p>
  </Card>
}
