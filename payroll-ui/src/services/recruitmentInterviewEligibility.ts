import type { RecruitmentCandidateApplication, RecruitmentInterview } from '../types/payroll'

export function interviewReady(application: RecruitmentCandidateApplication, interviews: RecruitmentInterview[]) {
  if (application.applicationType !== 'Application' || !application.isInterviewReady) return false
  if (application.atsScore == null || application.atsScore < (application.atsShortlistThreshold || 60)) return false
  return !interviews.some(row => row.applicationId === application.id && ['Scheduled', 'Rescheduled'].includes(row.status))
}

export function panelRecommendationLabel(value: string) {
  return ({ 'Strong Hire': 'Strongly recommend selection', Hire: 'Recommend selection', 'On Hold': 'Recommend hold',
    'No Hire': 'Recommend rejection', 'Strong No Hire': 'Strongly recommend rejection' } as Record<string, string>)[value] || value
}
