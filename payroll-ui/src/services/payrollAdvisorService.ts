import type { Component, Structure } from '../types/payroll'
import { getJsonResult, postJson } from './apiClient'

export type ReviewComponent = Pick<Component, 'code' | 'category' | 'componentRole' | 'statutoryType' | 'calculationType' | 'value' | 'formula' | 'baseComponent' | 'proRata' | 'taxable' | 'active' | 'payType'> & { id: string; priority: number }
export type ReviewSnapshot = { mode: 'Component' | 'Template'; clientId: number; focusCode: string; annualCtc: number; components: ReviewComponent[]; lines: { componentId: string; value: string; formula: string; calculationType: string; baseComponent: string; proRataOverride: string }[] }
export type ReviewFinding = { id: string; source: string; severity: string; code: string; field: string; title: string; explanation: string; example: string; suggestion: string; canDisableProration: boolean }
export type ReviewScenario = { label: string; payrollDays: number; payableDays: number; error?: string; amounts: { code: string; category: string; fullPeriod: number; earned: number }[] }
export type ReviewReport = { status: string; coverage: string; findings: ReviewFinding[]; scenarios: ReviewScenario[]; model?: string; provider?: string }
export type ReviewJob = { id: string; clientId: number; snapshotHash: string; status: 'Queued' | 'Processing' | 'NeedsReview' | 'Failed'; error?: string; createdAt: string; snapshot: ReviewSnapshot; report?: ReviewReport }
const project = (c: Component): ReviewComponent => ({ id: String(c.id || 'draft'), code: c.code, category: c.category, componentRole: c.componentRole || '', statutoryType: c.statutoryType || 'None', calculationType: c.calculationType, value: c.value || '', formula: c.formula || '', baseComponent: c.baseComponent || '', proRata: c.proRata ?? true, taxable: c.taxable ?? true, active: c.active ?? true, priority: String(c.priority ?? '').trim() !== '' && Number.isFinite(Number(c.priority)) ? Number(c.priority) : 999, payType: c.payType || 'Fixed Pay' })
export function reviewSnapshot(components: Component[], component?: Component, template?: Structure, clientId = 0): ReviewSnapshot {
  const selected = component ? [component] : components.filter(c => template?.lines.some(line => String(line.componentId) === String(c.id)))
  return { mode: component ? 'Component' : 'Template', clientId, focusCode: component?.code || '', annualCtc: Number(template?.annualCtc) || 0, components: selected.map(project), lines: (template?.lines || []).map(l => ({ componentId: String(l.componentId), value: l.value || '', formula: l.formula || '', calculationType: l.calculationType || '', baseComponent: l.baseComponent || '', proRataOverride: l.proRataOverride || '' })) }
}
export const snapshotKey = (s: ReviewSnapshot): string => JSON.stringify(s)
export const queuePayrollReview = (snapshot: ReviewSnapshot, consent: boolean) => postJson('/api/payroll-advisor/reviews', { snapshot, consent }, null as ReviewJob | null, { toast: false, loader: false })
export const getPayrollReview = (id: string) => getJsonResult(`/api/payroll-advisor/reviews/${encodeURIComponent(id)}`, null as ReviewJob | null, { toast: false, loader: false })
export const getPayrollReviewHistory = (clientId: number) => getJsonResult(`/api/payroll-advisor/reviews?clientId=${clientId}`, [] as ReviewJob[], { toast: false, loader: false })
export const recordPayrollReviewDecision = (job: ReviewJob, snapshot: ReviewSnapshot, findingId: string, decision: 'ApplyToDraft' | 'KeepExisting') => postJson(`/api/payroll-advisor/reviews/${job.id}/decisions`, { snapshot, findingId, decision }, null, { toast: false, loader: false })
