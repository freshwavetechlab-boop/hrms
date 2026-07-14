import type { DashboardSnapshot } from '../types/payroll'
import { getJson } from './apiClient'

const fallbackDashboard: DashboardSnapshot = {
  month: '',
  selectedClientId: 0,
  sections: [],
  clients: [],
  metrics: {
    activeEmployees: 0,
    portalUsers: 0,
    currentMonthPayRuns: 0,
    currentMonthNetPay: 0,
    attendanceRecorded: 0,
    attendanceMissing: 0,
    attendanceIssues: 0,
    pendingTasks: 0,
    pendingLeaveRequests: 0,
    payrollExceptions: 0
  },
  payRunStatuses: [],
  recentPayRuns: [],
  departmentHeadcount: [],
  locationHeadcount: [],
  payrollTrend: [],
  attendanceMix: [],
  attendancePayability: [],
  approvalStageBreakup: [],
  approvalActionMix: [],
  designationHeadcount: [],
  gradeHeadcount: [],
  genderHeadcount: [],
  essAdoption: [],
  payrollPaymentStatus: [],
  payrollRunType: [],
  payrollCostBreakup: { grossEarnings: 0, statutoryDeductions: 0, otherDeductions: 0, netPay: 0 },
  attendanceDailyStatus: [],
  attendanceSourceType: [],
  approvalResourceBreakup: [],
  approvalAging: []
}

export const getDashboard = (clientId = 0) => getJson<DashboardSnapshot>(`/api/dashboard?clientId=${clientId}`, fallbackDashboard)
