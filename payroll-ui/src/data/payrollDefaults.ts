import type { Client, Component, Drop, Employee, Org, PayslipTemplate, Setup, Structure, TaxDeclarationSection, TaxFinalAdjustment, TaxSlab, TaxSurcharge, WorkLocation } from '../types/payroll'

export const org0: Org = { name: '', legalName: '', businessType: '', businessLocation: 'India', industry: '', hasRunPayrollThisYear: false, setupCompleted: false, logoDataUrl: '', addressLine1: '', addressLine2: '', city: '', state: '', postalCode: '', country: 'India' }
export const demoComponents: Component[] = [
  { id: 101, code: 'BASIC', componentType: 'Basic', category: 'Earning', name: 'Basic', payType: 'Fixed Pay', calculationType: 'Percentage of CTC', value: '40', formula: 'CTC * 40%', baseComponent: 'CTC', taxable: true, ctc: true, proRata: true, fbp: false, restrictFbp: false, epf: 'Always', esi: true, recurring: true, scheduled: false, investmentType: '', correctionOf: '', active: true, priority: '10' },
  { id: 102, code: 'HRA', componentType: 'House Rent Allowance', category: 'Earning', name: 'House Rent Allowance', payType: 'Fixed Pay', calculationType: 'Formula', value: '', formula: 'BASIC * 50%', baseComponent: 'BASIC', taxable: true, ctc: true, proRata: true, fbp: false, restrictFbp: false, epf: 'Never', esi: true, recurring: true, scheduled: false, investmentType: '', correctionOf: '', active: true, priority: '20' },
  { id: 103, code: 'SPAL', componentType: 'Custom Allowance', category: 'Earning', name: 'Special Allowance', payType: 'Fixed Pay', calculationType: 'Balancing Amount', value: '', formula: 'CTC - SUM(Fixed Earnings)', baseComponent: 'CTC', taxable: true, ctc: true, proRata: true, fbp: false, restrictFbp: false, epf: 'Never', esi: true, recurring: true, scheduled: false, investmentType: '', correctionOf: '', active: true, priority: '90' },
  { id: 104, code: 'PF', componentType: 'Provident Fund', category: 'Deduction', name: 'Provident Fund', payType: 'Fixed Pay', calculationType: 'Formula', value: '', formula: 'MIN(BASIC, 15000) * 12%', baseComponent: 'BASIC', taxable: false, ctc: true, proRata: true, fbp: false, restrictFbp: false, epf: 'Never', esi: false, recurring: true, scheduled: false, investmentType: '', correctionOf: '', active: true, priority: '110' }
]
export const demoStructures: Structure[] = [{ id: 201, clientId: '1:Acme Technologies', name: 'Acme Default CTC', annualCtc: '900000', lines: [{ componentId: '101', value: '40% of CTC' }, { componentId: '102', value: '50% of BASIC' }, { componentId: '103', value: 'Balance' }, { componentId: '104', value: 'MIN(BASIC,15000)*12%' }], active: true }]
const currentFy = `${new Date().getFullYear()}-${String(new Date().getFullYear() + 1).slice(2)}`
export const defaultTaxSlabs: TaxSlab[] = [{ id: 1, financialYear: currentFy, regime: 'New', incomeFrom: '0', incomeTo: '', ratePercent: '0', effectiveFrom: new Date().toISOString().slice(0, 10), active: true }]
export const defaultTaxSurcharges: TaxSurcharge[] = [
  { id: 1, financialYear: currentFy, incomeFrom: '0', incomeTo: '5000000', surchargePercent: '0', active: true },
  { id: 2, financialYear: currentFy, incomeFrom: '5000000', incomeTo: '10000000', surchargePercent: '10', active: true },
  { id: 3, financialYear: currentFy, incomeFrom: '10000000', incomeTo: '20000000', surchargePercent: '15', active: true },
  { id: 4, financialYear: currentFy, incomeFrom: '20000000', incomeTo: '50000000', surchargePercent: '25', active: true },
  { id: 5, financialYear: currentFy, incomeFrom: '50000000', incomeTo: '', surchargePercent: '37', active: true }
]
export const defaultTaxFinalAdjustments: TaxFinalAdjustment[] = [{ id: 1, financialYear: currentFy, label: 'Health & Education Cess', valueType: 'Percent', value: '4', applyOrder: '100', active: true }]
export const defaultTaxSections: TaxDeclarationSection[] = [
  { id: 1, financialYear: currentFy, code: '80C', name: 'Section 80C investments', regime: 'Old', limitAmount: '150000', proofRequired: true, requiresApproval: true, active: true },
  { id: 2, financialYear: currentFy, code: '80CCD1B', name: 'NPS employee contribution', regime: 'Old', limitAmount: '50000', proofRequired: true, requiresApproval: true, active: true },
  { id: 3, financialYear: currentFy, code: '80D', name: 'Medical insurance premium', regime: 'Old', limitAmount: '25000', proofRequired: true, requiresApproval: true, active: true },
  { id: 4, financialYear: currentFy, code: 'HRA', name: 'House rent allowance', regime: 'Old', limitAmount: '', proofRequired: true, requiresApproval: true, active: true },
  { id: 5, financialYear: currentFy, code: '24B', name: 'Home loan interest', regime: 'Old', limitAmount: '200000', proofRequired: true, requiresApproval: true, active: true },
  { id: 6, financialYear: currentFy, code: '80E', name: 'Education loan interest', regime: 'Old', limitAmount: '', proofRequired: true, requiresApproval: true, active: true },
  { id: 7, financialYear: currentFy, code: '80G', name: 'Donations', regime: 'Old', limitAmount: '', proofRequired: true, requiresApproval: true, active: true },
  { id: 8, financialYear: currentFy, code: 'OTHER', name: 'Other exemptions and deductions', regime: 'Old', limitAmount: '', proofRequired: true, requiresApproval: true, active: true }
]
export const setup0: Setup = { tax: { pan: '', tan: '', aoCode: '', frequency: 'Monthly', clientSettings: [], slabs: defaultTaxSlabs, surcharges: defaultTaxSurcharges, finalAdjustments: defaultTaxFinalAdjustments, declarationSections: defaultTaxSections }, schedule: { workWeek: 'Monday - Friday', salaryDays: 'Actual days', fixedDays: '30', payDay: 'Last working day', firstPayPeriod: new Date().toISOString().slice(0, 7) }, statutory: { epf: true, epfNumber: '', epfCtc: true, abry: false, epfContribution: 'Both Employee and Employer', restrictPf: true, esi: false, esiNumber: '', pt: false, ptNumber: '', ptState: '', ptCycle: 'Monthly', ptSlabs: 'Up to 15000: 0\n15001 and above: 200', ptStateSlabs: [{ id: 1, state: 'Karnataka', salaryFrom: '15001', salaryTo: '', deductionAmount: '200', effectiveFrom: new Date().toISOString().slice(0, 10), effectiveTo: '', gender: 'All', notes: 'Default monthly slab', active: true }], lwf: false, lwfState: '', lwfCycle: 'Half-yearly', lwfEligibilityLimit: '15000', lwfEmployeeContribution: '', lwfEmployerContribution: '' }, salaryComponents: demoComponents, salaryStructures: demoStructures, payslipTemplates: [] }
export const component0: Component = { id: 0, code: '', componentType: 'Custom Allowance', category: 'Earning', name: '', payType: 'Fixed Pay', calculationType: 'Flat Amount', value: '', formula: '', baseComponent: '', taxable: true, ctc: true, proRata: true, fbp: false, restrictFbp: false, epf: 'Never', esi: false, recurring: true, scheduled: false, investmentType: '', correctionOf: '', active: true, priority: '100' }
export const structure0: Structure = { id: 0, clientId: '', name: '', annualCtc: '', lines: [], active: true }
export const payslip0: PayslipTemplate = { id: 0, clientId: '', name: 'Standard Payslip', theme: 'Classic', showLogo: true, showClient: true, showYtd: true, showBank: true, note: 'This is a system generated payslip.', active: true }
export const employee0: Employee = { id: 0, clientId: 0, employeeCode: '', firstName: '', lastName: '', gender: '', dateOfJoining: '', workEmail: '', department: '', designation: '', workLocationId: 0, reportingManagerId: 0, portalAccess: false, salaryStructureId: '', annualCtc: 0, salaryJson: '{}', personalJson: '{}', paymentJson: '{}', isActive: true }
export const client0: Client = { id: 0, name: '', code: '', contactPerson: '', email: '', phone: '', address: '', payScheduleJson: '{}', isActive: true }
export const location0: WorkLocation = { id: 0, name: '', address: '', city: '', state: '', postalCode: '', isPrimary: false, isActive: true }
export const drop0: Drop = { id: 0, type: 'Department', value: '', isActive: true }
export const settingsMenus = ['Organization', 'Clients', 'Work Locations', 'Dropdown Masters', 'Pay Schedule', 'Tax Engine', 'Statutory Setup', 'Salary Components', 'Salary Templates', 'Payslip Templates'] as const
export const securityMenus = ['Users', 'Roles', 'Audit'] as const
export const leaveAttendanceMenus = ['Preferences', 'Leave Types', 'Holiday', 'Attendance', 'Geo-Fencing', 'Import Balance'] as const
export const reportingMenus = ['Payroll Reports', 'Employee Reports', 'Attendance Reports', 'Leave Reports', 'Recruitment Reports', 'Onboarding Reports', 'Separation Reports', 'Compliance Reports', 'Tax Reports', 'Loan & Advance Reports', 'Cost Center Reports', 'Department Reports', 'Location Reports', 'Contractor Reports', 'Audit Reports', 'MIS Reports', 'Executive Dashboards', 'Scheduled Reports', 'Report Builder'] as const
export const workflowMenus = ['Workflow Setup', 'Department Head Assignments', 'My Tasks', 'Workflow History'] as const
export const dropTypes = ['Department', 'Designation', 'Employment Type', 'Employee Grade', 'Cost Center', 'Location Tag']
export const states = [
  'Andaman and Nicobar Islands', 'Andhra Pradesh', 'Arunachal Pradesh', 'Assam', 'Bihar', 'Chandigarh', 'Chhattisgarh',
  'Dadra and Nagar Haveli and Daman and Diu', 'Delhi', 'Goa', 'Gujarat', 'Haryana', 'Himachal Pradesh',
  'Jammu and Kashmir', 'Jharkhand', 'Karnataka', 'Kerala', 'Ladakh', 'Lakshadweep', 'Madhya Pradesh',
  'Maharashtra', 'Manipur', 'Meghalaya', 'Mizoram', 'Nagaland', 'Odisha', 'Puducherry', 'Punjab', 'Rajasthan',
  'Sikkim', 'Tamil Nadu', 'Telangana', 'Tripura', 'Uttar Pradesh', 'Uttarakhand', 'West Bengal'
]
export const stateCities: Record<string, string[]> = {
  'Andaman and Nicobar Islands': ['Port Blair', 'Mayabunder', 'Diglipur'],
  'Andhra Pradesh': ['Visakhapatnam', 'Vijayawada', 'Guntur', 'Nellore', 'Kurnool', 'Tirupati', 'Kakinada', 'Rajahmundry', 'Anantapur', 'Kadapa'],
  'Arunachal Pradesh': ['Itanagar', 'Naharlagun', 'Tawang', 'Pasighat', 'Ziro', 'Bomdila'],
  Assam: ['Guwahati', 'Dibrugarh', 'Silchar', 'Jorhat', 'Tezpur', 'Nagaon', 'Tinsukia'],
  Bihar: ['Patna', 'Gaya', 'Bhagalpur', 'Muzaffarpur', 'Purnia', 'Darbhanga', 'Begusarai'],
  Chandigarh: ['Chandigarh'],
  Chhattisgarh: ['Raipur', 'Bilaspur', 'Durg', 'Bhilai', 'Korba', 'Raigarh', 'Jagdalpur'],
  'Dadra and Nagar Haveli and Daman and Diu': ['Silvassa', 'Daman', 'Diu'],
  Delhi: ['Delhi', 'New Delhi'],
  Goa: ['Panaji', 'Margao', 'Vasco da Gama', 'Mapusa', 'Ponda'],
  Gujarat: ['Ahmedabad', 'Surat', 'Vadodara', 'Rajkot', 'Gandhinagar', 'Bhavnagar', 'Jamnagar', 'Junagadh', 'Anand', 'Bharuch'],
  Haryana: ['Gurugram', 'Faridabad', 'Panipat', 'Ambala', 'Hisar', 'Karnal', 'Rohtak', 'Sonipat'],
  'Himachal Pradesh': ['Shimla', 'Dharamshala', 'Solan', 'Mandi', 'Kullu', 'Una'],
  'Jammu and Kashmir': ['Srinagar', 'Jammu', 'Anantnag', 'Baramulla', 'Udhampur', 'Kathua'],
  Jharkhand: ['Ranchi', 'Jamshedpur', 'Dhanbad', 'Bokaro', 'Deoghar', 'Hazaribagh'],
  Karnataka: ['Bengaluru', 'Mysuru', 'Mangaluru', 'Hubballi', 'Belagavi', 'Kalaburagi', 'Davanagere', 'Shivamogga', 'Udupi', 'Ballari'],
  Kerala: ['Thiruvananthapuram', 'Kochi', 'Kozhikode', 'Thrissur', 'Kollam', 'Alappuzha', 'Kannur', 'Kottayam', 'Palakkad'],
  Ladakh: ['Leh', 'Kargil'],
  Lakshadweep: ['Kavaratti', 'Agatti', 'Minicoy'],
  'Madhya Pradesh': ['Indore', 'Bhopal', 'Jabalpur', 'Gwalior', 'Ujjain', 'Sagar', 'Rewa', 'Satna'],
  Maharashtra: ['Mumbai', 'Pune', 'Nagpur', 'Nashik', 'Thane', 'Aurangabad', 'Solapur', 'Kolhapur', 'Amravati', 'Navi Mumbai'],
  Manipur: ['Imphal', 'Thoubal', 'Bishnupur', 'Churachandpur', 'Ukhrul'],
  Meghalaya: ['Shillong', 'Tura', 'Jowai', 'Nongstoin'],
  Mizoram: ['Aizawl', 'Lunglei', 'Champhai', 'Serchhip'],
  Nagaland: ['Kohima', 'Dimapur', 'Mokokchung', 'Wokha', 'Tuensang'],
  Odisha: ['Bhubaneswar', 'Cuttack', 'Rourkela', 'Sambalpur', 'Berhampur', 'Puri', 'Balasore'],
  Puducherry: ['Puducherry', 'Karaikal', 'Mahe', 'Yanam'],
  Punjab: ['Ludhiana', 'Amritsar', 'Jalandhar', 'Patiala', 'Bathinda', 'Mohali', 'Hoshiarpur'],
  Rajasthan: ['Jaipur', 'Jodhpur', 'Udaipur', 'Kota', 'Ajmer', 'Bikaner', 'Alwar', 'Bhilwara'],
  Sikkim: ['Gangtok', 'Namchi', 'Gyalshing', 'Mangan'],
  'Tamil Nadu': ['Chennai', 'Coimbatore', 'Madurai', 'Tiruchirappalli', 'Salem', 'Tirunelveli', 'Vellore', 'Erode', 'Thoothukudi'],
  Telangana: ['Hyderabad', 'Warangal', 'Nizamabad', 'Karimnagar', 'Khammam', 'Ramagundam', 'Secunderabad'],
  Tripura: ['Agartala', 'Udaipur', 'Dharmanagar', 'Kailashahar'],
  'Uttar Pradesh': ['Lucknow', 'Kanpur', 'Noida', 'Ghaziabad', 'Agra', 'Varanasi', 'Prayagraj', 'Meerut', 'Bareilly', 'Aligarh', 'Gorakhpur'],
  Uttarakhand: ['Dehradun', 'Haridwar', 'Roorkee', 'Haldwani', 'Rudrapur', 'Nainital', 'Rishikesh'],
  'West Bengal': ['Kolkata', 'Howrah', 'Durgapur', 'Asansol', 'Siliguri', 'Kharagpur', 'Malda', 'Haldia']
}
export const citiesForState = (state: string) => stateCities[state] ?? []
