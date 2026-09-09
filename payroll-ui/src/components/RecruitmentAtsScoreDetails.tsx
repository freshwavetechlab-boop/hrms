import { useMemo, useState } from 'react'
import { Button, Drawer, Empty, Progress, Select, Space, Table, Tag, Typography, type TableColumnsType } from 'antd'
import {
  AimOutlined, CheckCircleOutlined, ClockCircleOutlined, EyeOutlined, FileSearchOutlined,
  InfoCircleOutlined, SafetyCertificateOutlined, WarningOutlined,
} from '@ant-design/icons'
import type { RecruitmentApplicationScore, RecruitmentCandidateApplication } from '../types/payroll'
import './RecruitmentAtsScoreDetails.css'

type Props = {
  scores: RecruitmentApplicationScore[]
  applications: RecruitmentCandidateApplication[]
  onOverride?: (score: RecruitmentApplicationScore) => void
  display?: 'drawer' | 'inline'
}

const effectiveScore = (score: RecruitmentApplicationScore) => score.overrideScore ?? score.totalScore
const scoreColor = (score: number, threshold: number) => score >= threshold ? '#16a34a' : score >= Math.max(30, threshold * 0.65) ? '#d97706' : '#dc2626'
const statusColor = (status: string) => status === 'Matched' ? 'green' : status === 'Partial' ? 'gold' : status === 'Missing' || status === 'NotMatched' ? 'red' : 'default'
const statusClass = (status: string) => status === 'Matched' ? 'is-matched' : status === 'Partial' ? 'is-partial' : status === 'Missing' || status === 'NotMatched' ? 'is-missing' : ''
const confidencePercent = (value: number) => Math.max(0, Math.min(100, Math.round(Number(value || 0) * 100)))
const scorePercent = (value: number) => Math.max(0, Math.min(100, Math.round(Number(value || 0))))

