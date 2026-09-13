import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  AuditOutlined,
  CalendarOutlined,
  CheckCircleOutlined,
  ClockCircleOutlined,
  FileDoneOutlined,
  FileSearchOutlined,
  NotificationOutlined,
  SafetyCertificateOutlined,
  SendOutlined,
  TeamOutlined,
  UserAddOutlined,
  WarningOutlined,
} from '@ant-design/icons'
import { Button, Card, Tag } from 'antd'
import type { Client, RecruitmentCandidateApplication, RecruitmentInterview, RecruitmentOffer, RecruitmentOpenPosition, RecruitmentRequisition } from '../types/payroll'
import type { RecruitmentHiringCase } from '../types/recruitmentCases'
import type { RecruitmentJobPosting } from '../types/recruitmentOrchestration'
import { getRecruitmentHiringCases } from '../services/recruitmentCaseService'
import { getRecruitmentJobPostings } from '../services/recruitmentOrchestrationService'
import { getRecruitmentOpenPositions, getRecruitmentRequisitions } from '../services/recruitmentService'
import { getApplications, getInterviews, getOffers } from '../services/recruitmentTalentService'
import {
  DashboardActionQueue,
  DashboardChartCard,
  DashboardChartGrid,
  DashboardEngine,
  DashboardFilterBar,
  DashboardFunnel,
  DashboardKpiGrid,
  type DashboardChartSpec,
  type DashboardKpi,
  type DashboardQueueItem,
} from './dashboard/DashboardEngine'
import DataTable from './DataTable'

type RecruitmentDashboardData = {
  requisitions: RecruitmentRequisition[]
  positions: RecruitmentOpenPosition[]
  applications: RecruitmentCandidateApplication[]
  interviews: RecruitmentInterview[]
  offers: RecruitmentOffer[]
  postings: RecruitmentJobPosting[]
  hiringCases: RecruitmentHiringCase[]
}

type DashboardFilters = {
  dateRange: 'month' | 'quarter' | 'year' | 'all' | 'custom'
  dateFrom: string
  dateTo: string
  department: string
  location: string
  recruiter: string
  status: string
}

const emptyData: RecruitmentDashboardData = { requisitions: [], positions: [], applications: [], interviews: [], offers: [], postings: [], hiringCases: [] }
const initialFilters: DashboardFilters = { dateRange: 'quarter', dateFrom: '', dateTo: '', department: '', location: '', recruiter: '', status: '' }
const dashboardPalette = ['#6546e8', '#2f80ed', '#20a46b', '#f59e0b', '#e25563', '#17a2b8', '#8b5cf6', '#64748b']
const dayMs = 86400000
const asTime = (value?: string | null) => value ? new Date(value).getTime() : 0
const lower = (value?: string | null) => String(value || '').trim().toLowerCase()
const includesAny = (value: string | null | undefined, needles: string[]) => needles.some(needle => lower(value).includes(needle))
const unique = (values: Array<string | null | undefined>) => [...new Set(values.map(value => String(value || '').trim()).filter(Boolean))].sort((a, b) => a.localeCompare(b))
const ageLabel = (value: string | null | undefined, referenceTime: number) => {
  const days = Math.max(0, Math.floor((referenceTime - asTime(value)) / dayMs))
  if (!Number.isFinite(days) || !asTime(value)) return 'Age unavailable'
  if (days === 0) return 'Today'
  return `${days}d waiting`
}
const sum = (values: number[]) => values.reduce((total, value) => total + Number(value || 0), 0)
const pct = (value: number, base: number) => base > 0 ? (value / base) * 100 : value > 0 ? 100 : 0
const delta = (current: number, previous: number) => previous > 0 ? ((current - previous) / previous) * 100 : current > 0 ? 100 : 0
const monthKey = (value: Date) => `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, '0')}`
const monthLabel = (value: Date) => value.toLocaleDateString('en-IN', { month: 'short', year: '2-digit' })

function rangeFor(filters: DashboardFilters, referenceTime: number) {
  const now = new Date(referenceTime)
  let start = 0
  let end = now.getTime() + dayMs
  if (filters.dateRange === 'month') start = new Date(now.getFullYear(), now.getMonth(), 1).getTime()
  if (filters.dateRange === 'quarter') start = new Date(now.getFullYear(), Math.floor(now.getMonth() / 3) * 3, 1).getTime()
  if (filters.dateRange === 'year') start = new Date(now.getFullYear(), 0, 1).getTime()
  if (filters.dateRange === 'custom') {
    start = asTime(filters.dateFrom)
    end = filters.dateTo ? asTime(filters.dateTo) + dayMs : end
  }
  return { start, end }
}

