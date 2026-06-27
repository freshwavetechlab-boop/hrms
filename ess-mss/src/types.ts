export type User = {
  email: string
  displayName: string
  roles: string[]
  permissions: string[]
  employeeId?: number
  clientId?: number
}

export type View = 'Dashboard' | 'My Profile' | 'Leave' | 'Attendance' | 'Pay' | 'Tax' | 'My Tasks' | 'Team' | 'Approvals'
export type LoadState = 'loading' | 'ready' | 'error'

export type Task = { id: number; resourceType: string; resourceId: string; stageName: string; createdAt: string }
export type ProfileData = { employeeCode: string; firstName: string; lastName: string; workEmail: string; department: string; designation: string; dateOfJoining: string; workLocation: string; reportingManager: string }
export type LeaveBalance = { leaveCode: string; leaveType: string; balance: number; balanceDate: string }
export type LeaveRequest = { id: number; leaveCode: string; leaveType: string; fromDate: string; toDate: string; days: number; reason: string; status: string; createdAt: string }
export type WorkflowTrail = { instanceId?: number; workflowCode: string; workflowName: string; resourceType: string; matchScope: string; status: string; createdAt?: string; completedAt?: string; events: WorkflowTrailItem[] }
export type WorkflowTrailItem = { stageName: string; action: string; actor: string; comment: string; createdAt: string; isPending: boolean }
export type DailyAttendance = { attendanceDate: string; status: string; payableValue: number; remarks: string }
export type AttendanceSummary = { presentDays: number; payableDays: number; totalWorkingDays: number }
export type Holiday = { name: string; startDate: string; endDate: string }
export type Birthday = { name: string; department: string }
export type CalendarDayInfo = { date: string; status: string; label: string; canApply: boolean; leave?: { leaveType: string; status: string }; holiday?: { name: string } }
export type Payslip = { payRunId: number; payPeriod: string; payDate: string; runStatus: string; grossPay: number; statutoryDeductions: number; oneTimeDeductions: number; netPay: number; paymentStatus: string; paymentDate?: string }
export type TaxPortal = { financialYear: string; enabled: boolean; defaultRegime: 'Old' | 'New'; selectedRegime?: 'Old' | 'New'; regimeStatus: string; canSelectRegime: boolean; canDeclare: boolean; canSubmitPlanned: boolean; canSubmitActual: boolean; regimeSelectionWindowOpen: boolean; plannedDeclarationWindowOpen: boolean; actualDeclarationWindowOpen: boolean; declarationRequired: boolean; declarationPhase: 'Planned' | 'Actual' | 'Closed' | 'NotRequired'; requiresApproval: boolean; regimeSelectionCutoff?: string; declarationWindowStart?: string; declarationWindowEnd?: string; plannedDeclarationStart?: string; plannedDeclarationEnd?: string; actualDeclarationStart?: string; actualDeclarationEnd?: string; poiProcessingMonth?: string; message: string; sections: TaxDeclarationSection[]; finalAdjustments: TaxFinalAdjustmentInfo[] }
export type TaxDeclarationSection = { declarationId?: number; sectionId: number; code: string; name: string; regime: 'Old' | 'New' | 'Both'; limitAmount?: number; proofRequired: boolean; requiresApproval: boolean; declaredAmount: number; plannedAmount: number; actualAmount: number; approvedAmount?: number; status: string; remarks: string }
export type TaxFinalAdjustmentInfo = { label: string; valueType: 'Percent' | 'Fixed'; value: number }
