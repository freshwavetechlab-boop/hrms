import type { Client, Employee } from '../types/payroll'
import type { PayrollScopeOption } from '../components/PayrollScopeSelect'

/** Counts use the authorized roster, or its lightweight initial count summary,
 * independently of the currently selected client/list filters. */
export function employeeClientOptions(clients: (Pick<Client, 'id' | 'name' | 'isActive'> & { activeEmployeeCount?: number; code?: string })[], employees: Pick<Employee, 'id' | 'clientId' | 'isActive'>[], useClientCounts = false): PayrollScopeOption[] {
  const permitted = new Set(clients.filter(client => client.isActive).map(client => client.id))
  const counts = new Map<number, number>(), seen = new Set<number>()
  for (const employee of employees) {
    if (!employee.isActive || !permitted.has(employee.clientId) || !Number.isSafeInteger(employee.id) || employee.id <= 0 || seen.has(employee.id)) continue
    seen.add(employee.id)
    counts.set(employee.clientId, (counts.get(employee.clientId) || 0) + 1)
  }
  if (useClientCounts) for (const client of clients) {
    if (permitted.has(client.id) && Number.isSafeInteger(client.activeEmployeeCount) && Number(client.activeEmployeeCount) >= 0)
      counts.set(client.id, Number(client.activeEmployeeCount))
  }
  return [
    { value: 0, label: 'All clients', count: [...counts.values()].reduce((sum, count) => sum + count, 0), hint: 'Active employees' },
    ...clients.filter(client => permitted.has(client.id)).map(client => ({ value: client.id, label: client.name, ...(client.code ? { code: client.code } : {}), count: counts.get(client.id) || 0, hint: 'Active employees' })),
  ]
}