function inRange(value: string | null | undefined, range: { start: number; end: number }) {
  if (!range.start) return true
  const time = asTime(value)
  return time >= range.start && time < range.end
}

function bucketRows<T>(rows: T[], read: (row: T) => string | null | undefined, months = 6) {
  const anchors = Array.from({ length: months }, (_, index) => {
    const date = new Date()
    date.setDate(1)
    date.setMonth(date.getMonth() - (months - index - 1))
    return date
  })
  const counts = new Map(anchors.map(date => [monthKey(date), 0]))
  rows.forEach(row => {
    const value = read(row)
    if (!value) return
    const key = monthKey(new Date(value))
    if (counts.has(key)) counts.set(key, Number(counts.get(key) || 0) + 1)
  })
  return { labels: anchors.map(monthLabel), values: anchors.map(date => counts.get(monthKey(date)) || 0) }
}

function groupCount<T>(rows: T[], readLabel: (row: T) => string | null | undefined, readValue: (row: T) => number = () => 1, limit = 8) {
  const values = new Map<string, number>()
  rows.forEach(row => {
    const label = String(readLabel(row) || 'Not specified').trim() || 'Not specified'
    values.set(label, Number(values.get(label) || 0) + readValue(row))
  })
  return [...values.entries()].sort((a, b) => b[1] - a[1]).slice(0, limit).map(([label, value]) => ({ label, value }))
}

function applicationSource(row: RecruitmentCandidateApplication) {
  if (row.jobPostingId) return 'Published job link'
  if (includesAny(row.sourceType, ['referral'])) return 'Referral'
  if (includesAny(row.sourceType, ['vendor', 'consultant'])) return 'Vendor / consultant'
  if (includesAny(row.sourceType, ['upload', 'manual', 'resume'])) return 'Manual resume upload'
  return row.sourceType || 'Other'
}

function offerIsReleased(row: RecruitmentOffer) { return !includesAny(row.status, ['draft', 'cancel', 'reject', 'withdraw']) }
function offerIsAccepted(row: RecruitmentOffer) { return includesAny(row.status, ['accepted', 'joining', 'joined']) }
function applicationIsJoined(row: RecruitmentCandidateApplication) { return Boolean(row.joinedEmployeeId) || includesAny(row.currentStage, ['joined']) || includesAny(row.currentStatus, ['joined']) }

