import type { Client, Employee } from '../types/payroll'
import type { PayrollScopeOption } from '../components/PayrollScopeSelect'

/** Counts use the entire authorized roster, independent of current list filters. */
export function employeeClientOptions(clients: Pick<Client, 'id' | 'name' | 'isActive'>[], employees: Pick<Employee, 'id' | 'clientId' | 'isActive'>[]): PayrollScopeOption[] {
  const permitted = new Set(clients.filter(client => client.isActive).map(client => client.id))
  const counts = new Map<number, number>(), seen = new Set<number>()
  for (const employee of employees) {
    if (!employee.isActive || !permitted.has(employee.clientId) || !Number.isSafeInteger(employee.id) || employee.id <= 0 || seen.has(employee.id)) continue
    seen.add(employee.id)
    counts.set(employee.clientId, (counts.get(employee.clientId) || 0) + 1)
  }
  return [
    { value: 0, label: 'All clients', count: [...counts.values()].reduce((sum, count) => sum + count, 0), hint: 'Active employees' },
    ...clients.filter(client => permitted.has(client.id)).map(client => ({ value: client.id, label: client.name, count: counts.get(client.id) || 0, hint: 'Active employees' })),
  ]
}
