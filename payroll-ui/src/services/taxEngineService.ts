import type { ClientTaxSetting, TaxDeclarationSection, TaxFinalAdjustment, TaxSlab, TaxSurcharge } from '../types/payroll'
import { deleteJson, getJson, postJson } from './apiClient'

export type TaxEngineSetup = {
  financialYears: unknown[]
  ruleVersions: unknown[]
  regimes: unknown[]
  clientSettings: ClientTaxSetting[]
  slabs: TaxSlab[]
  surcharges: TaxSurcharge[]
  finalAdjustments: TaxFinalAdjustment[]
  declarationSections: TaxDeclarationSection[]
  deductionOptions: unknown[]
  standardDeductions: unknown[]
  rebateRules: unknown[]
  exemptionRules: unknown[]
  hraRules: unknown[]
  sourceReferences: unknown[]
  auditLogs: unknown[]
}

const empty: TaxEngineSetup = { financialYears: [], ruleVersions: [], regimes: [], clientSettings: [], slabs: [], surcharges: [], finalAdjustments: [], declarationSections: [], deductionOptions: [], standardDeductions: [], rebateRules: [], exemptionRules: [], hraRules: [], sourceReferences: [], auditLogs: [] }
const fy = `${new Date().getFullYear()}-${String(new Date().getFullYear() + 1).slice(2)}`
const dateOnly = (value?: string | null) => String(value || '').slice(0, 10)
const text = (value: unknown) => value === null || value === undefined ? '' : String(value)
const toApiClient = (row: ClientTaxSetting) => ({ ...row, clientId: Number(String(row.clientId).split(':')[0] || 0) })
const fromApiClient = (row: ClientTaxSetting & { clientName?: string }) => ({ ...row, regimeSelectionWindowOpen: Boolean(row.regimeSelectionWindowOpen), plannedDeclarationWindowOpen: Boolean(row.plannedDeclarationWindowOpen), actualDeclarationWindowOpen: Boolean(row.actualDeclarationWindowOpen), reminderEmailsEnabled: Boolean(row.reminderEmailsEnabled), reminderFrequency: row.reminderFrequency || 'Weekly', reminderBeforeLockDays: row.reminderBeforeLockDays ?? 7, regimeSelectionCutoff: dateOnly(row.regimeSelectionCutoff), declarationWindowStart: dateOnly(row.declarationWindowStart), declarationWindowEnd: dateOnly(row.declarationWindowEnd), plannedDeclarationStart: dateOnly(row.plannedDeclarationStart || row.declarationWindowStart), plannedDeclarationEnd: dateOnly(row.plannedDeclarationEnd || row.declarationWindowEnd), actualDeclarationStart: dateOnly(row.actualDeclarationStart), actualDeclarationEnd: dateOnly(row.actualDeclarationEnd), poiProcessingMonth: dateOnly(row.poiProcessingMonth).slice(0, 7), clientId: `${row.clientId}:${row.clientName || row.clientId}` })
const fromApiSlab = (row: TaxSlab) => ({ ...row, financialYear: row.financialYear || fy, incomeFrom: text(row.incomeFrom), incomeTo: text(row.incomeTo), ratePercent: text(row.ratePercent), effectiveFrom: dateOnly(row.effectiveFrom) })
const fromApiSurcharge = (row: TaxSurcharge) => ({ ...row, financialYear: row.financialYear || fy, incomeFrom: text(row.incomeFrom), incomeTo: text(row.incomeTo), surchargePercent: text(row.surchargePercent) })
const fromApiFinalAdjustment = (row: TaxFinalAdjustment) => ({ ...row, financialYear: row.financialYear || fy, valueType: row.valueType || 'Percent', value: text(row.value), applyOrder: text(row.applyOrder || '100') })
const fromApiSection = (row: TaxDeclarationSection) => ({ ...row, financialYear: row.financialYear || fy, limitAmount: text(row.limitAmount) })

export const getTaxEngineSetup = async () => {
  const data = await getJson<TaxEngineSetup>('/api/tax-engine', empty)
  return { ...data, clientSettings: data.clientSettings.map(fromApiClient), slabs: data.slabs.map(fromApiSlab), surcharges: (data.surcharges ?? []).map(fromApiSurcharge), finalAdjustments: (data.finalAdjustments ?? []).map(fromApiFinalAdjustment), declarationSections: data.declarationSections.map(fromApiSection) }
}
export const saveTaxClientSetting = async (row: ClientTaxSetting) => {
  const result = await postJson('/api/tax-engine/client-settings', toApiClient(row), row)
  return { ...result, data: fromApiClient(result.data as ClientTaxSetting & { clientName?: string }) }
}
export const saveTaxSlab = (row: TaxSlab) => postJson('/api/tax-engine/slabs', row, row)
export const saveTaxSurcharge = (row: TaxSurcharge) => postJson('/api/tax-engine/surcharges', row, row)
export const saveTaxFinalAdjustment = (row: TaxFinalAdjustment) => postJson('/api/tax-engine/final-adjustments', row, row)
export const saveTaxDeclarationSection = (row: TaxDeclarationSection) => postJson('/api/tax-engine/sections', row, row)
export const deleteTaxEngineRow = (kind: 'client-settings' | 'slabs' | 'surcharges' | 'final-adjustments' | 'sections', id: number) => deleteJson(`/api/tax-engine/${kind}/${id}`, null)
