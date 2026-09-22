import { Tag } from 'antd'
import './RecruitmentHiringProgress.css'

export type HiringProgress = {
  requiredCount: number
  candidateStages: { stage: string; count: number }[]
  pendingReason: string
}

export default function RecruitmentHiringProgress({ progress }: { progress?: HiringProgress | null }) {
  if (!progress) return null
  return <div className="recruitment-hiring-progress" data-testid="hiring-candidate-progress">
    <div aria-label="Candidates by stage">
      {progress.candidateStages.length ? progress.candidateStages.map(row => <Tag key={row.stage}>{row.count} · {row.stage}</Tag>) : <span>No candidates yet</span>}
    </div>
    <p>{progress.pendingReason}</p>
  </div>
}
