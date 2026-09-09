import { getJson, getJsonResult } from './apiClient'
import type { WorkflowRequestProgress } from '../types/payroll'

const myRequestsPath = (instanceId?: number) => {
  const query = instanceId ? `?instanceId=${encodeURIComponent(String(instanceId))}` : ''
  return `/api/workflows/requests/mine${query}`
}

export const getMyWorkflowRequests = (instanceId?: number) => {
  return getJson<WorkflowRequestProgress[]>(myRequestsPath(instanceId), [])
}

export const getMyWorkflowRequestsResult = (instanceId?: number) =>
  getJsonResult<WorkflowRequestProgress[]>(myRequestsPath(instanceId), [], { loader: false, toast: false })
