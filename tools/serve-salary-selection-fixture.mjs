import fs from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'

const here = path.dirname(fileURLToPath(import.meta.url)), root = path.resolve(here, '../payroll-ui')
const require = createRequire(path.join(root, 'package.json'))
const { createServer } = await import(pathToFileURL(require.resolve('vite')).href)
const port = Number(process.env.FREVO_SALARY_SELECTION_FIXTURE_PORT || 5215)
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('Choose a valid local fixture port.')
const entry = path.join(root, '.codex-salary-selection.tsx')
let eligible = false
let saves = []
const requests = []
const clients = [{ id: 17, name: 'Synthetic Research Client', code: 'SYN-RESEARCH', isActive: true }, { id: 33, name: 'Synthetic Other Client', code: 'SYN-OTHER', isActive: true }]
const component = { componentType: 'Custom Allowance', componentRole: 'Regular Earning', statutoryType: 'None', category: 'Earning', payType: 'Fixed Pay', calculationType: 'Fixed Amount', formula: '', baseComponent: '', taxable: true, ctc: true, proRata: true, fbp: false, restrictFbp: false, epf: 'Never', esi: false, recurring: true, scheduled: false, investmentType: '', correctionOf: '', active: true }
const components = [{ ...component, id: 1, code: 'BASIC', name: 'Basic', value: '10000', priority: '1' }, { ...component, id: 2, code: 'HRA', name: 'HRA', value: '3000', priority: '2' }]
const templates = () => [{ id: 701, clientId: '33', name: 'Other client monthly salary', annualCtc: '0', active: true, lines: [{ componentId: '1', value: '99999' }, { componentId: '2', value: '20000' }] }, ...(eligible ? [{ id: 702, clientId: '17', name: 'Research client monthly salary', annualCtc: '0', active: true, lines: [{ componentId: '1', value: '10000' }, { componentId: '2', value: '3000' }] }] : [])]
const initialEmployee = { id: 501, clientId: 17, employeeCode: 'SYN-EMP-205', firstName: 'Synthetic', lastName: 'Employee', gender: 'Female', dateOfJoining: '2026-10-01', workEmail: 'synthetic@example.invalid', department: 'Research', designation: 'Technical Support', grade: '', workLocationId: 171, reportingManagerId: 0, reportingManagerUserId: null, portalAccess: false, salaryStructureId: '', annualCtc: 436800, salaryJson: '{"1":28000,"2":8400}', salaryComponents: { 1: 28000, 2: 8400 }, personalDetails: { employmentType: 'Contractual', skillCategory: '', dateOfBirth: '', mobile: '', panNumber: '', aadhaarNumber: '', uanNumber: '', esicNumber: '', address: '', correspondenceAddress: '', permanentAddress: '', source: '', sourceLocation: '', city: '', district: '', state: '', rawDesignation: '', originalEmployeeCode: '', duplicateResolution: '', excelRow: 0, esicEmployee: 0, ptLwfWorkmenComp: 0, tds: 0, recovery: 0 }, paymentDetails: { bankName: '', bankAccountNo: '', ifscCode: '', paymentMode: '' }, personalJson: '{}', paymentJson: '{}', isActive: true }
let employee = structuredClone(initialEmployee)
const plugin = {
  name: 'local-salary-selection-verification', enforce: 'pre',
  resolveId(id) { if (id === '/.codex-salary-selection.tsx') return entry },
  async load(id) {
    if (id.split('?')[0].replaceAll('\\', '/') === entry.replaceAll('\\', '/')) { this.addWatchFile(path.join(here, 'fixtures/SalarySelection.fixture.tsx')); return fs.readFile(path.join(here, 'fixtures/SalarySelection.fixture.tsx'), 'utf8') }
    if (id.split('?')[0].replaceAll('\\', '/').endsWith('/src/services/apiBaseUrl.ts')) return 'export function resolveRuntimeApiBase(){return window.location.origin}'
  },
  configureServer(server) { server.middlewares.use(async (req, res, next) => {
    const url = new URL(req.url, `http://127.0.0.1:${port}`), route = url.pathname
    const reply = (body, status = 200) => { res.writeHead(status, { 'content-type': 'application/json' }); res.end(JSON.stringify(body)) }
    if (route === '/salary-selection-verification') {
      eligible = url.searchParams.has('eligible'); saves = []; requests.length = 0; employee = structuredClone(initialEmployee)
      res.writeHead(200, { 'content-type': 'text/html' })
      res.end(await server.transformIndexHtml(req.url, '<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module" src="/.codex-salary-selection.tsx"></script></body></html>')); return
    }
    if (route === '/__fixture/state') return reply({ eligible, saves, employee, requests, productionRequests: 0, databaseWrites: 0, emails: 0 })
    if (!route.startsWith('/api/')) return next()
    requests.push({ method: req.method, route })
    if (route === '/api/auth/me') return reply({ id: 991, clientId: 17, displayName: 'Synthetic local verifier', email: 'verifier@example.invalid', roles: ['Verifier'], permissions: ['settings.manage', 'employees.manage', 'employees.view'], isActive: true, mustChangePassword: false, clientAccessMode: 'Legacy', assignedClientIds: [17], recruitmentScopeMode: 'Client' })
    if (req.method === 'GET' && route === '/api/clients') return reply(clients)
    if (req.method === 'GET' && route === '/api/employees') return reply([employee])
    if (req.method === 'GET' && route === '/api/work-locations') return reply([{ id: 171, clientId: 17, name: 'Synthetic Research Site', isActive: true }])
    if (req.method === 'GET' && route === '/api/setup') return reply({ salaryComponents: components, salaryStructures: templates(), payslipTemplates: [] })
    if (req.method === 'GET' && route === '/api/dropdowns') return reply([{ id: 1, clientId: 17, type: 'Department', value: 'Research', isActive: true }, { id: 2, clientId: 17, type: 'Designation', value: 'Technical Support', isActive: true }])
    if (req.method === 'GET' && route === '/api/employees/manager-users') return reply([])
    if (req.method === 'GET' && route.endsWith('/infotypes')) return reply([])
    if (req.method === 'GET' && route === '/api/employees/field-configuration/catalog') return reply({ forms: [], fields: [], values: {} })
    if (req.method === 'POST' && route === '/api/payroll-calculation/catalogue') return reply([{ token: 'PAYROLL_DAYS', meaning: 'Payroll days', source: 'Synthetic fixture', unit: 'days', example: '30', availability: 'Available', legacy: false }])
    if (req.method === 'POST' && route === '/api/employees') {
      let body = ''; for await (const chunk of req) body += chunk
      const payload = JSON.parse(body || '{}')
      if (payload.clientId !== 17 || payload.salaryStructureId && payload.salaryStructureId !== '702') return reply({ error: 'Synthetic assignment rejected: use a template belonging to this employee client.' }, 400)
      saves.push(payload); employee = structuredClone(payload); return reply({ id: 501 })
    }
    if (req.method !== 'GET') return reply({ error: 'This synthetic fixture does not perform that action.' }, 400)
    return reply([])
  }) },
}
const server = await createServer({ root, configFile: false, plugins: [plugin], server: { host: '127.0.0.1', port, strictPort: true }, cacheDir: 'C:/Users/APPC0024/AppData/Local/Temp/frevo-salary-selection-vite-cache' })
await server.listen()
console.log(`Synthetic salary selection fixture: http://127.0.0.1:${port}/salary-selection-verification?clientId=17`)