export default function RecruitmentAtsScoreDetails({ scores, applications, onOverride, display = 'drawer' }: Props) {
  const [selected, setSelected] = useState<RecruitmentApplicationScore | null>(null)
  const applicationsById = useMemo(() => new Map(applications.map(row => [row.id, row])), [applications])
  const orderedScores = useMemo(() => scores.slice().sort((a, b) => Number(b.isCurrent) - Number(a.isCurrent) || new Date(b.scoredAt).getTime() - new Date(a.scoredAt).getTime()), [scores])
  const latest = orderedScores[0]
  const latestApplication = latest ? applicationsById.get(latest.applicationId) : undefined
  const inlineScore = orderedScores.find(row => row.id === selected?.id) || latest
  const scoreColumns: TableColumnsType<RecruitmentApplicationScore> = [
    {
      title: 'Application', key: 'application', width: 220, render: (_value, row) => {
        const application = applicationsById.get(row.applicationId)
        return <div className="ats-history-primary"><Typography.Text strong>{application?.applicationCode ?? `#${row.applicationId}`}</Typography.Text><Typography.Text type="secondary">{application?.positionTitle ?? row.positionSnapshot?.positionTitle ?? 'Position snapshot'}</Typography.Text></div>
      }
    },
    { title: 'Score', key: 'effectiveScore', width: 115, render: (_value, row) => <Tag color={effectiveScore(row) >= row.shortlistThreshold ? 'green' : 'orange'}>{effectiveScore(row).toFixed(1)} / 100</Tag> },
    { title: 'Decision support', dataIndex: 'recommendation', key: 'recommendation', width: 220, render: value => value || 'Human review required' },
    { title: 'Profile', dataIndex: 'profileVersionNumber', key: 'profileVersionNumber', width: 82, render: value => `v${value || 1}` },
    { title: 'Version', dataIndex: 'isCurrent', key: 'isCurrent', width: 88, render: value => value ? <Tag color="blue">Current</Tag> : <Tag>History</Tag> },
    { title: 'Scored', dataIndex: 'scoredAt', key: 'scoredAt', width: 150, render: value => new Date(value).toLocaleString('en-IN') },
    {
      title: 'Actions', key: 'actions', width: onOverride ? 160 : 100, render: (_value, row) => <Space size={4} wrap={false}>
        <Button size="small" icon={<EyeOutlined />} onClick={() => setSelected(row)}>Evidence</Button>
        {row.isCurrent && onOverride && <Button size="small" onClick={() => onOverride(row)}>Override</Button>}
      </Space>
    }
  ]

  if (display === 'inline') return <section className="ats-inline-evidence" data-testid="ats-inline-evidence">
    {!inlineScore ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="This application has not been scored yet. Run ATS to see its evidence." /> : <>
      <Space wrap className="ats-inline-evidence-toolbar">
        <Typography.Text strong>Assessment history</Typography.Text>
        <Select aria-label="Assessment version" value={inlineScore.id} onChange={id => setSelected(orderedScores.find(row => row.id === id) || null)} options={orderedScores.map(row => ({ value: row.id, label: `${row.isCurrent ? 'Current' : 'Previous'} · ${new Date(row.scoredAt).toLocaleString('en-IN')} · ${effectiveScore(row).toFixed(1)} / 100` }))} />
        {inlineScore.isCurrent && onOverride && <Button onClick={() => onOverride(inlineScore)}>Override</Button>}
      </Space>
      <ScoreEvidence score={inlineScore} application={applicationsById.get(inlineScore.applicationId)} />
    </>}
  </section>

  return <>
    <section className="ats-score-register" data-testid="ats-score-register">
      <header className="ats-score-register-head">
        <div className="ats-score-register-title"><span><FileSearchOutlined /></span><div><Typography.Title level={4}>ATS score and evidence</Typography.Title><Typography.Text>Versioned assessment history with traceable resume evidence.</Typography.Text></div></div>
        {latest && <Button type="primary" icon={<EyeOutlined />} onClick={() => setSelected(latest)} data-testid="open-latest-ats-report">Open latest report</Button>}
      </header>
      {!orderedScores.length
        ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No ATS score has been generated for this candidate." />
        : <>
          <div className="ats-score-latest">
            <Progress type="circle" size={72} percent={scorePercent(effectiveScore(latest))} strokeColor={scoreColor(effectiveScore(latest), latest.shortlistThreshold)} format={() => <b>{effectiveScore(latest).toFixed(1)}</b>} />
            <div className="ats-score-latest-copy">
              <span>Current assessment</span>
              <b>{latestApplication?.positionTitle ?? latest.positionSnapshot?.positionTitle ?? 'Application score'}</b>
              <small>{latestApplication?.applicationCode ?? `Application #${latest.applicationId}`} · scored {new Date(latest.scoredAt).toLocaleDateString('en-IN')}</small>
            </div>
            <div className="ats-score-latest-tags"><Tag color={effectiveScore(latest) >= latest.shortlistThreshold ? 'green' : 'orange'}>{effectiveScore(latest) >= latest.shortlistThreshold ? 'Threshold reached' : 'Recruiter review'}</Tag><Tag color="blue">Threshold {latest.shortlistThreshold.toFixed(0)}%</Tag></div>
          </div>
          <div className="ats-score-history" data-testid="ats-score-history"><Table<RecruitmentApplicationScore> size="small" pagination={orderedScores.length > 6 ? { pageSize: 6 } : false} rowKey="id" dataSource={orderedScores} columns={scoreColumns} scroll={{ x: 940 }} /></div>
        </>}
    </section>

    <Drawer className="ats-evidence-drawer" open={!!selected} onClose={() => setSelected(null)} title="ATS evidence report" width="min(1120px, 96vw)" destroyOnClose>
      {selected && <ScoreEvidence score={selected} application={applicationsById.get(selected.applicationId)} />}
    </Drawer>
  </>
}

