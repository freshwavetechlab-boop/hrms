export function employeeEmailEditTarget<T extends { id: number; clientId: number }>(params: URLSearchParams, canManage: boolean, clientId: number, employees: T[]): T | undefined {
  if (!canManage || params.get('edit') !== '0001') return undefined
  const rawEmployee = params.get('employeeId') ?? '', rawClient = params.get('clientId') ?? ''
  if (!/^[1-9]\d*$/.test(rawEmployee) || !/^[1-9]\d*$/.test(rawClient)) return undefined
  const employeeId = Number(rawEmployee), requestedClient = Number(rawClient)
  if (!Number.isSafeInteger(employeeId) || !Number.isSafeInteger(requestedClient) || requestedClient !== clientId) return undefined
  return employees.find(employee => employee.id === employeeId && employee.clientId === requestedClient)
}

export function consumeEmployeeEmailEditLink(params: URLSearchParams) {
  const next = new URLSearchParams(params)
  next.delete('edit')
  return next
}
