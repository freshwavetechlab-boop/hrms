import { useEffect, useState } from 'react'
import type { Component, Structure } from '../types/payroll'
import { getJsonResult, postJson } from './apiClient'
import { reviewSnapshot, type ReviewSnapshot } from './payrollAdvisorService'

export type PayrollValue = { token: string; meaning: string; source: string; unit: string; example: string; availability: string; legacy: boolean; componentId?: string | null }
export type SimulationLine = { code: string; name: string; category: string; formula: string; substituted: string; raw: number; monthly: number; earned: number; unroundedEarned?: number | null; proRata: boolean; source: string; rounding: string; inputs: Record<string, number> }
export type AttendanceInputEvidence = { sourceKind: string; payPeriod: string; workingDays: number | null; presentDays: number | null; actualPresenceKnown?: boolean; payableDays: number | null; weeklyOffPayableUnits: number | null; holidayPayableUnits: number | null; cycleStart: string | null; cycleEnd: string | null; temporaryDayOverrides: boolean; policyValidationError: string; explanation: string }
export type SimulationLocationAllocations = { version: number; clientId: number; employeeId: number; payPeriod: string; referenceAssignmentId?: string | null; parts: { partKey: string; assignmentId: string; workLocationId: number; salaryStructureId: string; payrollDays: number; presentDays: number; payableDays: number }[] }
export type SimulationResult = { clientId?: number; employeeId?: number | null; payPeriod?: string; locationAllocations?: SimulationLocationAllocations | null; attendanceEvidence?: AttendanceInputEvidence | null; mode: string; templateId: string; employee: string; complete: boolean; coverage: string; issues: { severity: string; component: string; message: string; action: string }[]; inputs: { name: string; value: number | null; source: string; unit: string }[]; lines: SimulationLine[]; gross: number | null; deductions: number | null; benefits: number | null; net: number | null }
export type SimulationEmployee = { id: number; code: string; name: string }
const catalogueCache = new Map<string, PayrollValue[]>()
export function usePayrollCatalogue(components: Component[], enabled = true, included?: string[], exclude?: string) {
  const key = JSON.stringify({ components: components.filter(c => c.code.trim()).map(c => ({ id: String(c.id), code: c.code, name: c.name, active: c.active })), includedComponentIds: included, excludeComponentId: exclude })
  const [state, setState] = useState<{ key: string; values: PayrollValue[]; error: string; loading: boolean }>({ key: '', values: [], error: '', loading: false })
  const [retry, setRetry] = useState(0)
  useEffect(() => {
    if (!enabled) return
    let cancelled = false
    const cached = catalogueCache.get(key)
    if (cached) { setState({ key, values: cached, error: '', loading: false }); return }
    setState({ key, values: [], error: '', loading: true })
    void postJson('/api/payroll-calculation/catalogue', JSON.parse(key), [] as PayrollValue[], { toast: false, loader: false }).then(result => {
      if (cancelled) return
      const valid = result.ok && Array.isArray(result.data) && result.data.some(v => v.token === 'PAYROLL_DAYS')
      if (valid) { if (catalogueCache.size > 30) catalogueCache.clear(); catalogueCache.set(key, result.data) }
      setState({ key, values: valid ? result.data : [], error: valid ? '' : result.error || 'Engine input catalogue is unavailable. Deploy the API and retry.', loading: false })
    })
    return () => { cancelled = true }
  }, [key, enabled, retry])
  return { values: state.key === key ? state.values : [], error: state.key === key ? state.error : '', loading: enabled && (state.key !== key || state.loading), reload: () => { catalogueCache.delete(key); setRetry(n => n + 1) } }
}
export const valueOptions = (values: PayrollValue[]) => values.map(v => ({ value: v.token, label: `${v.token} - ${v.meaning}`, disabled: v.availability.startsWith('Missing from') }))
export const valueHints = (values: PayrollValue[]) => values.map(v => ({ label: v.token, meaning: `${v.meaning} Source: ${v.source}. ${v.availability}.`, example: v.example }))
export const simulationEmployees = (clientId: number, search: string) => getJsonResult(`/api/payroll-calculation/employees?clientId=${clientId}&search=${encodeURIComponent(search)}`, [] as SimulationEmployee[], { toast: false, loader: false })
export type SourceSalaryScenario = { mode: 'Template'; clientId: number; templateId: string; snapshot: ReviewSnapshot; payrollDays: number; presentDays: number; payableDays: number; annualCtc: number; amountOverrides: Record<string, number> }
export function simulateSalary(args: { mode: 'Template' | 'Employee'; clientId: number; employeeId?: number; template: Structure; components: Component[]; snapshot?: ReviewSnapshot; payPeriod: string; payrollDays: number | null; presentDays: number | null; payableDays: number | null; annualCtc: number | null; amountOverrides: Record<string, number> }) {
  return postJson('/api/payroll-calculation/simulate', { ...args, template: undefined, components: undefined, templateId: String(args.template.id || ''), snapshot: args.mode === 'Template' ? args.snapshot ?? reviewSnapshot(args.components, undefined, args.template, args.clientId) : undefined }, null as SimulationResult | null, { toast: false, loader: false, timeoutMs: 90000 })
}
