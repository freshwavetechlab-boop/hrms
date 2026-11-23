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
const recruitmentReadPaths = new Set([
  '/api/recruitment/dashboard', '/api/recruitment/requisitions', '/api/recruitment/open-positions',
  '/api/recruitment/applications', '/api/recruitment/interviews', '/api/recruitment/offers',
  '/api/recruitment/hiring-cases', '/api/recruitment/pipeline-workspace', '/api/recruitment/pipeline-targets',
  '/api/recruitment/client-configuration-summary', '/api/recruitment-orchestration/job-postings',
  '/api/recruitment-orchestration/pipelines',
  '/api/recruitment-orchestration/forms', '/api/recruitment-admin/templates',
])
const configurationCommand = (path: string) => path === '/api/recruitment-orchestration/pipeline-configurations'
  || /^\/api\/recruitment-orchestration\/(?:pipelines|forms)\/[1-9]\d*\/create-similar$/.test(path)
  || /^\/api\/recruitment-admin\/templates\/[1-9]\d*\/create-similar$/.test(path)

export function activeClientHeader(path: string, method = 'GET') {
  if (!identity || !isSelectedClientUser(identity) || /^\/?api\/(auth|public)(\/|\?|$)/i.test(path)) return undefined
  const [rawPath, query = ''] = path.split('?', 2)
  const route = ('/' + rawPath.replace(/^\//, '')).replace(/\/$/, '').toLowerCase()
  const verb = method.toUpperCase()
  if (verb === 'GET') {
    const isRecruitment = /^\/api\/recruitment(?:-orchestration|-admin)?(?:\/|$)/.test(route)
    const hasExplicitClient = [...new URLSearchParams(query).keys()].some(key => ['clientid', 'clientids', 'companyid'].includes(key.replaceAll('_', '').toLowerCase()))
    // An explicit TA picker may be editing a different assigned client from the
    // shell. The API validates that query instead of receiving a conflicting header.
    if (isRecruitment && hasExplicitClient) return undefined
    if (recruitmentReadPaths.has(route) || /^\/api\/recruitment-orchestration\/(?:pipelines\/[1-9]\d*(?:\/versions)?|pipeline-versions\/[1-9]\d*|forms\/[1-9]\d*)$/.test(route)) return undefined
  }
  // Atomic copy/configuration commands validate their own source and target.
  if (verb === 'POST' && configurationCommand(route)) return undefined
  // Definition/template bodies supply one client; normal scoped middleware still
  // validates that client and every referenced record before projecting identity.
  if (verb === 'POST' && (route === '/api/recruitment-admin/templates' || route === '/api/recruitment-orchestration/forms')) return undefined
  const explicitClient = /^[1-9]\d*$/.test(new URLSearchParams(query).get('clientId') ?? '')
  if (verb === 'POST' && explicitClient && /^\/api\/recruitment\/document-roster\/standard\/(?:preview|import)$/.test(route)) return undefined
  if (verb === 'POST' && explicitClient && /^\/api\/recruitment-orchestration\/(?:forms\/[1-9]\d*\/versions|form-versions\/[1-9]\d*\/publish)$/.test(route)) return undefined
  // Competency creation supplies the editor's client in its body. The normal
  // scoped middleware validates and projects that single client plus any row ID.
  if (verb === 'POST' && route === '/api/recruitment-orchestration/interview-competencies') return undefined
  // Delete remains single-client: its owning-client query is validated together
  // with the definition ID by the API, rather than a stale shell selection.
  if (verb === 'DELETE' && /^\/api\/recruitment-orchestration\/(?:pipelines|forms)\/[1-9]\d*$/.test(route)
    && explicitClient) return undefined
  // A missing/revoked assignment is never converted into legacy All clients.
  return activeClientId > 0 ? String(activeClientId) : '0'
}
export function filterActiveClientOptions<T extends { id: number }>(rows: T[], includeAssigned = false) {
  if (!identity || !isSelectedClientUser(identity)) return rows
  const allowed = new Set(assignedClients(identity))
  return rows.filter(row => allowed.has(row.id) && (includeAssigned || row.id === activeClientId))
}
export function beginClientScopedWrite(path: string, method?: string) {
  const tracked = !!identity && isSelectedClientUser(identity) && !/^\/?api\/(auth|public)(\/|\?|$)/i.test(path)
    && ['POST', 'PUT', 'PATCH', 'DELETE'].includes((method || 'GET').toUpperCase())
  if (tracked) pendingWrites++
  return () => { if (tracked) pendingWrites = Math.max(0, pendingWrites - 1) }
}
