import type { RecruitmentCandidateApplication, RecruitmentInterview } from '../types/payroll'
import { recruitmentEligibility } from '../utils/recruitmentEligibility'

export function interviewReady(application: RecruitmentCandidateApplication, interviews: RecruitmentInterview[]) {
  if (application.applicationType !== 'Application' || !application.isInterviewReady) return false
  if (!hasInterviewEligibility(application)) return false
  return !interviews.some(row => row.applicationId === application.id && ['Scheduled', 'Rescheduled'].includes(row.status))
}

export function hasInterviewEligibility(application: RecruitmentCandidateApplication) {
  return recruitmentEligibility(application).isEligible && application.atsScore != null
    && application.atsScore >= (application.atsShortlistThreshold || 60)
}

export function panelRecommendationLabel(value: string) {
  return ({ 'Strong Hire': 'Strongly recommend selection', Hire: 'Recommend selection', 'On Hold': 'Recommend hold',
    'No Hire': 'Recommend rejection', 'Strong No Hire': 'Strongly recommend rejection' } as Record<string, string>)[value] || value
}
