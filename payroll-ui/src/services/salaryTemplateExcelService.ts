import { postJson } from './apiClient'
import type { ExcelPayslipCalculationSource } from './excelPayslipService'
import type { ReviewSnapshot } from './payrollAdvisorService'
import type { SimulationResult } from './payrollCalculationService'

export type SalaryTemplateExcelRole = 'earning' | 'deduction' | 'employer' | 'input' | 'payrollDays' | 'presentDays' | 'payableDays' | 'monthlyCtc' | 'annualCtc' | 'netPay' | 'grossTotal' | 'deductionTotal' | 'ignore'
export type SalaryTemplateExcelColumn = { columnIndex: number; code: string; name: string; kind: SalaryTemplateExcelRole; existingComponentId?: string; reviewedFixedValue?: boolean; inputMode?: 'monthly' | 'period' }
export type SalaryTemplateExcelExtractionRequest = { clientId: number; templateName: string; month: string; source: ExcelPayslipCalculationSource; sampleRow: number; selectedColumns: SalaryTemplateExcelColumn[]; fixedPayrollDays?: number; payrollDaysCell?: string }
export type SalaryTemplateExcelCandidate = { id: string; existingComponentId?: string | null; columnIndex: number; inputColumnIndex?: number | null; code: string; name: string; category: 'Earning' | 'Deduction' | 'Benefit'; componentRole: string; statutoryType: string; calculationType: 'Formula' | 'Fixed Amount'; value: string; formula: string; baseComponent: string; proRata: boolean; taxable: boolean; active: boolean; priority: number; payType: string; requiresEmployeeInput: boolean }
export type SalaryTemplateExcelScenario = { mode: 'Template'; clientId: number; templateId: string; payPeriod: string; snapshot: ReviewSnapshot; payrollDays: number; presentDays: number; payableDays: number; annualCtc: number; amountOverrides: Record<string, number> }
export type SalaryTemplateExcelExtractionResult = { canCreateDraft: boolean; templateName: string; components: SalaryTemplateExcelCandidate[]; lines: ReviewSnapshot['lines']; issues: { severity: string; code: string; columnIndex?: number | null; message: string }[]; comparisons: { columnIndex: number; code: string; sourceAmount: number | null; calculatedAmount: number | null; variance: number | null; status: string }[]; scenario: SalaryTemplateExcelScenario | null; simulation: SimulationResult | null }

export const extractSalaryTemplateExcel = (request: SalaryTemplateExcelExtractionRequest) => postJson('/api/payroll-calculation/extract-template', request, null as SalaryTemplateExcelExtractionResult | null, { toast: false, loader: false, timeoutMs: 90000 })
export const simulateSalaryTemplateExcelScenario = (scenario: SalaryTemplateExcelScenario) => postJson('/api/payroll-calculation/simulate', scenario, null as SimulationResult | null, { toast: false, loader: false, timeoutMs: 90000 })
