import { Space, Tag, Tooltip, Typography } from 'antd'
import { recruitmentEligibility, type RecruitmentEligibilityRecord } from '../utils/recruitmentEligibility'

export default function RecruitmentEligibilityBadge({ row, showScore = false, showReason = false }: {
  row: RecruitmentEligibilityRecord
  showScore?: boolean
  showReason?: boolean
}) {
  const state = recruitmentEligibility(row)
  const score = row.atsScore ?? row.totalScore
  return <span className="recruitment-eligibility" data-eligibility={state.status}>
    <Space size={4} wrap>
      <Tooltip title={state.reason || undefined}><Tag color={state.color} tabIndex={state.reason ? 0 : undefined}>{state.label}</Tag></Tooltip>
      {showScore && state.showScore && score != null && <Tag color="blue">{score.toFixed(1)} / 100</Tag>}
    </Space>
    {showReason && state.reason && <Typography.Text type={state.status === 'ineligible' ? 'danger' : 'secondary'} style={{ display: 'block', whiteSpace: 'normal', fontSize: 12 }}>{state.reason}</Typography.Text>}
  </span>
}
