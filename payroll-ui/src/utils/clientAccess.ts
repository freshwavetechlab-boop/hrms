/** User assignments and the currently active client are separate from role grants. */
export type ClientAccessIdentity = { id?: number; clientId?: number | null; clientAccessMode?: string; assignedClientIds?: number[]; roles?: string[]; permissions?: string[] }
export const isSelectedClientUser = (user?: ClientAccessIdentity | null) => user?.clientAccessMode?.toLowerCase() === 'selected'
export const assignedClients = (user?: ClientAccessIdentity | null) => [...new Set((user?.assignedClientIds || []).filter(id => Number.isSafeInteger(id) && id > 0))]
export function isUnrestrictedClientAdmin(user?: ClientAccessIdentity | null) {
  return !!user && !user.clientId && !isSelectedClientUser(user) && (user.roles?.some(role => role.toLowerCase() === 'super_admin') || user.permissions?.some(permission => ['security.manage', 'settings.manage'].includes(permission))) === true
}
const globalUiPermissions = new Set(['security.manage', 'client.users.manage', 'client.roles.assign', 'workflow.manage'])
/** Existing module components continue using their established single-client UI.
 * The shell supplies switching between assigned clients; the server independently
 * validates every request using the original authenticated assignment. */
export function projectClientAccess<T extends ClientAccessIdentity>(user: T, activeClientId: number): T {
  if (!isSelectedClientUser(user)) return user
  return { ...user, clientId: assignedClients(user).includes(activeClientId) ? activeClientId : 0,
    roles: user.roles?.filter(role => role.toLowerCase() !== 'super_admin'),
    permissions: user.permissions?.filter(permission => !globalUiPermissions.has(permission)) }
}
export function chooseAssignedClient(user: ClientAccessIdentity, requested?: number) {
  const ids = assignedClients(user)
  return ids.includes(Number(requested)) ? Number(requested) : ids[0] || 0
}