export default function RecruitmentDashboardOverview({
  clients,
  selectedClientId,
  canChooseClient,
  onClientChange,
  onNavigate,
}: {
  clients: Client[]
  selectedClientId: number
  canChooseClient: boolean
  onClientChange: (value?: number) => void
  onNavigate: (path: string) => void
}) {
  const [data, setData] = useState<RecruitmentDashboardData>(emptyData)
  const [filters, setFilters] = useState<DashboardFilters>(initialFilters)
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [refreshedAt, setRefreshedAt] = useState<Date | null>(null)
  const [referenceTime, setReferenceTime] = useState(() => Date.now())

  const load = useCallback(async (quiet = false) => {
    if (quiet) setRefreshing(true)
    else setLoading(true)
    try {
      const [requisitions, positions, allApplications, allInterviews, allOffers, postings, hiringCases] = await Promise.all([
        getRecruitmentRequisitions(selectedClientId ? { clientId: selectedClientId } : {}),
        getRecruitmentOpenPositions(selectedClientId),
        getApplications(),
        getInterviews(),
        getOffers(),
        getRecruitmentJobPostings(selectedClientId),
        getRecruitmentHiringCases(selectedClientId),
      ])
      const applications = selectedClientId ? allApplications.filter(row => row.clientId === selectedClientId) : allApplications
      const applicationIds = new Set(applications.map(row => row.id))
      setData({
        requisitions,
        positions,
        applications,
        interviews: allInterviews.filter(row => applicationIds.has(row.applicationId)),
        offers: selectedClientId ? allOffers.filter(row => row.clientId === selectedClientId) : allOffers,
        postings,
        hiringCases,
      })
      setRefreshedAt(new Date())
      setReferenceTime(Date.now())
    } finally {
      setLoading(false)
      setRefreshing(false)
    }
  }, [selectedClientId])

  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])

  const positionById = useMemo(() => new Map(data.positions.map(row => [row.id, row])), [data.positions])
  const positionByRequisitionId = useMemo(() => new Map(data.positions.map(row => [row.requisitionId, row])), [data.positions])
  const dimensionMatch = useCallback((department: string, location: string, recruiter: string, status: string) => {
    if (filters.department && department !== filters.department) return false
    if (filters.location && location !== filters.location) return false
    if (filters.recruiter && recruiter !== filters.recruiter) return false
    if (filters.status && status !== filters.status) return false
    return true
  }, [filters.department, filters.location, filters.recruiter, filters.status])
  const range = useMemo(() => rangeFor(filters, referenceTime), [filters, referenceTime])
  const rangeDuration = range.start ? Math.max(dayMs, range.end - range.start) : 0
  const previousRange = useMemo(() => ({ start: range.start ? range.start - rangeDuration : 0, end: range.start || 0 }), [range.start, rangeDuration])

  const dimensionRows = useMemo(() => {
    const positions = data.positions.filter(row => dimensionMatch(row.department, row.jobLocation, row.recruiterName, row.status))
    const positionIds = new Set(positions.map(row => row.id))
    const requisitions = data.requisitions.filter(row => {
      const position = positionByRequisitionId.get(row.id)
      return dimensionMatch(row.department, row.jobLocation, position?.recruiterName || '', position?.status || row.status)
    })
    const applications = data.applications.filter(row => {
      const position = positionById.get(row.positionId)
      return (!data.positions.length || positionIds.has(row.positionId)) && dimensionMatch(position?.department || '', position?.jobLocation || '', row.recruiterName || position?.recruiterName || '', position?.status || '')
    })
    const applicationIds = new Set(applications.map(row => row.id))
    const interviews = data.interviews.filter(row => applicationIds.has(row.applicationId))
    const offers = data.offers.filter(row => applicationIds.has(row.applicationId))
    const positionClientIds = new Set(positions.map(row => row.clientId))
    const postings = data.postings.filter(row => positionIds.has(row.positionId))
    const hiringCases = data.hiringCases.filter(row => (!row.positionId || positionIds.has(row.positionId)) && (!selectedClientId || row.clientId === selectedClientId || positionClientIds.has(row.clientId)))
    return { positions, requisitions, applications, interviews, offers, postings, hiringCases }
  }, [data, dimensionMatch, positionById, positionByRequisitionId, selectedClientId])

  const current = useMemo(() => ({
    requisitions: dimensionRows.requisitions.filter(row => inRange(row.createdAt || row.requestDate, range)),
    positions: dimensionRows.positions.filter(row => inRange(row.createdAt, range)),
    applications: dimensionRows.applications.filter(row => inRange(row.appliedAt, range)),
    interviews: dimensionRows.interviews.filter(row => inRange(row.scheduledStart, range)),
    offers: dimensionRows.offers.filter(row => inRange(row.proposedJoiningDate || row.expiryDate, range)),
    postings: dimensionRows.postings.filter(row => inRange(row.createdAtUtc || row.publishedAtUtc, range)),
  }), [dimensionRows, range])
  const previous = useMemo(() => ({
    requisitions: dimensionRows.requisitions.filter(row => inRange(row.createdAt || row.requestDate, previousRange)),
    applications: dimensionRows.applications.filter(row => inRange(row.appliedAt, previousRange)),
    offers: dimensionRows.offers.filter(row => inRange(row.proposedJoiningDate || row.expiryDate, previousRange)),
  }), [dimensionRows, previousRange])

  const options = useMemo(() => ({
    departments: unique([...data.positions.map(row => row.department), ...data.requisitions.map(row => row.department)]),
    locations: unique([...data.positions.map(row => row.jobLocation), ...data.requisitions.map(row => row.jobLocation)]),
    recruiters: unique([...data.positions.map(row => row.recruiterName), ...data.applications.map(row => row.recruiterName)]),
    statuses: unique(data.positions.map(row => row.status)),
  }), [data])

  const approvedRequests = current.requisitions.filter(row => includesAny(row.status, ['approved'])).length
  const pendingRequests = current.requisitions.filter(row => includesAny(row.status, ['pending'])).length
  const publishedJobs = current.postings.filter(row => includesAny(row.status, ['publish', 'open', 'active'])).length
  const shortlisted = current.applications.filter(row => includesAny(row.currentStage, ['shortlist', 'screening passed'])).length
  const interviewedApplicationIds = new Set(current.interviews.map(row => row.applicationId))
  const offeredApplicationIds = new Set(current.offers.filter(offerIsReleased).map(row => row.applicationId))
  const joined = current.applications.filter(applicationIsJoined).length
  const filled = sum(current.positions.map(row => row.filledPositions))
  const joiningPending = current.offers.filter(row => offerIsAccepted(row) && !includesAny(row.status, ['joined'])).length
  const slaBreaches = dimensionRows.hiringCases.filter(row => row.stages.some(stage => stage.isSlaBreached) || (row.overallDueAtUtc && asTime(row.overallDueAtUtc) < referenceTime && !includesAny(row.status, ['complete', 'closed']))).length
  const filledDurations = current.positions.filter(row => row.filledPositions > 0).map(row => Math.max(0, (asTime(row.updatedAt) - asTime(row.createdAt)) / dayMs)).filter(Number.isFinite)
  const averageTimeToHire = filledDurations.length ? Math.round(sum(filledDurations) / filledDurations.length) : 0
  const requestTrend = bucketRows(dimensionRows.requisitions, row => row.createdAt)
  const applicationTrend = bucketRows(dimensionRows.applications, row => row.appliedAt)
  const offerTrend = bucketRows(dimensionRows.offers, row => row.proposedJoiningDate)

  const kpis = useMemo<DashboardKpi[]>(() => [
    { key: 'requests', label: 'Hiring requests', value: current.requisitions.length, helper: 'Demand raised', icon: <FileDoneOutlined />, delta: delta(current.requisitions.length, previous.requisitions.length), sparkline: requestTrend.values, onClick: () => onNavigate('/recruitment/requisitions') },
    { key: 'approvals', label: 'Pending approvals', value: pendingRequests, helper: 'Decision required', icon: <AuditOutlined />, tone: pendingRequests ? 'warning' : 'success', onClick: () => onNavigate('/recruitment/requisitions?status=pending') },
    { key: 'vacancies', label: 'Open vacancies', value: sum(current.positions.map(row => row.remainingPositions)), helper: 'Approved demand', icon: <TeamOutlined />, onClick: () => onNavigate('/recruitment/open-positions') },
    { key: 'applications', label: 'Applications', value: current.applications.length, helper: 'Received in period', icon: <FileSearchOutlined />, delta: delta(current.applications.length, previous.applications.length), sparkline: applicationTrend.values, onClick: () => onNavigate('/recruitment/applications') },
    { key: 'shortlisted', label: 'Shortlisted', value: shortlisted, helper: `${pct(shortlisted, current.applications.length).toFixed(0)}% of applications`, icon: <CheckCircleOutlined />, tone: 'success', onClick: () => onNavigate('/recruitment/applications?stage=shortlisted') },
    { key: 'offers', label: 'Offers released', value: offeredApplicationIds.size, helper: 'Candidate decisions', icon: <SendOutlined />, delta: delta(current.offers.filter(offerIsReleased).length, previous.offers.filter(offerIsReleased).length), sparkline: offerTrend.values, onClick: () => onNavigate('/recruitment/offers-and-pre-onboarding') },
    { key: 'joining', label: 'Joining pending', value: joiningPending, helper: 'Accepted, not joined', icon: <CalendarOutlined />, tone: joiningPending ? 'warning' : 'neutral', onClick: () => onNavigate('/recruitment/offers-and-pre-onboarding') },
    { key: 'filled', label: 'Filled positions', value: filled, helper: 'Demand completed', icon: <UserAddOutlined />, tone: 'success', onClick: () => onNavigate('/recruitment/open-positions') },
    { key: 'time', label: 'Average time-to-hire', value: `${averageTimeToHire}d`, helper: 'Created to last update', icon: <ClockCircleOutlined />, tone: 'neutral', onClick: () => onNavigate('/recruitment/hiring-pipeline') },
    { key: 'sla', label: 'SLA breaches', value: slaBreaches, helper: 'Overdue active cases', icon: <WarningOutlined />, tone: slaBreaches ? 'danger' : 'success', onClick: () => onNavigate('/recruitment/work-orders-and-sla') },
  ], [applicationTrend.values, averageTimeToHire, current, filled, joiningPending, offerTrend.values, offeredApplicationIds.size, onNavigate, pendingRequests, previous, requestTrend.values, shortlisted, slaBreaches])

  const funnel = [
    { key: 'requests', label: 'Requests', value: current.requisitions.length, onClick: () => onNavigate('/recruitment/requisitions') },
    { key: 'approved', label: 'Approved', value: approvedRequests, conversion: pct(approvedRequests, current.requisitions.length), onClick: () => onNavigate('/recruitment/open-positions') },
    { key: 'published', label: 'Published jobs', value: publishedJobs, conversion: pct(publishedJobs, approvedRequests), onClick: () => onNavigate('/recruitment/job-postings') },
    { key: 'applications', label: 'Applications', value: current.applications.length, conversionLabel: publishedJobs ? `${(current.applications.length / publishedJobs).toFixed(1)} per job` : 'No published jobs', onClick: () => onNavigate('/recruitment/applications') },
      { key: 'shortlisted', label: 'Shortlisted', value: shortlisted, conversion: pct(shortlisted, current.applications.length), onClick: () => onNavigate('/recruitment/applications') },
    { key: 'interviewed', label: 'Interviewed', value: interviewedApplicationIds.size, conversion: pct(interviewedApplicationIds.size, shortlisted), onClick: () => onNavigate('/recruitment/interviews') },
    { key: 'offered', label: 'Offered', value: offeredApplicationIds.size, conversion: pct(offeredApplicationIds.size, interviewedApplicationIds.size), onClick: () => onNavigate('/recruitment/offers-and-pre-onboarding') },
    { key: 'joined', label: 'Joined', value: joined, conversion: pct(joined, offeredApplicationIds.size), onClick: () => onNavigate('/recruitment/offers-and-pre-onboarding') },
  ]

  const charts = useMemo<DashboardChartSpec[]>(() => {
    const departments = groupCount(current.positions, row => row.department, row => row.remainingPositions)
    const companies = groupCount(current.positions, row => row.clientName, row => row.remainingPositions)
    const locations = groupCount(current.positions, row => row.jobLocation, row => row.remainingPositions)
    const priorities = groupCount(current.requisitions, row => row.hiringPriority || 'Normal', row => row.numberOfOpenings)
    const sources = groupCount(current.applications, applicationSource)
    const joinedBySource = new Map(groupCount(current.applications.filter(applicationIsJoined), applicationSource, () => 1, 50).map(row => [row.label, row.value]))
    const approvalAges = [
      { label: '0–2 days', value: 0 },
      { label: '3–5 days', value: 0 },
      { label: '5+ days', value: 0 },
    ]
    current.requisitions.filter(row => includesAny(row.status, ['pending'])).forEach(row => {
      const days = Math.max(0, Math.floor((referenceTime - asTime(row.submittedAt || row.updatedAt)) / dayMs))
      approvalAges[days <= 2 ? 0 : days <= 5 ? 1 : 2].value += 1
    })
    const ats = [
      { label: 'Needs scoring', value: current.applications.filter(row => row.atsScore == null).length },
      { label: 'Below 40', value: current.applications.filter(row => row.atsScore != null && row.atsScore < 40).length },
      { label: '40–59', value: current.applications.filter(row => row.atsScore != null && row.atsScore >= 40 && row.atsScore < 60).length },
      { label: '60–79', value: current.applications.filter(row => row.atsScore != null && row.atsScore >= 60 && row.atsScore < 80).length },
      { label: '80+', value: current.applications.filter(row => row.atsScore != null && row.atsScore >= 80).length },
    ]
    const monthTrend = bucketRows(dimensionRows.applications, row => row.appliedAt)
    const monthOffers = bucketRows(dimensionRows.offers, row => row.proposedJoiningDate)
    const monthJoined = bucketRows(dimensionRows.applications.filter(applicationIsJoined), row => row.lastStageChangedAt)
    const joiningAnchors = Array.from({ length: 6 }, (_, index) => {
      const start = new Date()
      start.setHours(0, 0, 0, 0)
      start.setDate(start.getDate() + index * 7)
      return start
    })
    const joiningValues = joiningAnchors.map(start => {
      const end = start.getTime() + 7 * dayMs
      return dimensionRows.offers.filter(row => offerIsAccepted(row) && asTime(row.proposedJoiningDate) >= start.getTime() && asTime(row.proposedJoiningDate) < end).length
    })
    const joiningLabels = joiningAnchors.map(date => date.toLocaleDateString('en-IN', { day: '2-digit', month: 'short' }))
    const metric = (rows: { label: string; value: number }[]) => ({ labels: rows.map(row => row.label), series: [{ label: 'Count', values: rows.map(row => row.value) }] })
    const drill = (path: string) => () => onNavigate(path)
    return [
      { id: 'ta-hiring-trend', title: 'Hiring activity trend', subtitle: 'Applications, offers and joins over six months', labels: monthTrend.labels, series: [{ label: 'Applications', values: monthTrend.values, color: dashboardPalette[0] }, { label: 'Offers', values: monthOffers.values, color: dashboardPalette[3] }, { label: 'Joined', values: monthJoined.values, color: dashboardPalette[2] }], kind: 'area', compatibleKinds: ['area', 'line', 'bar'], onPointClick: drill('/recruitment/applications') },
      { id: 'ta-department-demand', title: 'Open demand by department', subtitle: 'Remaining approved vacancies', ...metric(departments), kind: 'horizontalBar', compatibleKinds: ['horizontalBar', 'bar', 'doughnut'], onPointClick: drill('/recruitment/open-positions') },
      { id: 'ta-scope-demand', title: selectedClientId ? 'Open demand by location' : 'Client-wise hiring demand', subtitle: selectedClientId ? 'Remaining vacancies across hiring locations' : 'Vacancies across accessible clients', ...metric(selectedClientId ? locations : companies), kind: 'bar', compatibleKinds: ['bar', 'horizontalBar', 'doughnut'], onPointClick: drill('/recruitment/open-positions') },
      { id: 'ta-priority-demand', title: 'Priority mix', subtitle: 'Hiring demand by business priority', ...metric(priorities), kind: 'doughnut', compatibleKinds: ['doughnut', 'bar', 'horizontalBar'], onPointClick: drill('/recruitment/requisitions') },
      { id: 'ta-candidate-source', title: 'Source-to-joining conversion', subtitle: 'Applications and successful joins by source', labels: sources.map(row => row.label), series: [{ label: 'Applications', values: sources.map(row => row.value), color: dashboardPalette[0] }, { label: 'Joined', values: sources.map(row => joinedBySource.get(row.label) || 0), color: dashboardPalette[2] }], kind: 'bar', compatibleKinds: ['bar', 'horizontalBar'], onPointClick: drill('/recruitment/applications') },
      { id: 'ta-ats-distribution', title: 'ATS score distribution', subtitle: 'Scoring coverage and candidate quality bands', ...metric(ats), kind: 'bar', compatibleKinds: ['bar', 'line', 'doughnut'], onPointClick: drill('/recruitment/applications') },
      { id: 'ta-approval-ageing', title: 'Approval ageing', subtitle: 'Pending requests grouped by waiting time', ...metric(approvalAges), kind: 'bar', compatibleKinds: ['bar', 'doughnut', 'horizontalBar'], onPointClick: drill('/recruitment/requisitions?status=pending') },
      { id: 'ta-joining-forecast', title: 'Joining forecast', subtitle: 'Accepted offers expected over six weeks', labels: joiningLabels, series: [{ label: 'Expected joiners', values: joiningValues, color: dashboardPalette[2] }], kind: 'line', compatibleKinds: ['line', 'area', 'bar'], onPointClick: drill('/recruitment/offers-and-pre-onboarding') },
    ]
  }, [current, dimensionRows, onNavigate, referenceTime, selectedClientId])

  const actionItems = useMemo<DashboardQueueItem[]>(() => {
    const items: DashboardQueueItem[] = []
    dimensionRows.requisitions.filter(row => includesAny(row.status, ['pending'])).slice(0, 3).forEach(row => items.push({
      id: `approval-${row.id}`, title: row.positionTitle, meta: `Pending at ${row.pendingApproverName || row.approvalStageName || 'workflow approver'} · ${row.rfrNumber}`, owner: row.pendingApproverName || 'Approval queue', age: ageLabel(row.submittedAt || row.updatedAt, referenceTime), priority: includesAny(row.hiringPriority, ['urgent', 'high']) ? 'High' : 'Medium', icon: <AuditOutlined />, actionLabel: 'Review', onAction: () => onNavigate('/recruitment/requisitions?status=pending'),
    }))
    const publishedPositionIds = new Set(dimensionRows.postings.filter(row => includesAny(row.status, ['publish', 'open', 'active'])).map(row => row.positionId))
    dimensionRows.positions.filter(row => !publishedPositionIds.has(row.id) && includesAny(row.jobDescriptionStatus, ['approved'])).slice(0, 2).forEach(row => items.push({
      id: `publish-${row.id}`, title: `${row.positionTitle} is ready to publish`, meta: `${row.positionCode} · ${row.clientName}`, owner: row.recruiterName || 'Recruitment team', age: ageLabel(row.updatedAt, referenceTime), priority: 'Normal', icon: <NotificationOutlined />, actionLabel: 'Publish', onAction: () => onNavigate(`/recruitment/job-postings?positionId=${row.id}`),
    }))
    const needsScoring = dimensionRows.applications.filter(row => row.atsScore == null)
    if (needsScoring.length) items.push({ id: 'needs-scoring', title: `${needsScoring.length} candidate${needsScoring.length === 1 ? '' : 's'} need ATS scoring`, meta: 'Resume review and scoring queue', owner: 'Recruitment team', age: ageLabel(needsScoring[0]?.appliedAt, referenceTime), priority: needsScoring.length > 5 ? 'High' : 'Medium', icon: <FileSearchOutlined />, actionLabel: 'Score', onAction: () => onNavigate('/recruitment/applications') })
    const feedback = dimensionRows.interviews.filter(row => includesAny(row.status, ['completed']) && !row.overallFeedback)
    if (feedback.length) items.push({ id: 'interview-feedback', title: `${feedback.length} interview feedback item${feedback.length === 1 ? '' : 's'} pending`, meta: 'Completed interview without consolidated feedback', owner: 'Interview panel', age: ageLabel(feedback[0]?.scheduledEnd, referenceTime), priority: 'High', icon: <TeamOutlined />, actionLabel: 'Follow up', onAction: () => onNavigate('/recruitment/interviews') })
    const expiring = dimensionRows.offers.filter(row => row.expiryDate && asTime(row.expiryDate) >= referenceTime && asTime(row.expiryDate) <= referenceTime + 7 * dayMs && !offerIsAccepted(row))
    if (expiring.length) items.push({ id: 'expiring-offers', title: `${expiring.length} offer${expiring.length === 1 ? '' : 's'} expire within 7 days`, meta: 'Candidate response needs follow-up', owner: 'Offer owner', age: `Due ${new Date(expiring[0].expiryDate || '').toLocaleDateString('en-IN')}`, priority: 'High', icon: <ClockCircleOutlined />, actionLabel: 'Open offers', onAction: () => onNavigate('/recruitment/offers-and-pre-onboarding') })
    const pendingJoiners = dimensionRows.offers.filter(row => offerIsAccepted(row) && !includesAny(row.status, ['joined']))
    if (pendingJoiners.length) items.push({ id: 'joining-readiness', title: `${pendingJoiners.length} upcoming joiner${pendingJoiners.length === 1 ? '' : 's'} need readiness follow-up`, meta: 'Accepted offer awaiting joining completion', owner: 'Pre-onboarding owner', age: pendingJoiners[0].proposedJoiningDate ? `Joins ${new Date(pendingJoiners[0].proposedJoiningDate).toLocaleDateString('en-IN')}` : 'Date pending', priority: 'Medium', icon: <CalendarOutlined />, actionLabel: 'Check readiness', onAction: () => onNavigate('/recruitment/offers-and-pre-onboarding') })
    if (slaBreaches) items.push({ id: 'sla-breaches', title: `${slaBreaches} active hiring SLA breach${slaBreaches === 1 ? '' : 'es'}`, meta: 'Overdue pipeline stage or overall case target', owner: 'Hiring owner', age: 'Overdue', priority: 'High', icon: <WarningOutlined />, actionLabel: 'Resolve', onAction: () => onNavigate('/recruitment/work-orders-and-sla') })
    return items.slice(0, 8)
  }, [dimensionRows, onNavigate, referenceTime, slaBreaches])

  const positionMatrix = useMemo(() => dimensionRows.positions.map(position => {
    const hiringCase = dimensionRows.hiringCases.find(row => row.positionId === position.id)
    const applications = dimensionRows.applications.filter(row => row.positionId === position.id)
    const overdue = Boolean(hiringCase?.overallDueAtUtc && asTime(hiringCase.overallDueAtUtc) < referenceTime && !includesAny(hiringCase.status, ['completed', 'rejected', 'withdrawn']))
    return {
      id: position.id,
      clientName: position.clientName,
      positionCode: position.positionCode,
      positionTitle: position.positionTitle,
      department: position.department,
      location: position.jobLocation,
      openings: position.approvedPositions,
      remaining: position.remainingPositions,
      applications: applications.length,
      currentStage: hiringCase?.currentStageName || (includesAny(position.status, ['closed', 'filled']) ? position.status : position.jobDescriptionStatus || 'Request / JD'),
      owner: position.recruiterName || hiringCase?.currentStakeholderCode || 'Unassigned',
      overallDueAtUtc: hiringCase?.overallDueAtUtc || null,
      status: hiringCase?.status || position.status,
      overdue,
    }
  }), [dimensionRows, referenceTime])

  const filterDefinitions = [
    { key: 'client', label: 'Client', value: selectedClientId || 'all', allowClear: false, disabled: !canChooseClient, searchable: true, options: [{ value: 'all', label: 'All accessible clients' }, ...clients.map(row => ({ value: row.id, label: `${row.code} · ${row.name}` }))] },
    { key: 'dateRange', label: 'Date range', value: filters.dateRange, allowClear: false, options: [{ value: 'month', label: 'This month' }, { value: 'quarter', label: 'This quarter' }, { value: 'year', label: 'This year' }, { value: 'custom', label: 'Custom range' }, { value: 'all', label: 'All time' }] },
    { key: 'department', label: 'Department', value: filters.department, searchable: true, options: options.departments.map(value => ({ value, label: value })) },
    { key: 'location', label: 'Location', value: filters.location, searchable: true, options: options.locations.map(value => ({ value, label: value })) },
    { key: 'recruiter', label: 'Recruiter', value: filters.recruiter, searchable: true, options: options.recruiters.map(value => ({ value, label: value })) },
    { key: 'status', label: 'Job status', value: filters.status, searchable: true, options: options.statuses.map(value => ({ value, label: value })) },
  ]
  const changeFilter = (key: string, value?: string | number) => {
    if (key === 'client') { onClientChange(value === 'all' ? undefined : Number(value || 0)); return }
    setFilters(currentFilters => ({ ...currentFilters, [key]: value || '' }))
  }
  const clearFilters = (key?: string) => {
    if (key === 'client') { onClientChange(undefined); return }
    if (key) { setFilters(currentFilters => ({ ...currentFilters, [key]: key === 'dateRange' ? 'all' : '' })); return }
    setFilters({ ...initialFilters, dateRange: 'all' })
    if (canChooseClient) onClientChange(undefined)
  }

  return <DashboardEngine loading={loading}>
    <DashboardFilterBar
      eyebrow="Talent acquisition intelligence"
      title="Hiring command center"
      filters={filterDefinitions}
      refreshedAt={refreshedAt}
      refreshing={refreshing}
      onChange={changeFilter}
      onRefresh={() => void load(true)}
      onClear={clearFilters}
      extraControls={filters.dateRange === 'custom' ? <>
        <label className="dashboard-date-control"><span>From</span><input aria-label="Custom date from" type="date" value={filters.dateFrom} onChange={event => setFilters(value => ({ ...value, dateFrom: event.target.value }))} /></label>
        <label className="dashboard-date-control"><span>To</span><input aria-label="Custom date to" type="date" value={filters.dateTo} onChange={event => setFilters(value => ({ ...value, dateTo: event.target.value }))} /></label>
      </> : undefined}
    />
    <DashboardKpiGrid items={kpis} />
    <DashboardFunnel title="Hiring funnel" subtitle="Click any stage to open the records behind its conversion." stages={funnel} />
    <Card className="dashboard-position-matrix" title="Position-wise hiring status" extra={<Tag color="purple">{positionMatrix.length} position{positionMatrix.length === 1 ? '' : 's'}</Tag>}>
      <DataTable rows={positionMatrix} getRowId={row => row.id} exportFileName="position-wise-hiring-status" emptyText="No positions match the active filters." columns={[
        { key: 'position', label: 'Position', width: 230, value: row => `${row.positionTitle} ${row.positionCode}`, render: row => <div className="pipeline-table-candidate"><strong>{row.positionTitle}</strong><small>{row.positionCode} · {row.clientName}</small></div> },
        { key: 'department', label: 'Department', width: 170 },
        { key: 'location', label: 'Location', width: 150 },
        { key: 'openings', label: 'Openings', width: 90 },
        { key: 'remaining', label: 'Remaining', width: 100 },
        { key: 'applications', label: 'Candidates', width: 100 },
        { key: 'currentStage', label: 'Current stage', width: 180, render: row => <Tag color={row.overdue ? 'red' : 'blue'}>{row.currentStage}</Tag> },
        { key: 'owner', label: 'Owner', width: 160 },
        { key: 'overallDueAtUtc', label: 'Overall due', width: 150, render: row => row.overallDueAtUtc ? new Date(row.overallDueAtUtc).toLocaleDateString('en-IN') : 'Not started' },
        { key: 'status', label: 'Status', width: 120, render: row => <Tag color={row.overdue ? 'red' : includesAny(row.status, ['complete', 'filled']) ? 'green' : 'default'}>{row.overdue ? 'SLA overdue' : row.status}</Tag> },
        { key: 'action', label: 'Action', width: 90, sortable: false, filterable: false, render: row => <Button size="small" onClick={() => onNavigate(`/recruitment/hiring-pipeline?positionId=${row.id}`)}>Open</Button> },
      ]} />
    </Card>
    <DashboardChartGrid>{charts.map(chart => <DashboardChartCard key={chart.id} spec={chart} />)}</DashboardChartGrid>
    <DashboardActionQueue title="Action needed" subtitle="One operational queue for approvals, publishing, screening, feedback and SLA risk." items={actionItems} emptyText="Nothing needs attention for the active filters." />
    <section className="dashboard-insight-strip" aria-label="Recruitment operating health">
      <article><SafetyCertificateOutlined /><div><b>{pct(joined, offeredApplicationIds.size).toFixed(0)}%</b><span>Offer-to-join conversion</span></div></article>
      <article><CheckCircleOutlined /><div><b>{pct(interviewedApplicationIds.size, shortlisted).toFixed(0)}%</b><span>Shortlist-to-interview</span></div></article>
      <article><ClockCircleOutlined /><div><b>{averageTimeToHire} days</b><span>Average time-to-hire</span></div></article>
      <article><WarningOutlined /><div><b>{slaBreaches}</b><span>SLA cases needing attention</span></div></article>
    </section>
  </DashboardEngine>
}
