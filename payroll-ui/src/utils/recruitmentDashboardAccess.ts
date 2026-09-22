// Keep these read capabilities aligned with the corresponding API GET guards.
export function recruitmentDashboardAccess(permissions: readonly string[]) {
  const has = (...codes: string[]) => codes.some(code => permissions.includes(code))
  const manage = has('recruitment.manage', 'settings.manage')
  const positions = manage || has('recruitment.position.view', 'recruitment.position.manage', 'recruitment.work-order.manage')
  const cases = manage || has('recruitment.hiring-case.view', 'recruitment.hiring-case.manage')
  return { manage, positions, cases }
}
