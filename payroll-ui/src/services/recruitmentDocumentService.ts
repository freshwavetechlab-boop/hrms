import { apiRequest, getJsonResult, putJson, readError } from './apiClient'

export type DocumentInput = { key: string; label: string; type: 'text' | 'date' | 'money' | 'number' | 'percent'; required: boolean; sourceHeaders: string[]; defaultValue: string; blankText: string }
export type DocumentImportPreset = { name: string; headerRow: number; defaults: Record<string, string> }
export type DocumentTemplate = { id: number; templateName: string; clientId: number; fields: DocumentInput[]; importPresets: DocumentImportPreset[] }
export type RosterImportSettings = { clientId: number; templateId: number; sourceGroup: string; sheetName: string; headerRow: number; mapping: Record<string, number>; defaults: Record<string, string>; sourceRows: number[]; previewToken: string }
export type RosterSheet = { name: string; headers: string[]; sample: string[] }
export type RosterPreviewRow = { sourceRow: number; candidateName: string; email: string; phone: string; positionTitle: string; workLocation: string; matchStatus: string; candidateId: number | null; importIssues: string[]; documentIssues: string[]; values: Record<string, string> }
export type RosterPreview = { fileName: string; fileHash: string; previewToken: string; sheets: string[]; sheetName: string; headers: string[]; mapping: Record<string, number>; fields: DocumentInput[]; rows: RosterPreviewRow[] }
export type DocumentRosterRow = { id: number; clientId: number; candidateId: number; candidateCode: string; candidateName: string; email: string; phone: string; sourceGroup: string; workLocation: string; templateId: number; templateName: string; sourceFileName: string; sourceSheetName: string; sourceRow: number; values: Record<string, string>; documentIssues: string[]; revision: string }
export type RosterImportResult = { createdProfiles: number; reusedProfiles: number; savedRows: number; errors: string[] }
const root = '/api/recruitment/document-roster'
export const getDocumentTemplates = (clientId: number) => getJsonResult<DocumentTemplate[]>(`${root}/templates?clientId=${clientId}`, [], { toast: false, loader: false })
export const getDocumentRoster = (clientId: number) => getJsonResult<DocumentRosterRow[]>(`${root}/rows?clientId=${clientId}`, [], { toast: false, loader: false })

export async function uploadDocumentRoster<T>(action: 'inspect' | 'preview' | 'import', file: File, settings: RosterImportSettings) {
  const form = new FormData(); form.append('file', file); form.append('settings', JSON.stringify(settings))
  const response = await apiRequest(`${root}/${action}`, { method: 'POST', body: form, loader: false, toast: false, timeoutMs: action === 'import' ? 600000 : 60000 })
  if (!response.ok) throw new Error(await readError(response))
  return await response.json() as T
}
export const editDocumentRoster = (clientId: number, row: DocumentRosterRow, values: Record<string, string>) => putJson<{ values: Record<string, string>; revision: string }, null>(`${root}/rows/${row.id}?clientId=${clientId}`, { values, revision: row.revision }, null, { toast: false })

export async function downloadRosterDrafts(clientId: number, ids: number[]) {
  const single = ids.length === 1
  const response = await apiRequest(single ? `${root}/rows/${ids[0]}/draft?clientId=${clientId}` : `${root}/drafts?clientId=${clientId}`, { method: 'POST', ...(single ? {} : { body: JSON.stringify({ ids }) }), timeoutMs: 180000, loader: false, toast: false })
  if (!response.ok) throw new Error(await readError(response))
  const disposition = response.headers.get('content-disposition') || ''
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1]
  const name = encoded ? decodeURIComponent(encoded) : /filename="?([^";]+)/i.exec(disposition)?.[1] || (single ? 'draft-offer.pdf' : 'draft-offers.zip')
  const blob = await response.blob(), link = document.createElement('a'), url = URL.createObjectURL(blob)
  link.href = url; link.download = name; link.click(); window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}
