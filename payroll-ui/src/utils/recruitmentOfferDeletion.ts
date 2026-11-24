import type { AuthUser } from '../types/payroll'

export const canHardDeleteRecruitmentOffers = (user?: Pick<AuthUser, 'id' | 'isActive' | 'mustChangePassword' | 'clientId' | 'clientAccessMode' | 'roles'> | null) =>
  !!user && user.id > 0 && user.isActive && !user.mustChangePassword && user.clientId == null
  && user.clientAccessMode?.toLowerCase() !== 'selected' && user.roles.some(role => role.toLowerCase() === 'super_admin')

export type OfferDeletionTarget = { sourceType: 'Offer' | 'Roster'; sourceId: number; clientId: number }
export function offerDeletionPath(target: OfferDeletionTarget): string {
  if (![target.sourceId, target.clientId].every(id => Number.isSafeInteger(id) && id > 0)) throw new Error('Select a valid offer and client.')
  if (target.sourceType === 'Roster') return `/api/recruitment/document-roster/rows/${target.sourceId}?clientId=${target.clientId}`
  if (target.sourceType === 'Offer') return `/api/recruitment/offers/${target.sourceId}`
  throw new Error('The offer source is invalid.')
}
