// Mounted Talent Acquisition verification against in-memory APIs only.
// No database, SMTP, upstream API or existing server is used.
import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = fileURLToPath(new URL('../payroll-ui/', import.meta.url))
const require = createRequire(path.join(root, 'package.json'))
const { createServer } = await import(pathToFileURL(require.resolve('vite')).href)
const { default: react } = await import(pathToFileURL(require.resolve('@vitejs/plugin-react')).href)
const ts = require('typescript')
const playwright = process.env.HRMS_PLAYWRIGHT_MODULE
  ? await import(pathToFileURL(process.env.HRMS_PLAYWRIGHT_MODULE).href)
  : createRequire(new URL('../playwright-e2e/package.json', import.meta.url))('@playwright/test')
const { chromium } = playwright
const expect = playwright.expect.configure({ timeout: 20000 })
const output = path.join(os.tmpdir(), 'frevo-recruitment-roster-ui')
fs.mkdirSync(output, { recursive: true })
const fixtureName = `.codex-roster-fixture-${crypto.randomUUID()}.tsx`
const fixture = path.join(root, fixtureName)
const modules = new Map()
function loadUtility(name) {
  if (modules.has(name)) return modules.get(name)
  const exports = {}, code = ts.transpileModule(fs.readFileSync(path.join(root, 'src/utils', name + '.ts'), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  modules.set(name, exports)
  new Function('exports', 'require', code)(exports, value => loadUtility(value.replace('./', '')))
  return exports
}
const { buildXlsxBlob } = loadUtility('xlsx')
const { standardRosterSheets } = loadUtility('recruitmentRosterExchangeTemplate')
const standardColumns = ['profileCode','employeeCode','firstName','lastName','candidateEmail','candidatePhone','positionTitle','joiningDate','workLocation','bankAccountNo','SALARY:BASIC','revision'].map(key => ({ key, header: key + ' [' + key + ']', infotype: key.startsWith('SALARY:') ? '0008' : '0001', type: key.startsWith('SALARY:') ? 'money' : 'text', readOnly: ['profileCode','revision'].includes(key), description: '' }))
const standardSchema = (clientId, sourceGroup, mode) => ({ version:'FREVO-EMPLOYEE-1', clientId, templateId:7, sourceGroup, columns:standardColumns, importBatches:['November joiners'], rows:mode === 'Update' ? rosterRows.map(row=>({profileCode:row.candidateCode,employeeCode:row.employeeCode||'',firstName:row.candidateName,lastName:'',candidateEmail:row.email,positionTitle:row.values.positionTitle,'SALARY:BASIC':row.values.monthlyBasic||'',workLocation:row.workLocation,revision:'a'.repeat(64)})) : [] })
const field = (key, label, sourceHeaders, type = 'text', required = false) => ({ key, label, sourceHeaders, type, required, defaultValue: '', blankText: '' })
const template = { id: 7, clientId: 30, templateName: 'Synthetic employee offer', importPresets: [{ name: 'Synthetic legacy list', headerRow: 4, defaults: {} }], fields: [
  field('candidateName', 'Employee name', ['Employee Name'], 'text', true), field('candidateEmail', 'Email', ['Email']),
  field('candidatePhone', 'Phone', ['Mobile']), field('positionTitle', 'Designation', ['Designation'], 'text', true),
  field('joiningDate', 'Joining date', ['DOJ'], 'date', true), field('monthlyGross', 'Monthly gross', ['Monthly Gross'], 'money', true),
  field('tenureMonths', 'Tenure', [], 'number'),
  field('jobLocation', 'Work location / institute', ['Location']),
] }
const clients = [30, 31, 32].map(id => ({ id, code: 'SYNTH' + id, name: id === 30 ? 'Synthetic saved roster' : id === 31 ? 'Synthetic native offers' : 'Synthetic empty client', isActive: true }))
const user = { id: 900, email: 'synthetic@example.invalid', displayName: 'Synthetic reviewer', isActive: true, mustChangePassword: false, roles: ['super_admin'], permissions: ['settings.manage', 'recruitment.manage'], clientId: null, clientAccessMode: 'All' }
const rosterRows = Array.from({ length: 35 }, (_, index) => ({ id: index + 1, clientId: 30, candidateId: index + 1, candidateCode: 'SYNTH-' + String(index + 1).padStart(3, '0'), candidateName: 'Synthetic employee ' + String(index + 1).padStart(2, '0'), email: `synthetic${index + 1}@example.invalid`, phone: '', sourceGroup: 'November joiners', workLocationId: 1, workLocation: 'Synthetic institute', offerNumber: '', releasedAt: null, templateId: 7, templateName: template.templateName, sourceFileName: 'synthetic.xlsx', sourceSheetName: 'Employees', sourceRow: index + 2, values: { candidateName: 'Synthetic employee ' + (index + 1), positionTitle: 'Synthetic coordinator', monthlyGross: '25000' }, documentIssues: [], revision: 'revision-' + index }))
// One employee deliberately needs only a position name for the simple-JD path.
rosterRows[2].values.positionTitle = ''
const offer = { id: 1, clientId: 31, applicationId: 1, candidateId: 1, candidateName: 'Synthetic native candidate', positionTitle: 'Synthetic role', offerNumber: 'OFFER-SYNTH-1', offerTemplateName: template.templateName, status: 'Draft', offeredCtc: 300000, currency: 'INR', proposedJoiningDate: '2026-12-01', approvedBudgetAmount: 0 }
const calls = [], errors = [], checks = [], unexpectedWrites = []
const workLocations = [{ id: 1, clientId: 30, name: 'Synthetic institute', isActive: true }, { id: 2, clientId: 31, name: 'Synthetic new institute', isActive: true }]
const locationWrites = [], importWrites = []
const simpleJds = new Map(), jdLinks = new Map(), jdWrites = []
function createSimpleJd(row, positionTitle) {
  const key = positionTitle.trim().replace(/\s+/g, ' ').toLowerCase()
  let jobDescription = simpleJds.get(key)
  const created = !jobDescription
  if (!jobDescription) {
    jobDescription = { id: 700 + simpleJds.size, source: 'Simple', title: positionTitle.trim(), summary: `Support the ${positionTitle.trim()} role.`, rolePurpose: `Perform the duties of ${positionTitle.trim()}.`, responsibilities: ['Complete assigned role duties.', 'Coordinate with the reporting team.'], skills: ['Role knowledge', 'Communication'] }
    simpleJds.set(key, jobDescription)
  }
  row.values.positionTitle = positionTitle.trim()
  jdLinks.set(row.id, { rosterId: row.id, jobDescriptionId: jobDescription.id, source: jobDescription.source, title: jobDescription.title })
  return { jobDescription, linkedRosterIds: [row.id], created }
}
let emptyClientHasSavedData = false
const decode = value => value.replace(/&(?:amp|lt|gt|quot|apos);/g, key => ({ '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&apos;': "'" })[key])
function readWorkbook(bytes) {
  const data = new Uint8Array(bytes), view = new DataView(data.buffer, data.byteOffset, data.byteLength), entries = new Map()
  let offset = 0
  while (view.getUint32(offset, true) === 0x04034b50) {
    assert.equal(view.getUint16(offset + 8, true), 0, 'synthetic writer uses stored ZIP entries')
    const length = view.getUint32(offset + 18, true), nameLength = view.getUint16(offset + 26, true), extra = view.getUint16(offset + 28, true), start = offset + 30 + nameLength + extra
    entries.set(new TextDecoder().decode(data.slice(offset + 30, offset + 30 + nameLength)), new TextDecoder().decode(data.slice(start, start + length)))
    offset = start + length
  }
  return [...entries.get('xl/workbook.xml').matchAll(/<sheet name="([^"]+)"[^>]*sheetId="(\d+)"/g)].map(([, name, id]) => ({ name: decode(name), rows: [...entries.get(`xl/worksheets/sheet${id}.xml`).matchAll(/<row r="(\d+)"[^>]*>(.*?)<\/row>/gs)].map(([, number, content]) => ({ number: +number, values: [...content.matchAll(/<c [^>]*>(.*?)<\/c>/gs)].map(([, cell]) => decode(/<t[^>]*>(.*?)<\/t>/s.exec(cell)?.[1] || /<v>(.*?)<\/v>/s.exec(cell)?.[1] || '')) })) }))
}
fs.writeFileSync(fixture, `import React from 'react';import ReactDOM from 'react-dom/client';import {BrowserRouter} from 'react-router-dom';import {ConfigProvider} from 'antd';import 'antd/dist/reset.css';import '/src/index.css';import '/src/AppShell.css';import '/src/RecruitmentExperience.css';import '/src/RecruitmentViewport.css';import theme from '/src/theme/hrmsTheme.ts';import AuthGate from '/src/components/AuthGate.tsx';import ToastProvider from '/src/components/ToastProvider.tsx';import AppPageHeader from '/src/components/layout/AppPageHeader.tsx';import RecruitmentPage from '/src/pages/RecruitmentPage.tsx';ReactDOM.createRoot(document.getElementById('root')).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><main style={{height:'100vh',display:'flex',flexDirection:'column',padding:16,boxSizing:'border-box',overflow:'hidden'}}><AppPageHeader recruitment title="Offers & Pre-boarding" description="" icon={null} breadcrumbs={[{title:'Talent Acquisition'},{title:'Offers & Pre-boarding'}]}/><RecruitmentPage view="Offers & Pre-Onboarding"/></main></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>);`)
fs.appendFileSync(fixture, "\nimport '/src/OrganizationSetup.css';import '/src/ModuleDrawer.css';\n")
const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module" src="/${fixtureName}"></script></body></html>`
const plugin = {
  name: 'synthetic-recruitment-roster-only', enforce: 'pre',
  load(id) { if (id.split('?')[0].replaceAll('\\', '/').endsWith('/src/services/apiBaseUrl.ts')) return 'export function resolveRuntimeApiBase(){return window.location.origin}' },
  configureServer(server) {
    server.middlewares.use(async (request, response, next) => {
      const url = new URL(request.url, 'http://127.0.0.1'), p = url.pathname
      const reply = (body, status = 200) => { response.writeHead(status, { 'content-type': 'application/json' }); response.end(JSON.stringify(body)) }
      if (p === '/roster-fixture') { response.writeHead(200, { 'content-type': 'text/html' }); response.end(await server.transformIndexHtml(request.url, html)); return }
      if (!p.startsWith('/api/')) return next()
      try {
        const call = { path: p, method: request.method, clientId: url.searchParams.get('clientId') }
        calls.push(call)
        if (p.endsWith('/document-roster/standard/template')) return reply(standardSchema(Number(url.searchParams.get('clientId')), url.searchParams.get('sourceGroup') || '', url.searchParams.get('mode')))
        if (p.endsWith('/document-roster/standard/preview') || p.endsWith('/document-roster/standard/import')) {
          const chunks=[]; for await (const chunk of request) chunks.push(chunk)
          const form=await new Response(Buffer.concat(chunks),{headers:{'content-type':request.headers['content-type']}}).formData()
          const settings=JSON.parse(form.get('settings')),file=form.get('file'),sheets=readWorkbook(await file.arrayBuffer());call.settings=settings
          assert.equal(sheets.length,1); assert.equal(sheets[0].name,'Employees')
          const header=sheets[0].rows[0].values
          const rows=sheets[0].rows.slice(1).map(row=>{
            const values=Object.fromEntries(standardColumns.map(c=>[c.key,row.values[header.findIndex(h=>h.endsWith('['+c.key+']'))]||'']))
            const existing=rosterRows.find(r=>r.candidateCode===values.profileCode)
            const action=settings.mode==='Update' ? existing?'Update':'Skip' : 'Insert'
            return {sourceRow:row.number,profileCode:values.profileCode,employeeCode:values.employeeCode,employeeName:(values.firstName+' '+values.lastName).trim(),action,reason:action==='Skip'?'Unknown Profile Code in this client/batch. Skip this row; other updates can continue.':'',changedFields:['SALARY:BASIC'],values}
          }).filter(row=>row.employeeName||row.profileCode)
          if (p.endsWith('/preview')) return reply({fileName:file.name,previewToken:'synthetic-standard',rows})
          assert.equal(settings.previewToken,'synthetic-standard')
          const items=rows.map(row=>{
            if(row.action==='Skip'||!settings.sourceRows.includes(row.sourceRow))return {...row,action:'Skip'}
            if(row.action==='Update')return {...row,action:'Updated'}
            let location=workLocations.find(l=>l.clientId===settings.clientId&&l.name===row.values.workLocation)
            if(!location&&row.values.workLocation){location={id:Math.max(...workLocations.map(l=>l.id))+1,clientId:settings.clientId,name:row.values.workLocation,isActive:true};workLocations.push(location);locationWrites.push(location)}
            rosterRows.unshift({id:200+row.sourceRow,clientId:settings.clientId,candidateId:200+row.sourceRow,candidateCode:'SYNTH-IMPORT-'+row.sourceRow,candidateName:row.employeeName,email:row.values.candidateEmail,phone:row.values.candidatePhone,sourceGroup:settings.sourceGroup,workLocationId:location?.id||null,workLocation:location?.name||'',templateId:7,templateName:template.templateName,sourceFileName:file.name,sourceSheetName:'Employees',sourceRow:row.sourceRow,values:row.values,documentIssues:[],revision:'imported-'+row.sourceRow})
            return {...row,action:'Inserted'}
          })
          importWrites.push({clientId:settings.clientId,mode:settings.mode})
          return reply({inserted:items.filter(r=>r.action==='Inserted').length,updated:items.filter(r=>r.action==='Updated').length,unchanged:0,skipped:items.filter(r=>r.action==='Skip').length,items})
        }
        const jdRowId = Number(/^\/api\/recruitment\/document-roster\/rows\/(\d+)\/simple-jd$/.exec(p)?.[1])
        if (jdRowId && request.method === 'GET') {
          const link = jdLinks.get(jdRowId)
          const description = link && [...simpleJds.values()].find(jd => jd.id === link.jobDescriptionId)
          return reply(description || { error: 'No synthetic JD linked.' }, description ? 200 : 404)
        }
        if (jdRowId && request.method === 'POST') {
          const chunks = []; for await (const chunk of request) chunks.push(chunk)
          const body = JSON.parse(Buffer.concat(chunks).toString()), row = rosterRows.find(row => row.id === jdRowId)
          assert.equal(body.revision, row.revision)
          assert.deepEqual(Object.keys(body).sort(), ['positionTitle', 'revision'], 'one-click JD has no extra required input or approval configuration')
          jdWrites.push({ rowId: jdRowId, body })
          return reply(createSimpleJd(row, body.positionTitle))
        }
        if (p.endsWith('/document-roster/simple-jds')) {
          if (request.method === 'GET') return reply([...jdLinks.values()])
          const chunks = []; for await (const chunk of request) chunks.push(chunk)
          const body = JSON.parse(Buffer.concat(chunks).toString())
          assert.deepEqual(Object.keys(body).sort(), ['ids', 'revisions'])
          const results = body.ids.map(id => {
            const row = rosterRows.find(row => row.id === id)
            assert.equal(body.revisions[id], row.revision)
            assert.ok(row.values.positionTitle, 'batch only receives rows with saved position names')
            return createSimpleJd(row, row.values.positionTitle)
          })
          jdWrites.push({ ids: body.ids })
          return reply({ created: results.filter(result => result.created).length, reused: results.filter(result => !result.created).length, linkedRows: results.length })
        }
        if (p.endsWith('/document-roster/inspect') || p.endsWith('/document-roster/preview') || p.endsWith('/document-roster/import')) {
          const chunks = []; for await (const chunk of request) chunks.push(chunk)
          const form = await new Response(Buffer.concat(chunks), { headers: { 'content-type': request.headers['content-type'] } }).formData()
          const settings = JSON.parse(form.get('settings')), file = form.get('file'), sheets = readWorkbook(await file.arrayBuffer())
          call.settings = settings
          if (p.endsWith('/inspect')) {
            const inspected = sheets.map(sheet => ({ name: sheet.name, headers: sheet.rows.find(row => row.number === settings.headerRow)?.values || [], sample: sheet.rows.find(row => row.number > settings.headerRow && row.values.some(Boolean))?.values || [] }))
            if (!inspected.some(sheet => sheet.headers.some(Boolean))) return reply({ error: 'Choose the actual employee header row.' }, 400)
            return reply(inspected)
          }
          const sheet = sheets.find(sheet => sheet.name === settings.sheetName), header = sheet.rows.find(row => row.number === settings.headerRow)
          assert.ok(header, 'preview must use the real employee header row')
          const rows = sheet.rows.filter(row => row.number > settings.headerRow && row.values[settings.mapping.candidateName]).map(row => {
            const values = Object.fromEntries(template.fields.map(field => [field.key, row.values[settings.mapping[field.key]] || '']))
            const explicit = settings.workLocationId && workLocations.find(location => location.id === settings.workLocationId && location.clientId === settings.clientId)
            const locationName = explicit?.name || values.jobLocation.trim()
            const existing = explicit || workLocations.find(location => location.clientId === settings.clientId && location.name.toLowerCase() === locationName.toLowerCase())
            return { sourceRow: row.number, candidateName: values.candidateName, email: values.candidateEmail, phone: values.candidatePhone, positionTitle: values.positionTitle, workLocation: locationName, workLocationId: existing?.id || null, workLocationStatus: existing ? 'Existing' : locationName ? 'New' : 'Unassigned', matchStatus: 'New profile', candidateId: null, importIssues: [], documentIssues: [], values }
          })
          if (p.endsWith('/import')) {
            assert.equal(settings.previewToken, 'synthetic-only')
            const previousLocationCount = locationWrites.length
            const selected = rows.filter(row => settings.sourceRows.includes(row.sourceRow))
            const saved = selected.map(row => {
              let locationId = row.workLocationId
              if (!locationId && row.workLocation) {
                const location = { id: Math.max(...workLocations.map(location => location.id)) + 1, clientId: settings.clientId, name: row.workLocation, isActive: true }
                workLocations.push(location); locationWrites.push(location); locationId = location.id
              }
              return { id: 200 + row.sourceRow, clientId: settings.clientId, candidateId: 200 + row.sourceRow, candidateCode: 'SYNTH-IMPORT-' + row.sourceRow, candidateName: row.candidateName, email: row.email, phone: row.phone, sourceGroup: settings.sourceGroup, workLocationId: locationId, workLocation: row.workLocation, templateId: settings.templateId, templateName: template.templateName, sourceFileName: file.name, sourceSheetName: sheet.name, sourceRow: row.sourceRow, values: row.values, documentIssues: [], revision: 'imported-' + row.sourceRow }
            })
            rosterRows.unshift(...saved); importWrites.push({ clientId: settings.clientId, rowIds: saved.map(row => row.id) })
            return reply({ createdProfiles: saved.length, reusedProfiles: 0, savedRows: saved.length, createdWorkLocations: locationWrites.length - previousLocationCount, errors: [] })
          }
          return reply({ fileName: file.name, fileHash: 'synthetic-only', previewToken: 'synthetic-only', sheets: sheets.map(sheet => sheet.name), sheetName: sheet.name, headers: header.values, mapping: settings.mapping, fields: template.fields, rows })
        }
        if (request.method !== 'GET') { unexpectedWrites.push({ method: request.method, path: p }); return reply({ error: 'Mutation outside this synthetic fixture is blocked.' }, 403) }
        if (p === '/api/auth/me') return reply(user)
        if (p === '/api/clients') return reply(clients)
        if (p.endsWith('/document-roster/client-summary')) return reply([{ clientId: 30, rosterCount: rosterRows.length, offerCount: 0 }, { clientId: 31, rosterCount: 0, offerCount: 1 }, { clientId: 32, rosterCount: emptyClientHasSavedData ? 1 : 0, offerCount: 0 }])
        if (p.endsWith('/document-roster/templates')) return reply([{ ...template, clientId: +(url.searchParams.get('clientId') || 30) }])
        if (p.endsWith('/document-roster/rows')) return reply(url.searchParams.get('clientId') === '30' ? rosterRows : [])
        if (p.endsWith('/document-roster/work-locations')) return reply(workLocations.filter(location => location.clientId === Number(url.searchParams.get('clientId'))))
        if (p.endsWith('/document-roster/mail-jobs')) return reply({ items: [] })
        if (p === '/api/recruitment/offers') return reply([offer])
        return reply([])
      } catch (error) { errors.push(String(error)); return reply({ error: String(error) }, 500) }
    })
  },
}
const server = await createServer({ root, configFile: false, plugins: [plugin, react()], server: { host: '127.0.0.1', port: 5198, strictPort: true, open: false } })
let browser
try {
  await server.listen()
  browser = await chromium.launch({ headless: true })
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/*', route => ['localhost', '127.0.0.1'].includes(new URL(route.request().url()).hostname) ? route.continue() : route.abort())
  const open = async id => { await page.goto(`http://127.0.0.1:5198/roster-fixture?clientId=${id}`); await expect(page.getByRole('button', { name: /Employee lists & offer letters$/ })).toBeVisible() }
  await open(30)
  await page.getByTestId('recruitment-client-scope').click()
  const saved = page.locator('.ant-select-item-option').filter({ hasText: 'Synthetic saved roster' })
  const native = page.locator('.ant-select-item-option').filter({ hasText: 'Synthetic native offers' })
  const empty = page.locator('.ant-select-item-option').filter({ hasText: 'Synthetic empty client' })
  await expect(saved).toHaveClass(/excel-payslip-client-has-batches/)
  await expect(native).toHaveClass(/excel-payslip-client-has-batches/)
  await expect(empty).toHaveClass(/excel-payslip-client-no-batches/)
  await expect(saved).toContainText('Saved offers / employee lists')
  await expect(empty).not.toContainText('Saved offers / employee lists')
  await page.screenshot({ path: path.join(output, 'saved-client-options.png'), fullPage: true })
  await page.keyboard.press('Escape')
  checks.push('Roster and native-offer clients receive green saved-data options; empty client remains normal')
  console.log('Verified saved-data client options')
  await page.getByRole('button', { name: /Employee lists & offer letters$/ }).click()
  const roster = page.locator('.recruitment-document-roster')
  await expect(roster).toContainText('35 profiles')
  await expect(roster).toContainText('All import batches')
  for (const [width, height] of [[1440, 900], [768, 1024], [390, 844]]) {
    await page.setViewportSize({ width, height })
    if (width < 600) { await roster.getByRole('button', { name: 'More', exact: true }).click(); await page.getByRole('menuitem', { name: 'Import employee list', exact: true }).click() }
    else await roster.getByRole('button', { name: /Import employee list$/ }).click()
    const drawer = page.locator('.document-roster-import')
    const download = drawer.getByRole('button', { name: /Download standard template$/ })
    await download.scrollIntoViewIfNeeded(); await expect(download).toBeInViewport()
    await expect.poll(async () => {
      const box = await download.boundingBox()
      return !!box && box.x >= 0 && box.x + box.width <= width + 1
    }, { message: `${width}px template download remains inside viewport after drawer motion` }).toBe(true)
    for (const button of await drawer.locator('.ant-drawer-footer .ant-btn').all()) {
      await expect.poll(async () => {
        const footerBox = await button.boundingBox()
        return !!footerBox && footerBox.x >= 0 && footerBox.x + footerBox.width <= width + 1
      }, { message: `${width}px footer actions remain inside viewport after drawer motion` }).toBe(true)
    }
    await page.screenshot({ path: path.join(output, `template-${width}.png`), fullPage: true, animations: 'disabled' })
    await drawer.getByRole('button', { name: 'Cancel', exact: true }).click()
    checks.push(`${width}px import template control visible and unclipped`)
    console.log(`Verified template control at ${width}px`)
  }
  await page.setViewportSize({ width: 1440, height: 900 })
  const scroll = roster.locator('.ant-table-body')
  assert.ok(await scroll.evaluate(element => element.scrollHeight > element.clientHeight), 'employee list has its own vertical scroll')
  await scroll.evaluate(element => { element.scrollTop = element.scrollHeight })
  checks.push('35-row roster has contained vertical scrolling and import-batch labels')
  await scroll.evaluate(element => { element.scrollTop = 0 })
  const row = id => roster.locator(`.ant-table-tbody tr[data-row-key="${id}"]`)
  await row(1).getByRole('button', { name: /Create JD$/ }).click()
  await expect(row(1).getByRole('button', { name: /View JD$/ })).toBeVisible()
  assert.equal(jdWrites.length, 1)
  assert.equal(simpleJds.size, 1)
  assert.equal(await page.locator('.ant-modal:visible').count(), 0, 'creating a JD requires no modal or review')
  assert.equal(await page.locator('.ant-drawer-open').count(), 1, 'only the original employee list stays open after create')
  checks.push('Saved designation creates a JD in one click without review, a modal or additional fields')
  await row(2).getByRole('button', { name: /Create JD$/ }).click()
  await expect(row(2).getByRole('button', { name: /View JD$/ })).toBeVisible()
  assert.equal(simpleJds.size, 1, 'another employee in the same role reuses the existing JD')
  assert.equal(jdLinks.get(1).jobDescriptionId, jdLinks.get(2).jobDescriptionId)
  checks.push('A second employee with the same position reuses the JD')
  await row(3).getByRole('textbox', { name: /Position name/i }).fill('Synthetic support specialist')
  await row(3).getByRole('button', { name: /Create JD$/ }).click()
  await expect(row(3).getByRole('button', { name: /View JD$/ })).toBeVisible()
  assert.equal(simpleJds.size, 2)
  assert.equal(jdWrites.at(-1).body.positionTitle, 'Synthetic support specialist')
  assert.equal(await page.locator('.ant-modal:visible').count(), 0)
  checks.push('Missing designation needs only one inline position name before Create JD')
  await row(1).getByRole('button', { name: /View JD$/ }).click()
  const jdDrawer = page.locator('.document-roster-jd-details')
  await expect(jdDrawer.locator('.ant-drawer-title')).toContainText('Synthetic coordinator')
  await expect(jdDrawer).toContainText('Complete assigned role duties.')
  await expect(jdDrawer.getByRole('heading', { name: 'Responsibilities', exact: true })).toBeInViewport()
  await expect(jdDrawer.getByText('Complete assigned role duties.', { exact: true })).toBeInViewport()
  await expect.poll(async () => {
    const box = await jdDrawer.boundingBox()
    return !!box && box.width > 500 && box.x >= 0 && box.x + box.width <= 1441
  }, { message: 'JD drawer finishes sliding fully into view before capture' }).toBe(true)
  await expect(jdDrawer.getByRole('button', { name: /Save|Submit|Approve|Review/i })).toHaveCount(0)
  await page.screenshot({ path: path.join(output, 'simple-jd-view.png'), fullPage: true, animations: 'disabled' })
  await jdDrawer.getByRole('button', { name: 'Close', exact: true }).first().click()
  checks.push('View JD opens existing details read-only without approval controls')
  await row(4).getByRole('checkbox').check()
  await row(5).getByRole('checkbox').check()
  await roster.getByRole('button', { name: /Create selected JDs/ }).click()
  await expect(row(4).getByRole('button', { name: /View JD$/ })).toBeVisible()
  await expect(row(5).getByRole('button', { name: /View JD$/ })).toBeVisible()
  assert.deepEqual(jdWrites.at(-1).ids, [4, 5])
  assert.equal(simpleJds.size, 2, 'batch reuses the already-created role JD')
  checks.push('Selected saved-designation rows create or reuse JDs together without additional forms')
  await roster.getByRole('button', { name: /Import employee list$/ }).click()
  const drawer = page.locator('.document-roster-import')
  const downloadEvent = page.waitForEvent('download')
  await drawer.getByRole('button', { name: /Download standard template$/ }).click()
  const download = await downloadEvent, blankFile = path.join(output, download.suggestedFilename())
  await download.saveAs(blankFile)
  const workbook = readWorkbook(fs.readFileSync(blankFile))
  assert.equal(workbook.length,1); assert.equal(workbook[0].name,'Employees')
  assert.ok(workbook[0].rows[0].values.some(value=>value.startsWith('__FREVO_TEMPLATE__:')))
  const insertionSchema=standardSchema(30,'Employee list','Insert')
  assert.deepEqual(workbook[0].rows[0].values.slice(0,standardColumns.length),standardColumns.map(c=>c.header))
  const data=[{firstName:'Synthetic upload employee',candidateEmail:'synthetic.upload@example.invalid',candidatePhone:'9000012345',positionTitle:'Synthetic coordinator',joiningDate:'2026-12-01','SALARY:BASIC':'25000',workLocation:'Synthetic institute'},
    {firstName:'Synthetic new location employee',candidateEmail:'synthetic.newlocation@example.invalid',candidatePhone:'9000012346',positionTitle:'Synthetic coordinator',joiningDate:'2026-12-01','SALARY:BASIC':'25000',workLocation:'Synthetic new institute'}]
  const filledSheets=standardRosterSheets(insertionSchema,'Insert')
  filledSheets[0].rows=[filledSheets[0].rows[0],...data.map(row=>[...standardColumns.map(c=>row[c.key]||''),''])]
  const filledFile=path.join(output,'synthetic-standard-insertion.xlsx')
  fs.writeFileSync(filledFile,Buffer.from(await buildXlsxBlob(filledSheets).arrayBuffer()))
  await drawer.locator('input[type=file]').setInputFiles(filledFile)
  await drawer.getByRole('button',{name:'Preview standard workbook',exact:true}).click()
  await expect(drawer).toContainText('Synthetic new location employee')
  assert.deepEqual(locationWrites,[]); assert.deepEqual(importWrites,[])
  await drawer.getByRole('button',{name:/Insert selected \(2\)/}).click()
  await expect(drawer).toContainText('2 inserted')
  await drawer.getByRole('button',{name:'Done',exact:true}).click()
  await expect(roster).toContainText('Synthetic new location employee')
  assert.equal(locationWrites.length,1); assert.equal(locationWrites[0].clientId,30)
  assert.equal(locationWrites[0].name,'Synthetic new institute')
  assert.equal(workLocations.filter(location=>location.name==='Synthetic new institute').length,2)
  checks.push('One-sheet standard insertion downloads and uploads without manual mapping; preview has no writes and save creates the permitted client location')
  await roster.getByRole('button',{name:'Update Corrected Version',exact:true}).click()
  await drawer.locator('.ant-select[aria-label="Corrected version import batch"]').click()
  await page.locator('.ant-select-dropdown:visible .ant-select-item-option').filter({hasText:/^November joiners$/}).click()
  await expect(drawer.getByRole('button',{name:/Download current version$/})).toBeEnabled()
  const correctionSchema=standardSchema(30,'November joiners','Update')
  correctionSchema.rows=[{...correctionSchema.rows.find(row=>row.profileCode==='SYNTH-001'),'SALARY:BASIC':'27000'},
    {profileCode:'UNKNOWN-SYNTH',firstName:'Synthetic unmatched row',revision:'a'.repeat(64)}]
  const correctedFile=path.join(output,'synthetic-standard-correction.xlsx')
  fs.writeFileSync(correctedFile,Buffer.from(await buildXlsxBlob(standardRosterSheets(correctionSchema,'Update')).arrayBuffer()))
  await drawer.locator('input[type=file]').setInputFiles(correctedFile)
  await drawer.getByRole('button',{name:'Preview standard workbook',exact:true}).click()
  await expect(drawer).toContainText('Unknown Profile Code')
  await drawer.getByRole('button',{name:'Update selected (1) · Skip 1',exact:true}).click()
  await expect(drawer).toContainText('1 updated · 0 inserted · 0 unchanged · 1 skipped')
  await drawer.getByRole('button',{name:'Done',exact:true}).click()
  checks.push('Corrected versions match Profile Code and complete valid rows while retaining an unknown-code Skip result')
  await open(31); await expect(page.getByText('Synthetic native candidate', { exact: true })).toBeVisible()
  await open(32); await expect(page.getByText('Synthetic native candidate', { exact: true })).toHaveCount(0)
  checks.push('Native-offer and empty clients remain separately scoped')
  await page.screenshot({ path: path.join(output, 'empty-client.png'), fullPage: true })
  const summariesBefore = calls.filter(call => call.path.endsWith('/client-summary')).length
  emptyClientHasSavedData = true
  await page.evaluate(() => window.dispatchEvent(new Event('recruitment:roster-changed')))
  await page.getByTestId('recruitment-client-scope').click()
  await expect(page.locator('.ant-select-item-option').filter({ hasText: 'Synthetic empty client' })).toHaveClass(/excel-payslip-client-has-batches/)
  assert.ok(calls.filter(call => call.path.endsWith('/client-summary')).length > summariesBefore)
  checks.push('Saved-data colors refresh after the roster-changed event using read-only summary requests')
  assert.deepEqual(errors, []); assert.deepEqual(unexpectedWrites, [])
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ checks, calls, jdWrites, locationWrites, importWrites, errors, unexpectedWrites, liveDatabaseWrites: 0, upstreamRequests: 0 }, null, 2))
  console.log(JSON.stringify({ passed: checks.length, evidence: output, liveDatabaseWrites: 0, upstreamRequests: 0 }))
} catch (error) {
  fs.writeFileSync(path.join(output, 'failure.json'), JSON.stringify({ error: String(error), checks, errors, calls, unexpectedWrites }, null, 2))
  if (browser) { const page = browser.contexts()[0]?.pages()[0]; if (page) { await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => {}); fs.writeFileSync(path.join(output, 'failure.html'), await page.content()) } }
  console.error(error); process.exitCode = 1
} finally { await browser?.close(); await server.close(); fs.rmSync(fixture, { force: true }) }
