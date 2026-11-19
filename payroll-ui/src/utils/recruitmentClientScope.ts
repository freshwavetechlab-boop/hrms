import { assignedClients, isSelectedClientUser, type ClientAccessIdentity } from './clientAccess'

// A display selection intersects the authenticated assignments. It never grants
// client access; the API independently scopes every query and mutation.
export function recruitmentClientSelection(user: ClientAccessIdentity | null | undefined, routeValue: string | null, savedClientId = 0, defaultAll = false) {
  if (!isSelectedClientUser(user) && user?.clientId && user.clientId > 0) return user.clientId
  const requested = routeValue === null ? defaultAll ? 0 : savedClientId : Number(routeValue)
  if (!Number.isSafeInteger(requested) || requested <= 0) return 0
  if (isSelectedClientUser(user) && !assignedClients(user).includes(requested)) return 0
  return requested
}
