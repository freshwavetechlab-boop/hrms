// Keep these read capabilities aligned with the corresponding API GET guards.
export function recruitmentDashboardAccess(permissions: readonly string[]) {
  const has = (...codes: string[]) => codes.some(code => permissions.includes(code))
  const manage = has('recruitment.manage', 'settings.manage')
  const positions = manage || has('recruitment.position.view', 'recruitment.position.manage', 'recruitment.work-order.manage')
  const cases = manage || has('recruitment.hiring-case.view', 'recruitment.hiring-case.manage')
  const requisitions = manage || has('recruitment.rfr.view', 'recruitment.rfr.create')
  const applications = manage || has('recruitment.candidate.view', 'recruitment.interview.schedule', 'recruitment.offer.manage', 'recruitment.proposal.manage')
  const interviews = manage || has('recruitment.interview.schedule', 'recruitment.interview.panel')
  const offers = manage || has('recruitment.offer.manage', 'recruitment.offer.issue', 'recruitment.proposal.manage', 'recruitment.proposal.approve')
  const postings = manage || has('recruitment.position.view', 'recruitment.position.manage', 'recruitment.publish')
  return { manage, positions, cases, requisitions, applications, interviews, offers, postings }
}

export function recruitmentLoginPath(permissions: readonly string[]) {
  if (permissions.includes('settings.manage') || permissions.includes('security.manage')) return '/dashboard'
  const access = recruitmentDashboardAccess(permissions)
  if (access.positions || access.cases || access.requisitions || access.applications || access.offers) return '/recruitment/dashboard'
  return access.interviews ? '/recruitment/interviews' : '/dashboard'
}
