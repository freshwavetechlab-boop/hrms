import { apiRequest, getJsonResult, readError } from './apiClient'

export type RosterExchangeMode = 'Insert' | 'Update'
export type RosterExchangeColumn = { key: string; header: string; infotype: string; type: string; readOnly: boolean; requiredOnInsert: boolean; description: string }
export type RosterExchangeTemplate = { version: string; clientId: number; templateId: number; sourceGroup: string; columns: RosterExchangeColumn[]; rows: Record<string, string>[]; importBatches: string[] }
export type RosterExchangeSettings = { clientId: number; templateId: number; sourceGroup: string; mode: RosterExchangeMode; sourceRows: number[]; previewToken: string }
export type RosterExchangeRow = { sourceRow: number; profileCode: string; employeeCode: string; employeeName: string; action: 'Insert' | 'Update' | 'Unchanged' | 'Skip' | 'Inserted' | 'Updated'; reason: string; changedFields: string[]; values: Record<string, string> }
export type RosterExchangePreview = { fileName: string; previewToken: string; rows: RosterExchangeRow[] }
export type RosterExchangeResult = { inserted: number; updated: number; unchanged: number; skipped: number; items: RosterExchangeRow[] }
const root = '/api/recruitment/document-roster/standard'
export const getStandardRosterTemplate = (settings: Pick<RosterExchangeSettings, 'clientId' | 'templateId' | 'sourceGroup' | 'mode'>) =>
  getJsonResult<RosterExchangeTemplate | null>(`${root}/template?${new URLSearchParams(Object.fromEntries(Object.entries(settings).map(([key, value]) => [key, String(value)])))}`, null, { toast: false, loader: false })
export async function uploadStandardRoster<T>(action: 'preview' | 'import', file: File, settings: RosterExchangeSettings) {
  const body = new FormData(); body.append('file', file); body.append('settings', JSON.stringify(settings))
  const response = await apiRequest(`${root}/${action}?clientId=${settings.clientId}`, { method: 'POST', body, loader: false, toast: false, timeoutMs: action === 'import' ? 600000 : 120000 })
  if (!response.ok) throw new Error(await readError(response))
  return await response.json() as T
}
