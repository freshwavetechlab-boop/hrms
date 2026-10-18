import { Space, Tag, Tooltip } from 'antd'
import { recruitmentEligibility, type RecruitmentEligibilityRecord } from '../utils/recruitmentEligibility'
import './RecruitmentEligibilityBadge.css'

export function RecruitmentEligibilityReasons({ row }: { row: RecruitmentEligibilityRecord }) {
  const reasons = row.eligibilityReasons?.filter(Boolean) || []
  if (!reasons.length) return null
  return <ul className="recruitment-eligibility-reasons" data-eligibility={recruitmentEligibility(row).status}>
    {reasons.map((reason, index) => <li key={`${index}-${reason}`}>{reason}</li>)}
  </ul>
}

export default function RecruitmentEligibilityBadge({ row, showScore = false, showReason = false }: {
  row: RecruitmentEligibilityRecord
  showScore?: boolean
  showReason?: boolean
}) {
  const state = recruitmentEligibility(row)
  const score = row.atsScore ?? row.totalScore
  return <div className="recruitment-eligibility" data-eligibility={state.status}>
    <Space size={4} wrap>
      <Tooltip title={!showReason ? state.reason || undefined : undefined}><Tag color={state.color} tabIndex={!showReason && state.reason ? 0 : undefined}>{state.label}</Tag></Tooltip>
      {showScore && state.showScore && score != null && <Tag color={state.provisional ? 'gold' : 'blue'}>{state.provisional ? 'Provisional ATS ' : ''}{score.toFixed(1)} / 100</Tag>}
    </Space>
    {showReason && state.reason && <details className="recruitment-eligibility-details">
      <summary>More details</summary>
      <RecruitmentEligibilityReasons row={row} />
    </details>}
  </div>
}
