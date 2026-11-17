// Mount the real browsing components with synthetic, GET-only local responses.
// No proxy, upstream API, database, payroll writer or SMTP is available here.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'
const toolsRoot = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(toolsRoot, '../payroll-ui')
const require = createRequire(path.join(root, 'package.json'))
const { createServer } = await import(pathToFileURL(require.resolve('vite')).href)
const fixture = path.join(root, '.codex-excel-payslip-scope-fixture.tsx')
const fixtureSource = path.join(toolsRoot, 'fixtures/ExcelPayslipScope.fixture.tsx')
const clients = [
  { id: 901, name: 'Example multi-location client', code: 'EX901', hasSavedBatch: true, locationBatchesSupported: true },
  { id: 902, name: 'Example legacy client', code: 'EX902', hasSavedBatch: true, locationBatchesSupported: true },
  { id: 903, name: 'Example empty client', code: 'EX903', hasSavedBatch: false, locationBatchesSupported: true },
]
const makeBatch = (clientId, month, index, locationLabel) => ({
  id: `example-${clientId}-${month}-${index}`, clientId, month, locationLabel, locationBatch: !!locationLabel,
  clientName: clients.find(client => client.id === clientId).name,
  sourceFileName: `Original salary workbook ${index}.xlsx`, sheetName: index % 5 === 0 ? 'Alternate' : 'Salary', headerRow: 1,
  salaryTemplateId: '', salaryTemplateName: '', createdAtUtc: `2026-11-${String(Math.min(17, index + 1)).padStart(2, '0')}T08:00:00Z`, createdBy: 'Local test',
  rows: Array.from({ length: index % 3 + 1 }, (_, number) => ({
    id: `example-row-${clientId}-${month}-${index}-${number}`, sourceRow: number + 2, employeeCode: `EX${clientId}-${index}-${number}`,
    employeeName: `Example employee ${index}-${number}`, email: '', information: [{ label: 'Location', value: locationLabel || 'Legacy office' }],
    earnings: [{ label: 'Basic', amount: 1000.25 }], deductions: [{ label: 'PF', amount: 100.10 }], employerContributions: [],
    netPay: 900.15, declaredGross: 1000.25, declaredDeductions: 100.10, warnings: [],
  })),
  calculationSource: { sheetName: 'Salary', headerRow: 1, columnCount: 3, columns: [], rows: [{ sourceRow: 2, cells: [{ value: '1000.25' }, { value: '100.10' }, { value: '900.15', formulaText: 'A2-B2' }] }], excludedSourceRows: [] },
})
const batches = [
  ...Array.from({ length: 45 }, (_, index) => makeBatch(901, '2026-10', index, index < 2 ? 'Harbour office' : `Office ${String(index + 1).padStart(2, '0')} — a long location label for layout verification`)),
  ...Array.from({ length: 5 }, (_, index) => makeBatch(901, '2026-09', index, index === 0 ? 'Harbour office' : `September office ${index}`)),
  makeBatch(902, '2026-09', 0), makeBatch(902, '2026-08', 0),
]
const witness = JSON.stringify(batches)
const summary = ({ rows, calculationSource: _, ...batch }) => ({ ...batch, rowCount: rows.length })
let blockedWrites = 0
const reads = []
const html = '<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module" src="/.codex-excel-payslip-scope-fixture.tsx"></script></body></html>'
const plugin = {
  name: 'isolated-payslip-scope', enforce: 'pre',
  resolveId(id) {
    if (id === '/.codex-excel-payslip-scope-fixture.tsx' || id.replaceAll('\\', '/') === fixture.replaceAll('\\', '/')) return fixture
  },
  load(id) {
    const file = id.split('?')[0].replaceAll('\\', '/')
    if (file === fixture.replaceAll('\\', '/')) return fs.readFileSync(fixtureSource, 'utf8')
    if (file.endsWith('/src/services/apiBaseUrl.ts')) return 'export function resolveRuntimeApiBase(){return window.location.origin}'
  },
  configureServer(server) { server.middlewares.use(async (req, res, next) => {
    const url = new URL(req.url, 'http://127.0.0.1:5197'), route = url.pathname
    const reply = (value, status = 200) => { res.writeHead(status, { 'content-type': 'application/json' }); res.end(JSON.stringify(value)) }
    if (route === '/scope-fixture') { res.writeHead(200, { 'content-type': 'text/html' }); res.end(await server.transformIndexHtml(req.url, html)); return }
    if (route === '/__test/state') return reply({ blockedWrites, reads, financialsAndFormulasUnchanged: JSON.stringify(batches) === witness, databaseWrites: 0, emailsSent: 0 })
    if (!route.startsWith('/api/')) return next()
    if (req.method !== 'GET') { blockedWrites++; return reply({ error: 'Mutations are disabled in this local fixture.' }, 405) }
    const bound = (req.headers.referer || '').includes('bound=1')
    const clientId = Number(url.searchParams.get('clientId'))
    if (route === '/api/auth/me') return reply({ id: bound ? 998 : 997, email: 'example@example.test', displayName: 'Local reviewer', isActive: true, mustChangePassword: false,
      roles: [bound ? 'Client reviewer' : 'Admin'], permissions: bound ? ['reports.view'] : ['security.manage', 'reports.view', 'payroll.run', 'settings.manage', 'employees.manage'], clientAccessMode: bound ? 'Selected' : 'All', clientId: bound ? 902 : null, assignedClientIds: bound ? [902] : [] })
    if (route === '/api/clients' || route === '/api/excel-payslips/clients') return reply(bound ? clients.filter(client => client.id === 902) : clients)
    if (bound && clientId && clientId !== 902) return reply({ error: 'Client not permitted.' }, 403)
    if (route === '/api/excel-payslips/batches') { reads.push({ clientId, kind: 'history' }); return reply(batches.filter(batch => batch.clientId === clientId).map(summary)) }
    const match = /^\/api\/excel-payslips\/batches\/([^/]+)(.*)$/.exec(route)
    if (match) {
      const batch = batches.find(item => item.id === decodeURIComponent(match[1]) && item.clientId === clientId)
      if (!batch) return reply({ error: 'No saved batch in this scope.' }, 404)
      if (!match[2]) { reads.push({ clientId, month: batch.month, id: batch.id, kind: 'batch' }); return reply(batch) }
      if (match[2] === '/delivery-status') return reply({ items: batch.rows.map(row => ({ rowId: row.id, status: 'Pending', email: '', canSend: false, message: 'Local test only' })) })
      if (match[2] === '/mail-jobs') return reply({ items: [] })
      if (match[2].includes('consolidation')) return reply({ items: [], groups: [] })
    }
    if (route.endsWith('/mail-jobs')) return reply({ items: [] })
    if (route.includes('consolidation')) return reply({ items: [], groups: [] })
    return reply({ error: 'Unmocked API request is blocked by the local fixture.' }, 403)
  }) },
}
const server = await createServer({ root, plugins: [plugin], server: { host: '127.0.0.1', port: 5197, strictPort: true, open: false } })
await server.listen()
console.log('Read-only local payslip scope fixture: http://127.0.0.1:5197/scope-fixture?clientId=901')
const close = async () => { await server.close(); process.exit() }
process.on('SIGINT', close); process.on('SIGTERM', close)
