import type { Employee, WorkLocation } from '../types/payroll'

export const workforceFields = ['employmentType', 'skillCategory', 'department', 'designation', 'grade', 'gender', 'location', 'portalAccess', 'campusGender'] as const
export type WorkforceField = typeof workforceFields[number]

export function workforceValue(row: Employee, key: WorkforceField, locations: WorkLocation[] = []): string {
  const text = (value: string | undefined, fallback = 'Not mapped') => value?.trim() || fallback
  switch (key) {
    case 'employmentType': return text(row.personalDetails?.employmentType, 'Not specified')
    case 'skillCategory': return text(row.personalDetails?.skillCategory, 'Not categorized')
    case 'location': return text(locations.find(item => item.id === row.workLocationId)?.name)
    case 'portalAccess': return row.portalAccess ? 'ESS enabled' : 'Not enabled'
    case 'campusGender': return ['male', 'female'].includes(row.gender?.trim().toLowerCase()) ? row.gender.trim().toLowerCase() : 'other'
    default: return text(row[key])
  }
}

export function matchesWorkforce(row: Employee, params: URLSearchParams, locations: WorkLocation[]) {
  const clientId = Number(params.get('clientId') || 0)
  return row.isActive && (!clientId || row.clientId === clientId) && workforceFields.every(key =>
    !params.has(key) || workforceValue(row, key, locations).toLocaleLowerCase() === params.get(key)!.trim().toLocaleLowerCase())
}

export function workforcePath(clientId: number, filters: Partial<Record<WorkforceField, string>> = {}) {
  return `/employees/master?${new URLSearchParams({ source: 'workforce', clientId: String(clientId), ...filters })}`
}
