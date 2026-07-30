import { useMemo, useState } from 'react'
import { Button, Card, Col, Descriptions, Drawer, Empty, List, Progress, Row, Space, Table, Tag, Typography, type TableColumnsType } from 'antd'
import { EyeOutlined } from '@ant-design/icons'
import type { RecruitmentApplicationScore, RecruitmentCandidateApplication } from '../types/payroll'

type Props = {
  scores: RecruitmentApplicationScore[]
  applications: RecruitmentCandidateApplication[]
  onOverride?: (score: RecruitmentApplicationScore) => void
}

const effectiveScore = (score: RecruitmentApplicationScore) => score.overrideScore ?? score.totalScore
const scoreColor = (score: number, threshold: number) => score >= threshold ? '#16a34a' : score >= Math.max(30, threshold * 0.65) ? '#d97706' : '#dc2626'
const statusColor = (status: string) => status === 'Matched' ? 'green' : status === 'Partial' ? 'gold' : status === 'Missing' || status === 'NotMatched' ? 'red' : 'default'

export default function RecruitmentAtsScoreDetails({ scores, applications, onOverride }: Props) {
  const [selected, setSelected] = useState<RecruitmentApplicationScore | null>(null)
  const applicationsById = useMemo(() => new Map(applications.map(row => [row.id, row])), [applications])
  const orderedScores = useMemo(() => scores.slice().sort((a, b) => Number(b.isCurrent) - Number(a.isCurrent) || new Date(b.scoredAt).getTime() - new Date(a.scoredAt).getTime()), [scores])
  const scoreColumns: TableColumnsType<RecruitmentApplicationScore> = [
    {
      title: 'Application', key: 'application', render: (_value, row) => {
        const application = applicationsById.get(row.applicationId)
        return <div><Typography.Text strong>{application?.applicationCode ?? `#${row.applicationId}`}</Typography.Text><div><Typography.Text type="secondary">{application?.positionTitle ?? row.positionSnapshot?.positionTitle ?? 'Position snapshot'}</Typography.Text></div></div>
      }
    },
    { title: 'Effective score', key: 'effectiveScore', render: (_value, row) => <Tag color={effectiveScore(row) >= row.shortlistThreshold ? 'green' : 'orange'}>{effectiveScore(row).toFixed(2)} / 100</Tag> },
    { title: 'Threshold', dataIndex: 'shortlistThreshold', key: 'shortlistThreshold', render: value => `${Number(value).toFixed(2)}%` },
    { title: 'Recommendation', dataIndex: 'recommendation', key: 'recommendation', render: value => value || 'Human review required' },
    { title: 'Profile version', dataIndex: 'profileVersionNumber', key: 'profileVersionNumber', render: value => `v${value || 1}` },
    { title: 'Current', dataIndex: 'isCurrent', key: 'isCurrent', render: value => value ? <Tag color="blue">Current</Tag> : <Tag>History</Tag> },
    { title: 'Scored', dataIndex: 'scoredAt', key: 'scoredAt', render: value => new Date(value).toLocaleString('en-IN') },
    {
      title: 'Actions', key: 'actions', render: (_value, row) => <Space wrap>
        <Button size="small" icon={<EyeOutlined />} onClick={() => setSelected(row)}>Evidence</Button>
        {row.isCurrent && onOverride && <Button size="small" onClick={() => onOverride(row)}>Override</Button>}
      </Space>
    }
  ]

  return <>
    <Card size="small" title="ATS score and evidence" extra={<Typography.Text type="secondary">Versioned / explainable / human reviewed</Typography.Text>}>
      {!orderedScores.length
        ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No ATS score has been generated for this candidate." />
        : <Table<RecruitmentApplicationScore> size="small" pagination={orderedScores.length > 6 ? { pageSize: 6 } : false} rowKey="id" dataSource={orderedScores} columns={scoreColumns} />}
    </Card>

    <Drawer open={!!selected} onClose={() => setSelected(null)} title="ATS evidence review" width={760} destroyOnClose>
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
  const componentColumns: TableColumnsType<RecruitmentApplicationScore['components'][number]> = [
    { title: 'Criterion', dataIndex: 'criterionLabel', key: 'criterionLabel' },
    { title: 'Weight', dataIndex: 'weight', key: 'weight', render: value => `${Number(value).toFixed(2)}%` },
    { title: 'Match', dataIndex: 'rawRatio', key: 'rawRatio', render: value => <Progress percent={Math.round(Number(value) * 100)} size="small" /> },
    { title: 'Awarded', key: 'awarded', render: (_value, row) => `${row.awardedScore.toFixed(2)} / ${row.maximumScore.toFixed(2)}` },
    { title: 'Evidence summary', dataIndex: 'evidenceSummary', key: 'evidenceSummary', render: value => value || '-' }
  ]
  return <Space direction="vertical" size="large" style={{ width: '100%' }}>
    <Card size="small">
      <Row gutter={[24, 24]} align="middle">
        <Col xs={24} sm={7} style={{ textAlign: 'center' }}><Progress type="dashboard" percent={Math.max(0, Math.min(100, value))} strokeColor={scoreColor(value, score.shortlistThreshold)} format={() => <span>{value.toFixed(1)}</span>} /></Col>
        <Col xs={24} sm={17}>
          <Typography.Title level={4} style={{ marginTop: 0 }}>{application?.positionTitle ?? snapshot?.positionTitle ?? 'Application score'}</Typography.Title>
          <Space wrap><Tag color={value >= score.shortlistThreshold ? 'green' : 'orange'}>{value >= score.shortlistThreshold ? 'Reached threshold' : 'Below threshold'}</Tag><Tag color="blue">Threshold {score.shortlistThreshold.toFixed(2)}%</Tag><Tag>Profile v{score.profileVersionNumber || 1}</Tag><Tag color="purple">{score.scoringMethod || 'Rule based'}</Tag>{score.humanReviewRequired && <Tag color="purple">Human review required</Tag>}</Space>
          <Space wrap style={{ marginTop: 8 }}><Tag>Local score {Number(score.localScore ?? score.totalScore).toFixed(2)}</Tag>{score.aiScore != null && <Tag color="geekblue">AI analysis {score.aiScore.toFixed(2)} · blend {score.aiBlendWeight.toFixed(0)}%</Tag>}<Tag color={score.aiAnalysisStatus === 'Completed' ? 'green' : score.aiAnalysisStatus === 'NotEnabled' ? 'default' : 'gold'}>AI: {score.aiAnalysisStatus || 'Not enabled'}</Tag></Space>
          <Typography.Paragraph style={{ marginTop: 12, marginBottom: 4 }}><strong>{score.recommendation || 'Recruiter review required'}</strong></Typography.Paragraph>
          <Typography.Paragraph type="secondary">{score.explanationText || 'This deterministic score is decision support and does not make the hiring decision.'}</Typography.Paragraph>
          {score.overrideScore != null && <Typography.Paragraph><Tag color="magenta">Manually overridden</Tag> Calculated {score.totalScore.toFixed(2)} to effective {score.overrideScore.toFixed(2)}. {score.overrideReason}</Typography.Paragraph>}
        </Col>
      </Row>
    </Card>

    <Card size="small" title="Criterion breakdown">
      {components.length ? <Table size="small" pagination={false} rowKey={row => `${row.id}-${row.criterionCode}`} dataSource={components} columns={componentColumns} /> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No normalized component evidence is available for this historical score." />}
    </Card>

    <Card size="small" title="Skill evidence">
      {skillMatches.length ? <List dataSource={skillMatches} renderItem={item => <List.Item>
        <List.Item.Meta
          title={<Space wrap><Typography.Text strong>{item.skillName}</Typography.Text><Tag>{item.skillType}</Tag><Tag color={statusColor(item.matchStatus)}>{item.matchStatus}</Tag><Tag color={item.matchMethod === 'Semantic' ? 'purple' : item.matchMethod === 'Alias' ? 'cyan' : 'default'}>{item.matchMethod || 'Exact/alias'}</Tag>{item.confidence > 0 && <Tag>{Math.round(item.confidence * 100)}% confidence</Tag>}</Space>}
          description={<Space direction="vertical" size={2}>
            {(item.requirementWeight > 0 || item.minimumYears > 0 || item.minimumProficiency) && <Space wrap>
              {item.requirementWeight > 0 && <Tag>JD weight {item.requirementWeight.toFixed(2)}%</Tag>}
              {item.minimumYears > 0 && <Tag>Minimum {item.minimumYears.toFixed(1)} years</Tag>}
              {item.minimumProficiency && <Tag>Proficiency {item.minimumProficiency}</Tag>}
            </Space>}
            {item.matchedTerm && <Typography.Text type="secondary">Matched evidence: {item.matchedTerm}</Typography.Text>}
            <Typography.Text type="secondary">{item.evidenceExcerpt || 'No matching resume excerpt was recorded.'}</Typography.Text>
          </Space>}
        />
      </List.Item>} /> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No skill match evidence was recorded." />}
    </Card>

    <Card size="small" title="Detailed evidence">
      {evidence.length ? <List dataSource={evidence} renderItem={item => <List.Item>
        <List.Item.Meta title={<Space wrap><Typography.Text strong>{item.evidenceType}</Typography.Text><Tag>{item.criterionCode}</Tag><Tag color={statusColor(item.matchStatus)}>{item.matchStatus}</Tag><Tag>{Math.round(item.confidence * 100)}% confidence</Tag></Space>} description={<Descriptions size="small" column={1}><Descriptions.Item label="Expected">{item.expectedValue || '-'}</Descriptions.Item><Descriptions.Item label="Observed">{item.actualValue || '-'}</Descriptions.Item></Descriptions>} />
      </List.Item>} /> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No detailed evidence was recorded." />}
    </Card>

    {snapshot && <Card size="small" title="Position snapshot used for this score"><Descriptions size="small" column={1}>
      <Descriptions.Item label="Position">{snapshot.positionCode} - {snapshot.positionTitle}</Descriptions.Item>
      {snapshot.jobDescriptionVersionId && <Descriptions.Item label="Job description">Version {snapshot.jobDescriptionVersionNumber || 1} (#{snapshot.jobDescriptionVersionId})</Descriptions.Item>}
      <Descriptions.Item label="Category">{snapshot.positionCategory || '-'}</Descriptions.Item>
      <Descriptions.Item label="Required skills">{snapshot.requiredSkills || '-'}</Descriptions.Item>
      <Descriptions.Item label="Preferred skills">{snapshot.preferredSkills || '-'}</Descriptions.Item>
      <Descriptions.Item label="Experience">{snapshot.experienceRange || '-'}</Descriptions.Item>
      <Descriptions.Item label="Qualification">{snapshot.qualification || '-'}</Descriptions.Item>
      <Descriptions.Item label="Certifications">{snapshot.certifications || '-'}</Descriptions.Item>
      <Descriptions.Item label="Location">{snapshot.jobLocation || '-'}</Descriptions.Item>
    </Descriptions></Card>}
  </Space>
}
