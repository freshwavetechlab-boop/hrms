import type { Client, Component, Drop, Employee, Org, PayslipTemplate, Setup, Structure, TaxDeclarationSection, TaxFinalAdjustment, TaxSlab, TaxSurcharge, WorkLocation } from '../types/payroll'

export const org0: Org = { name: '', legalName: '', businessType: '', businessLocation: 'India', industry: '', hasRunPayrollThisYear: false, setupCompleted: false, logoDataUrl: '', addressLine1: '', addressLine2: '', city: '', state: '', postalCode: '', country: 'India', professionalTaxNumber: '' }
export const defaultTaxSlabs: TaxSlab[] = []
export const defaultTaxSurcharges: TaxSurcharge[] = []
export const defaultTaxFinalAdjustments: TaxFinalAdjustment[] = []
export const defaultTaxSections: TaxDeclarationSection[] = []
export const setup0: Setup = { tax: { pan: '', tan: '', aoCode: '', frequency: '', clientSettings: [], slabs: defaultTaxSlabs, surcharges: defaultTaxSurcharges, finalAdjustments: defaultTaxFinalAdjustments, declarationSections: defaultTaxSections }, schedule: { workWeek: '', salaryDays: '', fixedDays: '', payDay: '', firstPayPeriod: '' }, statutory: { epf: false, epfNumber: '', epfCtc: false, abry: false, epfContribution: '', restrictPf: false, esi: false, esiNumber: '', pt: false, ptNumber: '', ptState: '', ptCycle: '', ptSlabs: '', ptStateSlabs: [], lwf: false, lwfState: '', lwfCycle: '', lwfEligibilityLimit: '', lwfEmployeeContribution: '', lwfEmployerContribution: '' }, salaryComponents: [], salaryStructures: [], payslipTemplates: [] }
export const component0: Component = { id: 0, code: '', componentType: 'Custom Allowance', category: 'Earning', name: '', payType: 'Fixed Pay', calculationType: 'Fixed Amount', value: '', formula: '', baseComponent: '', taxable: true, ctc: true, proRata: true, fbp: false, restrictFbp: false, epf: 'Never', esi: false, recurring: true, scheduled: false, investmentType: '', correctionOf: '', active: true, priority: '100' }
export const demoComponents: Component[] = []
export const structure0: Structure = { id: 0, clientId: '', name: '', annualCtc: '', lines: [], active: true }
export const payslip0: PayslipTemplate = { id: 0, clientId: '', name: 'GA Digital Payslip', theme: 'GA Digital', showLogo: true, showClient: true, showYtd: false, showBank: true, note: 'This is a system generated payslip.', active: true }
export const employee0: Employee = { id: 0, clientId: 0, employeeCode: '', firstName: '', lastName: '', gender: '', dateOfJoining: '', workEmail: '', department: '', designation: '', workLocationId: 0, reportingManagerId: 0, portalAccess: false, salaryStructureId: '', annualCtc: 0, salaryComponents: {}, personalDetails: { dateOfBirth: '', mobile: '', panNumber: '', aadhaarNumber: '', uanNumber: '', esicNumber: '', address: '', source: '', sourceLocation: '', city: '', district: '', state: '', rawDesignation: '', originalEmployeeCode: '', duplicateResolution: '', excelRow: 0, esicEmployee: 0, ptLwfWorkmenComp: 0, tds: 0, recovery: 0 }, paymentDetails: { bankName: '', bankAccountNo: '', ifscCode: '', paymentMode: '' }, salaryJson: '{}', personalJson: '{}', paymentJson: '{}', isActive: true }
export const client0: Client = { id: 0, name: '', code: '', contactPerson: '', email: '', phone: '', address: '', payScheduleJson: '', isActive: true }
export const location0: WorkLocation = { id: 0, clientId: 0, clientName: '', name: '', address: '', city: '', state: '', postalCode: '', isPrimary: false, isActive: true }
export const drop0: Drop = { id: 0, type: 'Department', value: '', isActive: true }
export const settingsMenus = ['Organization', 'Clients', 'Work Locations', 'Dropdown Masters', 'Pay Schedule', 'Tax Engine', 'Statutory Setup', 'Salary Components', 'Salary Templates', 'Payslip Templates'] as const
export const securityMenus = ['Users', 'Roles', 'Audit'] as const
export const leaveAttendanceMenus = ['Preferences', 'Leave Types', 'Holiday', 'Attendance', 'Geo-Fencing', 'Import Balance'] as const
export const reportingMenus = ['Payroll Reports', 'Employee Reports', 'Attendance Reports', 'Leave Reports', 'Recruitment Reports', 'Onboarding Reports', 'Separation Reports', 'Compliance Reports', 'Tax Reports', 'Loan & Advance Reports', 'Cost Center Reports', 'Department Reports', 'Location Reports', 'Contractor Reports', 'Audit Reports', 'MIS Reports', 'Executive Dashboards', 'Scheduled Reports', 'Report Builder'] as const
export const workflowMenus = ['Workflow Setup', 'Department Head Assignments', 'My Tasks', 'Workflow History'] as const
export const dropTypes = ['Department', 'Designation', 'Employment Type', 'Employee Grade', 'Cost Center', 'Location Tag', 'State', 'City']
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
  Haryana: ['Gurugram', 'Faridabad', 'Panipat', 'Ambala', 'Hisar', 'Karnal', 'Rohtak', 'Sonipat','Panchkula'],
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
