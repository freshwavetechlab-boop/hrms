// Isolated UI verification with the real extractor and pure native calculator.
import fs from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'
import { spawn } from 'node:child_process'
const toolsRoot = path.dirname(fileURLToPath(import.meta.url)), root = path.resolve(toolsRoot, '../payroll-ui')
const require = createRequire(path.join(root, 'package.json')), ts = require('typescript')
const { createServer } = await import(pathToFileURL(require.resolve('vite')).href)
const out = path.resolve(toolsRoot, '../../.codex-artifacts/salary-template-tools-20261119')
await fs.mkdir(path.join(out, 'inputs'), { recursive: true })
const components = [
  { id: 11, code: 'BASIC', name: 'Basic', category: 'Earning', calculationType: 'Fixed Amount', value: '', formula: '', proRata: true, componentRole: 'Regular Earning', priority: '1' },
  { id: 12, code: 'PF', name: 'PF', category: 'Deduction', calculationType: 'Formula', value: '', formula: 'MIN(BASIC_EARNED,15000)*12%', proRata: false, componentRole: 'Other Deduction', priority: '90' },
].map(c => ({ componentType: 'Salary', statutoryType: 'None', baseComponent: '', payType: 'Fixed Pay', taxable: true, ctc: c.category === 'Earning', fbp: false, restrictFbp: false, epf: '', esi: false, recurring: true, scheduled: false, investmentType: '', correctionOf: '', active: true, ...c }))
const clients = [{ id: 901, name: 'Example multi-location client' }, { id: 902, name: 'Example other client' }]
const locations = [{ id: 71, clientId: 901, name: 'Site A', isActive: true }, { id: 72, clientId: 901, name: 'Site B', isActive: true }, { id: 73, clientId: 902, name: 'Other site', isActive: true }]
const templates = Array.from({ length: 18 }, (_, i) => ({ id: i + 1, clientId: i === 17 ? '902' : '901', name: `Saved ${i === 17 ? 'other client' : 'site'} template ${String(i + 1).padStart(2, '0')}`, annualCtc: '', active: true, lines: [{ componentId: '11', value: '', proRataOverride: 'true' }], excelSource: { version: 1, fileName: 'Synthetic original.xlsx', sheetName: 'Salary', headerRow: 4, sampleRow: 5, payPeriod: '2026-10', payrollDays: i % 2 ? 30 : 26, workLocationLabel: i % 2 ? 'Site B' : 'Site A', columns: [] } }))
const exported = {}
new Function('exports', ts.transpileModule(await fs.readFile(path.join(root, 'src/utils/xlsx.ts'), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exported)
const input = value => ({ value, type: typeof value === 'number' ? 'number' : 'text' })
const formula = (formula, value) => ({ formula, value, type: 'number' })
const headers = ['Employee name', 'Monthly Basic', 'Payroll days', 'Actual present days', 'Payable days', 'Basic', 'Monthly HRA', 'HRA', 'PF', 'Professional Tax', 'Total earnings', 'Total deductions', 'Net pay', ...Array.from({ length: 23 }, (_, i) => `Report note ${i + 1}`)]
const data = n => [input(`Synthetic employee ${n - 4}`), input(10000), input(26), input(11), input(13), formula(`B${n}/C${n}*E${n}`, 5000), formula(`B${n}*40%`, 4000), formula(`G${n}/C${n}*E${n}`, 2000), formula(`MIN(F${n}+H${n},15000)*12%`, 840), input(0), formula(`F${n}+H${n}`, 7000), formula(`I${n}+J${n}`, 840), formula(`K${n}-L${n}`, 6160), ...Array.from({ length: 23 }, (_, i) => input(`Reference ${i + 1}`))]
const sheets = [{ name: 'Cover', rows: [['Synthetic workbook'], ['Open the Salary worksheet']] }, { name: 'Salary', rows: [['Example salary statement'], ['October 2026'], ['Site A'], headers.map(input), data(5), data(6)] }]
await fs.writeFile(path.join(out, 'inputs/salary-site-a.xlsx'), Buffer.from(await exported.buildXlsxBlob(sheets).arrayBuffer()))
const native = path.join(toolsRoot, 'fixtures/SalaryTemplateTools.Native/bin/Debug/net8.0/Native.dll')
const state = { nativeRequests: [], blockedWrites: 0, saves: [], databaseWrites: 0, emailsSent: 0 }
const calculate = payload => new Promise((resolve, reject) => {
  const child = spawn('dotnet', [native], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] })
  let stdout = '', stderr = ''; const timeout = setTimeout(() => child.kill(), 60000)
  child.stdout.on('data', data => stdout += data); child.stderr.on('data', data => stderr += data)
  child.on('error', reject); child.on('close', code => { clearTimeout(timeout); try { const result = JSON.parse(stdout.trim()); code ? reject(Error(result.error || stderr)) : resolve(result) } catch { reject(Error(stderr || stdout || 'Native fixture did not return JSON.')) } })
  child.stdin.end(JSON.stringify(payload))
})
const fixture = path.join(root, '.codex-salary-template-tools-fixture.tsx')
const plugin = {
  name: 'isolated-template-tools', enforce: 'pre',
  resolveId(id) { if (id === '/.codex-salary-template-tools-fixture.tsx' || id.replaceAll('\\', '/') === fixture.replaceAll('\\', '/')) return fixture; if (id === '/__fixture/seed') return '\0salary-template-seed' },
  async load(id) { if (id === '\0salary-template-seed') return `export default ${JSON.stringify({ clients, components, templates, locations })}`; if (id.split('?')[0].replaceAll('\\', '/') === fixture.replaceAll('\\', '/')) return fs.readFile(path.join(toolsRoot, 'fixtures/SalaryTemplateTools.fixture.tsx'), 'utf8'); if (id.split('?')[0].replaceAll('\\', '/').endsWith('/src/services/apiBaseUrl.ts')) return 'export function resolveRuntimeApiBase(){return window.location.origin}' },
  configureServer(server) { server.middlewares.use(async (req, res, next) => {
    const route = new URL(req.url, 'http://127.0.0.1:5198').pathname
    const reply = (body, status = 200) => { res.writeHead(status, { 'content-type': 'application/json' }); res.end(JSON.stringify(body)) }
    const body = async () => { let value = ''; for await (const chunk of req) { value += chunk; if (value.length > 10 * 1024 * 1024) throw Error('Fixture request too large') } return JSON.parse(value) }
    try {
      if (route === '/template-tools-fixture') { res.writeHead(200, { 'content-type': 'text/html' }); res.end(await server.transformIndexHtml(req.url, '<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module" src="/.codex-salary-template-tools-fixture.tsx"></script></body></html>')); return }
      if (route === '/__fixture/state') return reply(state)
      if (route === '/__fixture/save-receipt' && req.method === 'POST') { state.saves.push(await body()); return reply({ ok: true }) }
      if (!route.startsWith('/api/')) return next()
      if (req.method === 'GET' && route === '/api/auth/me') return reply({ id: 996, email: 'reviewer@example.test', displayName: 'Local reviewer', isActive: true, mustChangePassword: false, clientId: null, roles: ['Admin'], permissions: ['settings.manage', 'security.manage', 'employees.manage'], clientAccessMode: 'All', assignedClientIds: [] })
      if (req.method === 'GET' && route.startsWith('/api/excel-payslips/profiles')) return reply({ error: 'No saved mapping in synthetic fixture.' }, 404)
      if (req.method === 'POST' && ['/api/payroll-calculation/extract-template', '/api/payroll-calculation/simulate'].includes(route)) {
        const request = await body(), operation = route.endsWith('simulate') ? 'simulate' : 'extract'
        if (request.clientId !== 901 && request.clientId !== 902) return reply({ error: 'Client not permitted.' }, 403)
        const result = await calculate({ operation, request, components })
        state.nativeRequests.push({ operation, clientId: request.clientId, canCreateDraft: result.canCreateDraft, complete: result.complete, sourceMatched: result.comparisons?.every(row => row.status === 'Matched') })
        await fs.writeFile(path.join(out, 'last-native-request.json'), JSON.stringify({ operation, request, components }, null, 2))
        await fs.writeFile(path.join(out, 'last-native-result.json'), JSON.stringify(result, null, 2))
        return reply(result)
      }
      if (req.method !== 'GET') state.blockedWrites++
      return reply({ error: 'This isolated fixture has no application write or upstream API.' }, req.method === 'GET' ? 404 : 405)
    } catch (error) { return reply({ error: error.message }, 400) }
  }) },
}
const server = await createServer({ root, plugins: [plugin], server: { host: '127.0.0.1', port: 5198, strictPort: true, open: false } })
await server.listen(); console.log(`Local salary-template tools: http://127.0.0.1:5198/template-tools-fixture\nSynthetic upload: ${path.join(out, 'inputs/salary-site-a.xlsx')}`)
const close = async () => { await server.close(); process.exit() }; process.on('SIGINT', close); process.on('SIGTERM', close)
