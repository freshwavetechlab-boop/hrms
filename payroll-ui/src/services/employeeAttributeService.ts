import type {
  EmployeeAttributeContext,
  EmployeeAttributeLookupOption,
  SaveEmployeeAttributeValuesRequest,
  SaveEmployeeAttributeValuesResult,
} from '../types/employeeAttributes'
import { getJson, postJson } from './apiClient'

const emptyContext = (employeeId: number, clientId: number, infotypeCode: string): EmployeeAttributeContext => ({
  employeeId,
  clientId,
  infotypeCode,
  forms: [],
  values: [],
})

export const getEmployeeAttributeContext = (employeeId: number, clientId: number, infotypeCode: string) => {
  const query = new URLSearchParams({ clientId: String(clientId), infotypeCode })
  return getJson<EmployeeAttributeContext>(`/api/employees/${employeeId}/dynamic-fields?${query}`, emptyContext(employeeId, clientId, infotypeCode))
    .then(context => ({
      ...emptyContext(employeeId, clientId, infotypeCode),
      ...context,
      forms: Array.isArray(context?.forms) ? context.forms : [],
      values: Array.isArray(context?.values) ? context.values : [],
      files: Array.isArray(context?.files) ? context.files : [],
    }))
}

export const saveEmployeeAttributeValues = (employeeId: number, request: SaveEmployeeAttributeValuesRequest) =>
  postJson(`/api/employees/${employeeId}/dynamic-fields`, request, {
    employeeId,
    savedCount: 0,
    values: request.values,
  } as SaveEmployeeAttributeValuesResult, { toast: false })

export const searchEmployeeAttributeLookup = (employeeId: number, clientId: number, fieldId: number, search: string) => {
  const query = new URLSearchParams({ clientId: String(clientId), search })
  return getJson<EmployeeAttributeLookupOption[]>(`/api/employees/${employeeId}/dynamic-fields/${fieldId}/lookup?${query}`, [])
}
