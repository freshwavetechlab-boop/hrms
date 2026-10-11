import { Button, Collapse, Space, Table, Tag } from 'antd'
import type { RecruitmentMomReadiness, RecruitmentMomWorkOwner } from '../services/recruitmentCaseService'
import type { RecruitmentProcessDocument } from '../types/recruitmentCases'

export function RecruitmentMomVersion({ document }: { document: RecruitmentProcessDocument }) {
  const current = document.isCurrentJobMom
  return <Tag color={document.status === 'Draft' ? 'gold' : current ? 'green' : 'default'}>
    {document.status === 'Draft' ? 'Preparation pending' : current ? 'Current' : 'Previous'} · v{document.versionNumber} · {document.status}
  </Tag>
}

export const momWorkKey = (owner: RecruitmentMomWorkOwner) => `${owner.applicationId}:${owner.interviewId || 0}:${owner.userId}:${owner.task}`

export default function RecruitmentMomDetails({ readiness, job, includeCandidates = true, onPing, pinging, sentPings }: {
  readiness?: RecruitmentMomReadiness | null
  job?: { jobName: string; jobStatus: string; workOrderNumber: string }
  includeCandidates?: boolean
  onPing?: (owner: RecruitmentMomWorkOwner) => void
  pinging?: string
  sentPings?: Set<string>
}) {
  const identity = readiness?.jobName ? readiness : job
  if (!identity) return null
  const checks = readiness?.candidateChecks || []
  const panel = readiness?.panelMembers || []
  const blocked = readiness?.action === 'Terms' || readiness?.action === 'Interview'
  return <div className="recruitment-mom-details">
    <div className="recruitment-mom-identity" data-testid="mom-job-identity">
      <b>Job MoM · {identity.jobName}</b><span>{identity.workOrderNumber}</span>
      <Tag color={identity.jobStatus === 'Active' ? 'green' : 'default'}>Job: {identity.jobStatus}</Tag>
    </div>
    {readiness && <>
      <div className="recruitment-mom-panel" data-testid="mom-panel-status">
        <b>Panel signatures</b>
        <Space wrap>{panel.map(member => <Tag key={member.userId} color={member.isSigned ? 'green' : 'orange'}>{member.name} · {member.isSigned ? 'Signed' : 'Pending'}</Tag>)}</Space>
        {!panel.length && <span>No panel assigned. Assign the interview committee before preparing MoM.</span>}
      </div>
      {includeCandidates && <Collapse size="small" key={readiness.action} defaultActiveKey={blocked ? ['candidates'] : []}>
        <Collapse.Panel key="candidates" header={`Job candidate readiness · ${readiness.selectedCandidateCount} selected / ${readiness.requiredCandidateCount} required`}>
        <Table data-testid="mom-candidate-checks" size="small" rowKey="applicationId" dataSource={checks} pagination={false} scroll={{ x: 980, y: 260 }} locale={{ emptyText: 'No eligible candidates for this job. Complete final interview selection in Interview Tracker.' }} columns={[
          { title: 'Candidate', dataIndex: 'candidateName', width: 190, render: (name, row) => <span><b>{name}</b><small>Application #{row.applicationId}</small></span> },
          { title: 'Interview', dataIndex: 'interviewStatus', width: 180, render: (status, row) => <Tag color={row.interviewSelected ? 'green' : 'default'}>{status}</Tag> },
          { title: 'Agreed terms', key: 'terms', width: 115, render: (_, row) => row.interviewSelected ? <Tag color={row.termsConfirmed ? 'green' : 'orange'}>{row.termsConfirmed ? 'Confirmed' : 'Pending'}</Tag> : 'After selection' },
          { title: 'Pending interview work', key: 'feedback', width: 220, render: (_, row) => row.interviewSelected ? [row.pendingFeedbackFrom?.length ? `Feedback: ${row.pendingFeedbackFrom.join(', ')}` : '', row.scoresPending ? 'Configured scores incomplete' : ''].filter(Boolean).join(' · ') || 'Feedback complete' : 'Final selection pending' },
          { title: 'Responsible stage user', key: 'owners', width: 310, render: (_, row) => <Space direction="vertical" size={8} style={{ width: '100%' }}>
            {(row.pendingWorkOwners || []).map(owner => <div key={momWorkKey(owner)}>
              <Space wrap size={6}><b>{owner.name}</b>{onPing && readiness.canPing && <Button size="small" loading={pinging === momWorkKey(owner)} disabled={Boolean(pinging) || sentPings?.has(momWorkKey(owner))} onClick={() => onPing(owner)}>{sentPings?.has(momWorkKey(owner)) ? 'Ping sent' : 'Ping'}</Button>}</Space>
              <small>{owner.stageName} · {owner.role} · {owner.task === 'Terms' ? 'Confirm agreed terms' : owner.task}</small>
              <small style={{ overflowWrap: 'anywhere' }}>{[owner.designation, owner.email].filter(Boolean).join(' · ')}</small>
            </div>)}
            {!row.pendingWorkOwners?.length && <span>{!row.interviewSelected || !row.termsConfirmed || row.pendingFeedbackFrom?.length || row.scoresPending ? 'No active user assigned to the pending work. Check Interview Tracker / recruiter assignment.' : 'No pending work'}</span>}
          </Space> },
        ]} />
        </Collapse.Panel>
      </Collapse>}
    </>}
  </div>
}
