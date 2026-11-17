import type { ApiResult } from '../services/apiClient'
import type { PayRun } from '../types/payroll'

export function payslipRegisterRunError(result: ApiResult<PayRun | null>, clientId: number, runId: number): string {
  if (!result.ok) return 'Unable to load this pay run' + (result.status ? ' (HTTP ' + result.status + ')' : '') + ': ' + (result.error || 'Request failed.')
  const value = result.data
  if (!value) return 'The API returned an empty pay run response. Retry loading this approved run.'
  if (value.id !== runId || value.clientId !== clientId) return 'The pay run response did not match the selected client and run. Refresh or retry.'
  if (!['Approved', 'Partially Paid', 'Paid'].includes(value.status)) return 'This run is no longer approved. Refresh the page to review its current status.'
  if (!Array.isArray(value.employees)) return 'The API returned incomplete pay run details. Retry loading employee rows.'
  return ''
}
