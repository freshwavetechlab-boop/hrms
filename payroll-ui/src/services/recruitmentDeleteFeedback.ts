import type { ToastAction } from '../components/ToastProvider'

type DependencyRoute = {
  matches: RegExp[]
  action: ToastAction
}

const dependencyRoutes: DependencyRoute[] = [
  {
    matches: [/open position/i, /position\/posting record/i],
    action: { label: 'Open positions', href: '/recruitment/open-positions' },
  },
  {
    matches: [/job[- ]description/i, /ATS scoring evidence/i],
    action: { label: 'Open job descriptions', href: '/recruitment/job-descriptions' },
  },
  {
    matches: [/job posting/i, /posting\(s\)/i, /posting record/i],
    action: { label: 'Open job postings', href: '/recruitment/job-postings' },
  },
  {
    matches: [/live cumulative/i, /hiring case/i, /cumulative hiring/i, /signed\/process document/i, /profile-forwarding batch/i],
    action: { label: 'Open Work Orders & SLA', href: '/recruitment/work-orders-and-sla' },
  },
  {
    matches: [/interview/i, /panel feedback/i],
    action: { label: 'Open interviews', href: '/recruitment/interviews' },
  },
  {
    matches: [/offer/i, /pre[- ]onboarding/i, /offer letter/i],
    action: { label: 'Open offers & onboarding', href: '/recruitment/offers-and-pre-onboarding' },
  },
  {
    matches: [/application/i, /candidate journey/i],
    action: { label: 'Open applications', href: '/recruitment/applications' },
  },
  {
    matches: [/candidate profile/i, /candidate document/i],
    action: { label: 'Open talent pool', href: '/recruitment/talent-pool' },
  },
  {
    matches: [/pipeline stage/i, /pipeline version/i, /pipeline is assigned/i, /pipeline contains/i],
    action: { label: 'Open Pipeline Designer', href: '/settings/recruitment-administration/pipelines' },
  },
  {
    matches: [/form is used/i, /form has/i, /form is configured/i, /submitted form/i],
    action: { label: 'Open Form Designer', href: '/settings/recruitment-administration/forms' },
  },
  {
    matches: [/employee-converted/i, /joined/i, /employee lifecycle/i],
    action: { label: 'Open Employee Master', href: '/employees/master' },
  },
  {
    matches: [/global workflow approval/i, /workflow approval/i],
    action: { label: 'Open approval tasks', href: '/tasks' },
  },
]

export function recruitmentDeleteActions(path: string, error: string): ToastAction[] {
  if (!path.toLowerCase().includes('recruitment')) return []
  const actions: ToastAction[] = []
  for (const route of dependencyRoutes) {
    if (!route.matches.some(pattern => pattern.test(error))) continue
    if (!actions.some(action => action.href === route.action.href)) actions.push(route.action)
  }
  return actions.slice(0, 3)
}
