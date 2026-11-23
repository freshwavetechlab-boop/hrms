import fs from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'

const here = path.dirname(fileURLToPath(import.meta.url)), root = path.resolve(here, '../payroll-ui')
const require = createRequire(path.join(root, 'package.json'))
const { createServer } = await import(pathToFileURL(require.resolve('vite')).href)
const entry = path.join(root, '.codex-requisition-save.tsx')
const limits = Array.from((await fs.readFile(path.resolve(here, '../Payroll.API/Models/RecruitmentRequisitionTextLimits.cs'), 'utf8')).matchAll(/new\("([^"]+)", "([^"]+)", (\d+)(?:, "([^"]+)")?\)/g), m => ({ field: m[1], label: m[2], maximum: Number(m[3]), unit: m[4] || 'characters' }))
const original = { id: 123, clientId: 55, clientName: 'Synthetic client', rfrNumber: 'RFR-SYN-2026-00065', requestDate: '2026-11-23', requestedByEmployeeId: 5, requestedByUserId: 81, requesterUserId: 81, requestedByName: 'Synthetic approver', positionTitle: 'Project Technical Support III — Synthetic study', department: 'Medical / Health Research', positionCategory: '', employmentType: 'Contractual', hiringType: 'Contract', numberOfOpenings: 1, branchId: 17, jobLocation: 'Synthetic field location', workMode: 'Office', project: 'Synthetic study', hiringPriority: 'High', experienceRange: '2 years', qualification: 'Graduate', requiredSkills: 'Research', businessJustification: 'Coordinate synthetic research data collection.', reasonForHiring: 'Synthetic vacancy', currency: 'INR', salaryMin: 0, salaryMax: 0, budgetAvailable: false, budgetAmount: 0, status: 'Draft', hasLinkedOpenPosition: false, hasLinkedJobDescription: false, sourceDocumentName: 'synthetic-requisition.pdf', sourceParsedJson: '{}', createdAt: '2026-11-23T10:55:03Z', updatedAt: '2026-11-23T10:55:03Z' }
const state = { scenario: 'jd-network', mutations: [], record: { ...original }, jd: null, refreshFailure: false }
const plugin = {
  name: 'isolated-requisition-save', enforce: 'pre',
  resolveId(id) { if (id === '/.codex-requisition-save.tsx') return entry },
  async load(id) {
    if (id.split('?')[0].replaceAll('\\', '/') === entry.replaceAll('\\', '/')) return fs.readFile(path.join(here, 'fixtures/RequisitionSave.fixture.tsx'), 'utf8')
    if (id.split('?')[0].replaceAll('\\', '/').endsWith('/src/services/apiBaseUrl.ts')) return 'export function resolveRuntimeApiBase(){return window.location.origin}'
  },
  configureServer(server) { server.middlewares.use(async (req, res, next) => {
    const url = new URL(req.url, 'http://127.0.0.1:5211'), route = url.pathname
    const scenario = state.scenario
    const reply = (body, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json', 'X-Request-ID': 'synthetic-save-reference' }); res.end(JSON.stringify(body)) }
    if (route === '/requisition-save-verification') { state.scenario = url.searchParams.get('case') || 'jd-network'; state.mutations = []; state.record = { ...original }; state.jd = null; state.refreshFailure = false; res.writeHead(200, { 'Content-Type': 'text/html' }); res.end(await server.transformIndexHtml(req.url, '<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module" src="/.codex-requisition-save.tsx"></script></body></html>')); return }
    if (route === '/__fixture/state') return reply(state)
    if (route === '/__fixture/reset') { state.mutations = []; state.record = { ...original }; state.jd = null; state.refreshFailure = false; return reply({ ok: true }) }
    if (!route.startsWith('/api/')) return next()
    if (req.method !== 'GET') {
      const chunks = []; for await (const chunk of req) chunks.push(chunk)
      let body = {}; try { body = JSON.parse(Buffer.concat(chunks).toString()) } catch { /* Synthetic multipart content is never persisted. */ }
      state.mutations.push({ method: req.method, route })
      if (route === '/api/recruitment/requisitions/parse-source') return reply({ status: 'Parsed', parserName: 'Synthetic fixture', parserVersion: '1', originalFileName: 'synthetic-requisition.pdf', draft: { ...state.record }, detectedFields: ['positionTitle', 'requiredSkills'], reviewFields: [], warnings: [] })
      if (route === '/api/recruitment-orchestration/job-descriptions') {
        if (scenario === 'jd-network') { res.destroy(); return }
        if (scenario === 'jd-server') { res.writeHead(500, { 'X-Request-ID': 'synthetic-save-reference' }); res.end(); return }
        if (scenario === 'jd-validation') return reply({ error: 'Synthetic JD validation: review the required skills.' }, 400)
        state.jd = { ...body, id: 501, status: 'Draft', versionNumber: 1 }
        return reply(state.jd)
      }
      if (route === '/api/recruitment/requisitions') {
        if (scenario === 'save-validation') return reply({ error: 'Synthetic validation: openings cannot be below already-filled positions.' }, 400)
        state.record = { ...state.record, ...body, id: 123, rfrNumber: original.rfrNumber }
        if (scenario === 'save-network') { res.destroy(); return }
        if (scenario === 'refresh-network') state.refreshFailure = true
        return reply(state.record)
      }
      if (route === '/api/attachments') return scenario === 'source-upload' ? reply({ error: 'Synthetic source-document storage is unavailable. The request is already saved.' }, 503) : reply({ id: 1 })
      if (route === '/api/recruitment/requisitions/123/submit') {
        if (scenario === 'submit-network') { res.destroy(); return }
        state.record.status = 'Pending Approval'; return reply(state.record)
      }
      return reply({ error: 'This mutation is not enabled by the local fixture.' }, 400)
    }
    if (route === '/api/auth/me') return reply({ id: 81, clientId: null, email: 'verifier@example.invalid', displayName: 'Synthetic administrator', roles: ['employee'], permissions: ['settings.manage'], isActive: true, mustChangePassword: false, clientAccessMode: 'Legacy', assignedClientIds: [], recruitmentScopeMode: 'Client' })
    if (route === '/api/clients') return reply([{ id: 55, code: 'SYN', name: 'Synthetic client', isActive: true }])
    if (route === '/api/employees') return reply([{ id: 5, clientId: 55, firstName: 'Synthetic', lastName: 'Approver', isActive: true }])
    if (route === '/api/work-locations') return reply([{ id: 17, clientId: 55, name: 'Synthetic field location', isActive: true }])
    if (route === '/api/recruitment/requesters') return reply([{ userId: 81, id: 81, clientId: 55, employeeId: 5, displayName: 'Synthetic approver', email: 'verifier@example.invalid', isActive: true }])
    if (route === '/api/recruitment/requisitions') { if (state.refreshFailure) { res.destroy(); return }; return reply([state.record]) }
    if (route === '/api/recruitment/requisitions/field-limits') return reply(limits)
    if (route.startsWith('/api/recruitment/masters/')) return reply(['Contract', 'Contractual', 'High', 'Office', 'Medical / Health Research', 'Research'])
    if (route === '/api/attachment-configurations/effective') return reply(url.searchParams.get('formCode') === 'HIRING_REQUEST' ? [{ id: 47, clientId: 0, attachmentAttributeId: 37, attributeCode: 'RECRUITMENT_REQUEST_SOURCE', attributeName: 'Hiring request source', fieldKey: 'HIRING_REQUEST_SOURCE', fieldLabel: 'Original hiring request / JD', moduleCode: 'RECRUITMENT', formCode: 'HIRING_REQUEST', isActive: true, maximumFileSizeBytes: 10 * 1024 * 1024, maximumFileCount: 10, allowedExtensionsJson: '["pdf","docx","txt"]' }] : [])
    if (route.endsWith('/lookups')) return reply({ lookupSources: [], attachmentConfigurations: [], attachmentFieldConfigurations: [], workflows: [], forms: [], positions: [], atsScoringProfiles: [] })
    if (route === '/api/recruitment-orchestration/job-descriptions') return reply(state.jd ? [state.jd] : [])
    if (route === '/api/recruitment-orchestration/job-descriptions/501') return reply(state.jd)
    return reply([])
  }) },
}
const server = await createServer({ root, configFile: false, plugins: [plugin], cacheDir: 'C:/Users/APPC0024/AppData/Local/Temp/frevo-requisition-save-vite-cache', server: { host: '127.0.0.1', port: 5211, strictPort: true } })
await server.listen()
console.log('Synthetic hiring-request save: http://127.0.0.1:5211/requisition-save-verification?case=jd-network')
