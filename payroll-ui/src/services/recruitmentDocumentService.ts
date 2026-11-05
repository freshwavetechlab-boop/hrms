import type { WorkLocation } from '../types/payroll'
import { apiRequest, deleteJson, getJsonResult, postJson, putJson, readError } from './apiClient'

export type DocumentInput = { key: string; label: string; type: 'text' | 'date' | 'money' | 'number' | 'percent'; required: boolean; sourceHeaders: string[]; defaultValue: string; blankText: string }
export type DocumentImportPreset = { name: string; headerRow: number; defaults: Record<string, string> }
export type DocumentTemplate = { id: number; templateName: string; clientId: number; fields: DocumentInput[]; importPresets: DocumentImportPreset[] }
export type RosterImportSettings = { clientId: number; templateId: number; workLocationId?: number | null; sourceGroup: string; sheetName: string; headerRow: number; mapping: Record<string, number>; defaults: Record<string, string>; sourceRows: number[]; previewToken: string }
export type RosterSheet = { name: string; headers: string[]; sample: string[] }
export type RosterPreviewRow = { sourceRow: number; candidateName: string; email: string; phone: string; positionTitle: string; workLocation: string; matchStatus: string; candidateId: number | null; importIssues: string[]; documentIssues: string[]; values: Record<string, string> }
export type RosterPreview = { fileName: string; fileHash: string; previewToken: string; sheets: string[]; sheetName: string; headers: string[]; mapping: Record<string, number>; fields: DocumentInput[]; rows: RosterPreviewRow[] }
export type DocumentRosterRow = { id: number; clientId: number; candidateId: number; candidateCode: string; candidateName: string; email: string; phone: string; sourceGroup: string; workLocationId?: number | null; workLocation: string; offerNumber?: string; releasedAt?: string | null; templateId: number; templateName: string; sourceFileName: string; sourceSheetName: string; sourceRow: number; values: Record<string, string>; documentIssues: string[]; revision: string }
export type RosterSigningAsset = { available: boolean; fileName: string; contentType: string; width: number; height: number; sha256: string; updatedAt: string | null }
export type RosterImportResult = { createdProfiles: number; reusedProfiles: number; savedRows: number; errors: string[] }
const root = '/api/recruitment/document-roster'
export const getDocumentTemplates = (clientId: number) => getJsonResult<DocumentTemplate[]>(`${root}/templates?clientId=${clientId}`, [], { toast: false, loader: false })
export const getDocumentRoster = (clientId: number) => getJsonResult<DocumentRosterRow[]>(`${root}/rows?clientId=${clientId}`, [], { toast: false, loader: false })
export const getRosterWorkLocations = (clientId: number) => getJsonResult<WorkLocation[]>(`${root}/work-locations?clientId=${clientId}`, [], { toast: false, loader: false })
export const linkRosterWorkLocation = (clientId: number, rows: DocumentRosterRow[], workLocationId: number) => postJson(`${root}/rows/work-location?clientId=${clientId}`, { ids: rows.map(row => row.id), workLocationId, revisions: Object.fromEntries(rows.map(row => [String(row.id), row.revision])) }, null, { toast: false })
const noSigningAsset: RosterSigningAsset = { available: false, fileName: '', contentType: '', width: 0, height: 0, sha256: '', updatedAt: null }
export const getRosterSigningAsset = (clientId: number) => getJsonResult<RosterSigningAsset>(`${root}/signing-asset?clientId=${clientId}`, noSigningAsset, { toast: false, loader: false })
export const removeRosterSigningAsset = (clientId: number) => deleteJson(`${root}/signing-asset?clientId=${clientId}`, null, { toast: false })
export async function uploadRosterSigningAsset(clientId: number, file: File) {
  const form = new FormData(); form.append('file', file)
  const response = await apiRequest(`${root}/signing-asset?clientId=${clientId}`, { method: 'POST', body: form, loader: false, toast: false })
  if (!response.ok) throw new Error(await readError(response))
  return await response.json() as RosterSigningAsset
}

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
  await saveRosterDownload(response, single ? 'draft-offer.pdf' : 'draft-offers.zip')
}

export async function releaseRosterLetters(clientId: number, ids: number[], applySignature: boolean) {
  const single = ids.length === 1
  const response = await apiRequest(single ? `${root}/rows/${ids[0]}/release?clientId=${clientId}&applySignature=${applySignature}` : `${root}/releases?clientId=${clientId}`, { method: 'POST', ...(single ? {} : { body: JSON.stringify({ ids, applySignature }) }), timeoutMs: 180000, loader: false, toast: false })
  if (!response.ok) throw new Error(await readError(response))
  await saveRosterDownload(response, single ? 'offer-letter.pdf' : 'offer-letters.zip')
}

async function saveRosterDownload(response: Response, fallbackName: string) {
  const disposition = response.headers.get('content-disposition') || ''
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1]
  const name = encoded ? decodeURIComponent(encoded) : /filename="?([^";]+)/i.exec(disposition)?.[1] || fallbackName
  const blob = await response.blob(), link = document.createElement('a'), url = URL.createObjectURL(blob)
  link.href = url; link.download = name; link.click(); window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}
