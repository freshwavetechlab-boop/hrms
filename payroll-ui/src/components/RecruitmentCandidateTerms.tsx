import { Space, Tag } from 'antd'
import { candidateTermLabels, candidateTermText, type CandidateTerm, type CandidateTerms } from '../utils/recruitmentCandidateTerms'

export function CandidateTermBadge({ candidate, field, label = false }: { candidate?: CandidateTerms; field: CandidateTerm; label?: boolean }) {
  const value = candidate?.[field]
  return <Tag color={value == null ? 'orange' : field === 'expectedCtc' ? 'purple' : field === 'noticePeriodDays' ? 'green' : 'blue'}>
    {label && `${candidateTermLabels[field]}: `}{candidateTermText(value, field)}
  </Tag>
}

export default function RecruitmentCandidateTerms({ candidate }: { candidate?: CandidateTerms }) {
  return <Space wrap size={[4, 6]} className="candidate-terms-badges" data-testid="candidate-terms-badges">
    {(['currentCtc', 'expectedCtc', 'noticePeriodDays'] as const).map(field => <CandidateTermBadge key={field} candidate={candidate} field={field} label />)}
  </Space>
}
