import type { AuthUser } from '../types/payroll'

type RequestAccess = { status: string; clientId: number; requestedByUserId?: number; requesterUserId?: number | null; requestedByEmployeeId?: number | null; hasLinkedOpenPosition?: boolean; hasLinkedJobDescription?: boolean }
type User = Pick<AuthUser, 'id' | 'clientId' | 'employeeId' | 'permissions' | 'roles'> & Partial<Pick<AuthUser, 'clientAccessMode' | 'assignedClientIds'>>
const has = (user: User | null | undefined, permission: string) => !!user?.permissions.some(p => p.toLowerCase() === permission)
export const hasRecruitmentPermission = (user: User | null | undefined, ...permissions: string[]) => !!user && (has(user, 'settings.manage')
  || user.clientId == null && user.roles.some(role => role.toLowerCase() === 'super_admin')
  || has(user, 'recruitment.manage') || permissions.some(permission => has(user, permission)))
export const canMaintainApprovedRequisitions = (user?: User | null) => has(user, 'settings.manage')
export const canCreateHiringRequests = (user?: User | null) => hasRecruitmentPermission(user, 'recruitment.rfr.create')
export function canEditHiringRequest(user: User | null | undefined, row: RequestAccess) {
  if (!user || user.clientId != null && user.clientId !== row.clientId || !canCreateHiringRequests(user)) return false
  if (user.clientAccessMode?.toLowerCase() === 'selected' && !user.assignedClientIds?.includes(row.clientId)) return false
  if (row.status === 'Approved') return canMaintainApprovedRequisitions(user)
  if (!['Draft', 'Sent Back'].includes(row.status)) return false
  return canMaintainApprovedRequisitions(user) || has(user, 'recruitment.manage')
    || row.requestedByUserId === user.id || row.requesterUserId === user.id
    || !!user.employeeId && row.requestedByEmployeeId === user.employeeId
}
export function hiringRequestDeleteBlock(row: RequestAccess) {
  return row.hasLinkedOpenPosition ? 'Delete the linked open position and its dependent records first.'
    : row.hasLinkedJobDescription ? 'Delete the linked JD versions first.' : ''
}
