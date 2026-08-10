import type { User } from '../types'

export function canMaintainTravelExpense(user: User) {
  const roles = new Set(user.roles.map(role => role.toLowerCase()))
  const permissions = new Set(user.permissions.map(permission => permission.toLowerCase()))
  return roles.has('super_admin') || permissions.has('settings.manage') || permissions.has('security.manage')
}
