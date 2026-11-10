import type { DocumentInput, DocumentRosterRow } from '../services/recruitmentDocumentService'
import type { WorkLocation } from '../types/payroll'

export function rosterLocationName(row: DocumentRosterRow, locations: WorkLocation[]) {
  return locations.find(location => location.id === row.workLocationId)?.name || row.workLocation || 'Not supplied'
}

export function filterRosterRows(rows: DocumentRosterRow[], sourceGroup: string, workLocationId: number) {
  return rows.filter(row => (!sourceGroup || row.sourceGroup === sourceGroup) && (!workLocationId || (workLocationId === -1 ? !row.workLocationId : row.workLocationId === workLocationId)))
}

export function selectedRosterRows(rows: DocumentRosterRow[], selected: number[], readyOnly = false) {
  const ids = new Set(selected)
  return rows.filter(row => ids.has(row.id) && (!readyOnly || !row.documentIssues.length))
}

export const rosterReleaseReady = (row: DocumentRosterRow) => !row.documentIssues.length && Number(row.workLocationId) > 0

export function rosterPermissions(permissions: string[]) {
  const broad = permissions.some(permission => ['settings.manage', 'recruitment.manage'].includes(permission))
  return { canImport: broad || permissions.includes('recruitment.candidate.manage'), canManageOffers: broad || permissions.includes('recruitment.offer.manage'), canRelease: broad || permissions.includes('recruitment.offer.issue') }
}

const masterLocationFields = new Set(['jobLocation', 'workLocation', 'workLocationName', 'instituteName', 'institutionName', 'institutionShortName', 'instituteAddress', 'institutionAddress', 'workLocationAddress', 'workLocationCity', 'workLocationState', 'city', 'state', 'institutionLocation'].map(key => key.toLowerCase()))
export const rosterMasterLocationField = (key: string) => masterLocationFields.has(key.toLowerCase())

const profileIdentityFields = new Set(['candidatename', 'candidateemail', 'candidatephone', 'candidatecode'])
export function rosterEditValues(fields: ReadonlyArray<Pick<DocumentInput, 'key'>>, row: Pick<DocumentRosterRow, 'values' | 'workLocationId'>, draft: Record<string, string>) {
  // The read model also contains master-resolved aliases that are not template
  // inputs. Submit only edited document inputs, never those display projections.
  return Object.fromEntries(fields.filter(({ key }) =>
    Object.hasOwn(draft, key) && !profileIdentityFields.has(key.toLowerCase()) &&
    !(row.workLocationId && rosterMasterLocationField(key)) && draft[key] !== (row.values[key] ?? '')
  ).map(({ key }) => [key, draft[key]]))
}

export function signingAssetFileError(file: Pick<File, 'name' | 'type' | 'size'>) {
  if (!file.size) return 'Choose a PNG or JPEG image that is not empty.'
  if (file.size > 2 * 1024 * 1024) return 'Choose an image of 2 MB or less.'
  const validType = ['image/png', 'image/jpeg'].includes(file.type.toLowerCase())
  if (!validType && (file.type || !/\.(png|jpe?g)$/i.test(file.name))) return 'Choose a PNG or JPEG image.'
  return ''
}