function ScoreEvidence({ score, application }: { score: RecruitmentApplicationScore; application?: RecruitmentCandidateApplication }) {
  const value = effectiveScore(score)
  const components = score.components ?? []
  const skillMatches = score.skillMatches ?? []
  const evidence = score.evidence ?? []
  const snapshot = score.positionSnapshot
  const matchedSkills = skillMatches.filter(item => item.matchStatus === 'Matched')
  const gapSkills = skillMatches.filter(item => ['Missing', 'NotMatched', 'Partial'].includes(item.matchStatus))
  const strengths = [...new Set([
    ...matchedSkills.map(item => `${item.skillName} is supported by ${item.matchMethod || 'resume'} evidence.`),
    ...components.filter(item => Number(item.rawRatio) >= .7).map(item => `${item.criterionLabel} meets ${Math.round(Number(item.rawRatio) * 100)}% of the configured criterion.`),
  ])].slice(0, 5)
  const improvements = [...new Set([
    ...gapSkills.map(item => `${item.skillName}: ${item.matchStatus === 'Partial' ? 'partial evidence needs recruiter confirmation' : 'supporting evidence was not found'}.`),
    ...components.filter(item => Number(item.rawRatio) < .5).map(item => `${item.criterionLabel} is below half of the configured expectation.`),
  ])].slice(0, 5)
  const verdict = value >= score.shortlistThreshold ? 'Meets configured shortlist threshold' : 'Needs recruiter review'

  return <div className="ats-report" data-testid="ats-evidence-report">
    <section className="ats-report-hero">
      <div className="ats-report-identity">
        <span className="ats-report-eyebrow">Explainable ATS assessment</span>
        <Typography.Title level={2}>{application?.positionTitle ?? snapshot?.positionTitle ?? 'Application score'}</Typography.Title>
        <Typography.Text>{application?.applicationCode ?? `Application #${score.applicationId}`} · Profile v{score.profileVersionNumber || 1} · {new Date(score.scoredAt).toLocaleString('en-IN')}</Typography.Text>
        <Space wrap className="ats-report-tags">
          <Tag color={value >= score.shortlistThreshold ? 'green' : 'orange'}>{verdict}</Tag>
          <Tag color="blue">Threshold {score.shortlistThreshold.toFixed(0)}%</Tag>
          <Tag color="purple">{score.scoringMethod || 'Rule based'}</Tag>
          {score.humanReviewRequired && <Tag color="gold">Human review required</Tag>}
        </Space>
      </div>
      <div className="ats-report-score">
        <Progress type="circle" size={126} strokeWidth={8} percent={scorePercent(value)} strokeColor={scoreColor(value, score.shortlistThreshold)} format={() => <span><b>{value.toFixed(1)}</b><small>out of 100</small></span>} />
        <strong>Overall score</strong>
      </div>
    </section>

    <section className="ats-report-section is-blue">
      <header><span><AimOutlined /></span><div><h3>Role fitness assessment</h3><p>Alignment with the configured job profile and shortlist policy.</p></div></header>
      <div className="ats-report-callout">
        <b>{score.recommendation || verdict}</b>
        <p>{score.explanationText || 'This deterministic score is decision support and does not make the hiring decision.'}</p>
        <div className="ats-report-methods"><span>Local score <b>{Number(score.localScore ?? score.totalScore).toFixed(1)}</b></span>{score.aiScore != null && <span>AI analysis <b>{score.aiScore.toFixed(1)}</b></span>}<span>AI status <b>{score.aiAnalysisStatus || 'Not enabled'}</b></span></div>
        {score.overrideScore != null && <div className="ats-report-override"><InfoCircleOutlined /> Calculated {score.totalScore.toFixed(1)} was manually overridden to {score.overrideScore.toFixed(1)}. {score.overrideReason}</div>}
      </div>
    </section>

    <section className="ats-report-section is-violet">
      <header><span><SafetyCertificateOutlined /></span><div><h3>Criterion breakdown</h3><p>Weighted score contribution retained with its supporting summary.</p></div></header>
      {components.length ? <div className="ats-criterion-grid">{components.map(item => {
        const percent = confidencePercent(Number(item.rawRatio))
        return <article key={`${item.id}-${item.criterionCode}`}>
          <div><b>{item.criterionLabel}</b><strong>{item.awardedScore.toFixed(1)} / {item.maximumScore.toFixed(1)}</strong></div>
          <Progress percent={percent} showInfo={false} strokeColor={percent >= 70 ? '#5b4ce6' : percent >= 45 ? '#d97706' : '#dc2626'} />
          <p>{item.evidenceSummary || 'No normalized component summary was recorded.'}</p>
          <small>{item.weight.toFixed(1)}% weight · {percent}% match</small>
        </article>
      })}</div> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No normalized component evidence is available for this historical score." />}
    </section>

    <section className="ats-report-section is-skill">
      <header><span><FileSearchOutlined /></span><div><h3>Job skill assessment</h3><p>Resume evidence mapped to each configured skill requirement.</p></div></header>
      {skillMatches.length ? <div className="ats-skill-grid">{skillMatches.map(item => {
        const percent = confidencePercent(item.confidence || item.semanticSimilarity || (item.matchStatus === 'Matched' ? 1 : item.matchStatus === 'Partial' ? .5 : 0))
        return <article className={statusClass(item.matchStatus)} key={`${item.id}-${item.skillName}`}>
          <div className="ats-skill-title"><b>{item.skillName}</b><Tag color={statusColor(item.matchStatus)}>{item.matchStatus}</Tag></div>
          <div className="ats-skill-track"><i style={{ width: `${percent}%` }} /></div>
          <div className="ats-skill-meta"><span>{item.matchMethod || 'Exact / alias'}</span><strong>{percent}% evidence confidence</strong></div>
          {(item.requirementWeight > 0 || item.minimumYears > 0 || item.minimumProficiency) && <p>{item.requirementWeight > 0 ? `${item.requirementWeight.toFixed(1)}% JD weight` : ''}{item.minimumYears > 0 ? ` · ${item.minimumYears.toFixed(1)} years minimum` : ''}{item.minimumProficiency ? ` · ${item.minimumProficiency}` : ''}</p>}
          <blockquote>{item.evidenceExcerpt || item.matchedTerm || 'No matching resume excerpt was recorded.'}</blockquote>
        </article>
      })}</div> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No skill match evidence was recorded." />}
    </section>

    {(strengths.length > 0 || improvements.length > 0) && <div className="ats-report-insights">
      <section className="is-strength"><header><CheckCircleOutlined /><h3>Strengths</h3></header>{strengths.length ? <ul>{strengths.map(item => <li key={item}>{item}</li>)}</ul> : <p>No strong evidence signal was recorded.</p>}</section>
      <section className="is-improvement"><header><WarningOutlined /><h3>Areas for review</h3></header>{improvements.length ? <ul>{improvements.map(item => <li key={item}>{item}</li>)}</ul> : <p>No material evidence gap was recorded.</p>}</section>
    </div>}

    <section className="ats-report-section is-neutral">
      <header><span><FileSearchOutlined /></span><div><h3>Detailed evidence</h3><p>Expected and observed values kept for recruiter verification.</p></div></header>
      {evidence.length ? <div className="ats-evidence-grid">{evidence.map(item => <article key={`${item.id}-${item.criterionCode}`}>
        <div className="ats-evidence-title"><b>{item.evidenceType}</b><Space size={4} wrap><Tag>{item.criterionCode}</Tag><Tag color={statusColor(item.matchStatus)}>{item.matchStatus}</Tag><Tag>{confidencePercent(item.confidence)}% confidence</Tag></Space></div>
        <dl><div><dt>Expected</dt><dd>{item.expectedValue || '-'}</dd></div><div><dt>Observed</dt><dd>{item.actualValue || '-'}</dd></div></dl>
      </article>)}</div> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No detailed evidence was recorded." />}
    </section>

    {snapshot && <section className="ats-report-section is-neutral">
      <header><span><ClockCircleOutlined /></span><div><h3>Position snapshot used for this score</h3><p>The exact job context is retained even if the live JD changes later.</p></div></header>
      <div className="ats-snapshot-grid">{[
        ['Position', `${snapshot.positionCode} - ${snapshot.positionTitle}`],
        ['Job description', snapshot.jobDescriptionVersionId ? `Version ${snapshot.jobDescriptionVersionNumber || 1} (#${snapshot.jobDescriptionVersionId})` : '-'],
        ['Category', snapshot.positionCategory || '-'],
        ['Experience', snapshot.experienceRange || '-'],
        ['Qualification', snapshot.qualification || '-'],
        ['Location', snapshot.jobLocation || '-'],
        ['Required skills', snapshot.requiredSkills || '-'],
        ['Preferred skills', snapshot.preferredSkills || '-'],
        ['Certifications', snapshot.certifications || '-'],
      ].map(([label, content]) => <article key={label}><span>{label}</span><b>{content}</b></article>)}</div>
    </section>}
  </div>
}
