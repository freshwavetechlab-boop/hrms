import type { Employee, PayRun, WorkLocation } from '../types/payroll'
import type { LocationAttendanceContext } from '../services/payrollLocationService'

export function regularPayrollLocations(clientId: number, period: string, locations: WorkLocation[], employees: Employee[], context: LocationAttendanceContext | null, runs: PayRun[]) {
  const currentContext = context?.clientId === clientId && context.month === period ? context : null
  const map = new Map<number, { id: number; name: string; employeeIds: Set<number>; runIds: Set<number> }>()
  for (const location of locations) if (location.clientId === clientId && location.isActive) map.set(location.id, { id: location.id, name: location.name, employeeIds: new Set(), runIds: new Set() })
  for (const row of currentContext?.rows ?? []) {
    const location = map.get(row.workLocationId) || { id: row.workLocationId, name: row.workLocationName || 'Location not supplied', employeeIds: new Set<number>(), runIds: new Set<number>() }
    location.employeeIds.add(row.employeeId)
    if (row.lockedPayRunId) location.runIds.add(row.lockedPayRunId)
    map.set(row.workLocationId, location)
  }
  if (!currentContext) for (const employee of employees) {
    if (employee.clientId === clientId && employee.isActive) map.get(employee.workLocationId)?.employeeIds.add(employee.id)
  }
  const monthRuns = runs.filter(run => run.clientId === clientId && run.payPeriod === period && run.runType !== 'Off Cycle')
  for (const run of monthRuns) {
    try {
      const request = JSON.parse(run.requestJson || '{}')
      const ids = new Set<number>([...(request.includedEmployeeIds ?? request.IncludedEmployeeIds ?? []), ...(run.employees ?? []).map(row => row.employeeId)])
      for (const location of map.values()) if ([...location.employeeIds].some(id => ids.has(id))) location.runIds.add(run.id)
    } catch { /* Unknown legacy scope is not guessed from the run's title. */ }
  }
  return [...map.values()].map(location => ({ id: location.id, name: location.name, count: location.employeeIds.size, runIds: [...location.runIds].filter(id => monthRuns.some(run => run.id === id)), historicalScope: !!currentContext })).sort((a, b) => a.name.localeCompare(b.name))
}
