import { assignedClients, chooseAssignedClient, isSelectedClientUser, type ClientAccessIdentity } from '../utils/clientAccess'

let identity: ClientAccessIdentity | null = null
let activeClientId = 0
let pendingWrites = 0
const preferenceKey = (user: ClientAccessIdentity) => `payroll.client-access.active:${user.id}`

export function configureClientRequestScope(user: ClientAccessIdentity | null, requestedClientId?: number) {
  identity = user
  activeClientId = user && isSelectedClientUser(user)
    ? chooseAssignedClient(user, assignedClients(user).includes(Number(requestedClientId)) ? requestedClientId : Number(sessionStorage.getItem(preferenceKey(user)))) : 0
  if (user && isSelectedClientUser(user) && activeClientId) sessionStorage.setItem(preferenceKey(user), String(activeClientId))
  return activeClientId
}
export function setActiveClientRequestScope(clientId: number) {
  if (!identity || !isSelectedClientUser(identity) || !assignedClients(identity).includes(clientId) || pendingWrites) return false
  activeClientId = clientId
  sessionStorage.setItem(preferenceKey(identity), String(clientId))
  return true
}
export function activeClientHeader(path: string) {
  if (!identity || !isSelectedClientUser(identity) || /^\/?api\/(auth|public)(\/|\?|$)/i.test(path)) return undefined
  // A missing/revoked assignment is never converted into legacy All clients.
  return activeClientId > 0 ? String(activeClientId) : '0'
}
export function filterActiveClientOptions<T extends { id: number }>(rows: T[], includeAssigned = false) {
  if (!identity || !isSelectedClientUser(identity)) return rows
  const allowed = new Set(assignedClients(identity))
  return rows.filter(row => allowed.has(row.id) && (includeAssigned || row.id === activeClientId))
}
export function beginClientScopedWrite(path: string, method?: string) {
  const tracked = !!activeClientHeader(path) && ['POST', 'PUT', 'PATCH', 'DELETE'].includes((method || 'GET').toUpperCase())
  if (tracked) pendingWrites++
  return () => { if (tracked) pendingWrites = Math.max(0, pendingWrites - 1) }
}
