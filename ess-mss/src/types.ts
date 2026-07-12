export type User = {
  email: string
  displayName: string
  roles: string[]
  permissions: string[]
  employeeId?: number
  clientId?: number
}

export type OrganizationBrand = { name: string; logoDataUrl: string }

export type View = 'Dashboard' | 'My Profile' | 'Leave' | 'Travel' | 'Expense' | 'Attendance' | 'Pay' | 'Tax' | 'My Tasks' | 'Team' | 'Approvals'
export type LoadState = 'loading' | 'ready' | 'error'

export type Task = { id: number; instanceId: number; resourceType: string; resourceId: string; stageName: string; payloadJson: string; status?: string; comment?: string; createdAt: string; actionedAt?: string }
export type ProfileData = { employeeCode: string; firstName: string; lastName: string; workEmail: string; department: string; designation: string; dateOfJoining: string; workLocation: string; reportingManager: string }
export type LeaveBalance = { leaveCode: string; leaveType: string; balance: number; balanceDate: string; allowHalfDay: boolean }
export type LeaveRequest = { id: number; leaveCode: string; leaveType: string; fromDate: string; toDate: string; dayType: string; days: number; reason: string; status: string; createdAt: string }
export type TravelCity = { fromLocation: string; toLocation: string; travelMode: string; travelClass: string; remarks: string; startDateTime?: string; endDateTime?: string }
export type TravelAccommodation = { city: string; checkInDateTime?: string; checkOutDateTime?: string; occupancy: string; roomPreference: string; remarks: string }
export type LocalTravelDetail = { city: string; travelDateTime?: string; fromLocation: string; toLocation: string; travelMode: string; remarks: string }
export type TravelRequest = { id: number; requestNumber: string; requestDate: string; employeeName: string; department: string; designation: string; reportingManager: string; purpose: string; customer: string; project: string; costCenter: string; travelScope: 'Domestic' | 'International'; travelType: string; priority: string; fromLocation: string; toLocation: string; legs: TravelCity[]; accommodationDetails: TravelAccommodation[]; localTravelDetails: LocalTravelDetail[]; startDateTime: string; endDateTime: string; estimatedCost: number; policyId?: number; policyName: string; travelMode: string; accommodationRequired: boolean; localConveyanceRequired: boolean; advanceRequired: boolean; advanceAmount: number; remarks: string; status: string; policyValidationJson: string; cancellationReason: string; cancellationDate?: string; cancellationStatus: string; createdAt: string; updatedAt: string }
export type SaveTravelRequest = { id: number; purpose: string; customer: string; project: string; costCenter: string; travelScope: 'Domestic' | 'International'; travelType: string; priority: string; fromLocation: string; toLocation: string; cities: TravelCity[]; accommodationDetails: TravelAccommodation[]; localTravelDetails: LocalTravelDetail[]; startDateTime: string; endDateTime: string; estimatedCost: number; travelMode: string; accommodationRequired: boolean; localConveyanceRequired: boolean; advanceRequired: boolean; advanceAmount: number; remarks: string }
export type TravelOptions = { policyId?: number; policyName: string; clientName: string; travelModes: string[]; localTravelModes: string[]; travelTypes: string[]; priorities: string[]; locations: string[]; travelClasses: string[]; showTripDetails: boolean; showAccommodationDetails: boolean; showLocalTravelDetails: boolean; validationMessages: string[] }
export type TravelDashboard = { draftRequests: number; pendingApproval: number; approved: number; rejected: number; upcomingTravel: number; cancelledTrips: number }
export type ExpenseLine = { id?: number; claimId?: number; expenseDate: string; categoryId: number; categoryCode: string; categoryName: string; subCategory: string; vendorName: string; billNumber: string; invoiceNumber: string; amount: number; currency: string; exchangeRate: number; gstAmount: number; approvedAmount: number; costCenter: string; project: string; customer: string; location: string; paymentMethod: string; receiptAttached: boolean; receiptFileName: string; description: string; status: string; validationJson?: string }
export type ExpenseClaim = { id: number; claimNumber: string; claimDate: string; employeeName: string; department: string; designation: string; travelRequestId?: number; travelRequestNumber: string; expenseType: string; purpose: string; customer: string; project: string; costCenter: string; currency: string; totalClaimAmount: number; totalApprovedAmount: number; totalGstAmount: number; payrollStatus: string; payrollRunId?: number; reimbursementComponentCode: string; status: string; policyValidationJson: string; remarks: string; submittedAt?: string; createdAt: string; updatedAt: string; lines: ExpenseLine[] }
export type SaveExpenseClaim = { id: number; travelRequestId?: number; expenseType: string; purpose: string; customer: string; project: string; costCenter: string; currency: string; remarks: string; lines: ExpenseLine[] }
export type ExpenseCategoryOption = { id: number; clientId: number; parentId?: number; parentName: string; expenseType: string; isClaimHeader: boolean; categoryCode: string; categoryName: string; receiptMandatory: boolean; gstApplicable: boolean; dailyLimit?: number; maximumClaim?: number; requiresFinanceApproval: boolean; requiresManagerApproval: boolean }
export type ExpenseTravelOption = { id: number; requestNumber: string; purpose: string; customer: string; project: string; costCenter: string; startDateTime: string; endDateTime: string; travelMode: string; accommodationRequired: boolean; localConveyanceRequired: boolean }
export type ExpenseOptions = { clientName: string; policyId?: number; policyName: string; headers: ExpenseCategoryOption[]; categories: ExpenseCategoryOption[]; travelRequests: ExpenseTravelOption[]; currencies: string[]; locations: string[]; paymentMethods: string[]; validationMessages: string[] }
export type ExpenseDashboard = { draftClaims: number; pendingApproval: number; approved: number; rejected: number; pendingPayroll: number; approvedAmount: number }
export type WorkflowTrail = { instanceId?: number; workflowCode: string; workflowName: string; resourceType: string; matchScope: string; status: string; createdAt?: string; completedAt?: string; events: WorkflowTrailItem[] }
export type WorkflowTrailItem = { stageName: string; action: string; actor: string; comment: string; createdAt: string; isPending: boolean }
export type DailyAttendance = { attendanceDate: string; status: string; payableValue: number; remarks: string }
export type AttendanceSummary = { presentDays: number; payableDays: number; totalWorkingDays: number }
export type Holiday = { name: string; startDate: string; endDate: string }
export type Birthday = { name: string; department: string }
export type CalendarDayInfo = { date: string; status: string; label: string; canApply: boolean; leave?: { leaveType: string; status: string }; holiday?: { name: string } }
export type Payslip = { payRunId: number; payPeriod: string; payDate: string; runStatus: string; grossPay: number; statutoryDeductions: number; oneTimeDeductions: number; netPay: number; paymentStatus: string; paymentDate?: string }
export type PayslipDocument = { payRunId: number; payPeriod: string; employeeCode: string; fileName: string; html: string }
export type TaxPortal = { financialYear: string; enabled: boolean; defaultRegime: 'Old' | 'New'; selectedRegime?: 'Old' | 'New'; regimeStatus: string; canSelectRegime: boolean; canDeclare: boolean; canSubmitPlanned: boolean; canSubmitActual: boolean; regimeSelectionWindowOpen: boolean; plannedDeclarationWindowOpen: boolean; actualDeclarationWindowOpen: boolean; declarationRequired: boolean; declarationPhase: 'Planned' | 'Actual' | 'Closed' | 'NotRequired'; requiresApproval: boolean; regimeSelectionCutoff?: string; declarationWindowStart?: string; declarationWindowEnd?: string; plannedDeclarationStart?: string; plannedDeclarationEnd?: string; actualDeclarationStart?: string; actualDeclarationEnd?: string; poiProcessingMonth?: string; message: string; sections: TaxDeclarationSection[]; finalAdjustments: TaxFinalAdjustmentInfo[] }
export type TaxDeclarationSection = { declarationId?: number; sectionId: number; code: string; name: string; regime: 'Old' | 'New' | 'Both'; limitAmount?: number; proofRequired: boolean; requiresApproval: boolean; declaredAmount: number; plannedAmount: number; actualAmount: number; approvedAmount?: number; status: string; remarks: string }
export type TaxFinalAdjustmentInfo = { label: string; valueType: 'Percent' | 'Fixed'; value: number }
