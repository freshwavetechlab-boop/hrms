import fs from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'

// This fixture uses only process memory. Every /api request is handled here;
// there is no proxy, database, outbound email or production API connection.
const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '../payroll-ui')
const require = createRequire(path.join(root, 'package.json'))
const { createServer } = await import(pathToFileURL(require.resolve('vite')).href)
const entry = path.join(root, '.codex-candidate-documents.tsx')
const port = 5207
const completed = new Set(['complete'])
const failures = new Set()
const submissions = []
const mailJobs = new Map()
const documentNames = ['Aadhaar card', 'PAN card', 'Passport-size photograph', 'Bank account proof', 'Date of birth proof', 'Highest qualification certificate', 'Previous employment proof', 'Relieving letter', 'Experience certificate', 'Address proof', 'Other supporting document']
const fields = documentNames.map((label, index) => ({ fieldConfigurationId: 81 + index, label, isRequired: true }))
const documents = [
  { id: 'proof', label: 'Identity proof', required: true, minimumFileCount: 1, maximumFileCount: 1, maximumFileSizeBytes: 5 * 1024 * 1024, allowedExtensions: ['.pdf', '.jpg', '.png'], requiresDocumentNumber: false, requiresIssueDate: false, requiresExpiryDate: false },
  { id: 'supporting', label: 'Supporting documents with a deliberately long title for the mobile layout', required: false, minimumFileCount: 0, maximumFileCount: 3, maximumFileSizeBytes: 5 * 1024 * 1024, allowedExtensions: ['.pdf', '.jpg', '.png'], requiresDocumentNumber: false, requiresIssueDate: false, requiresExpiryDate: false },
]
const recipients = [1, 2, 3].map(id => ({ rosterId: id, workLocationId: 17, candidateCode: `SYN-CAN-${id}`, candidateName: id === 1 ? 'Synthetic candidate with a deliberately long full name' : `Synthetic candidate ${id}`, email: `synthetic${id}@example.invalid`, releaseId: id + 100, queueId: null, status: 'Ready', message: 'Released PDF is ready for this synthetic test.', sentCopyStatus: 'Not sent', deliveryStatus: 'Not queued' }))

function samplePdf() {
  const content = 'BT /F1 14 Tf 36 100 Td (Synthetic candidate document - local test only) Tj ET'
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>', '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 160] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>', '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>', `<< /Length ${Buffer.byteLength(content)} >>\nstream\n${content}\nendstream`]
  let pdf = '%PDF-1.4\n', offsets = [0]
  objects.forEach((object, index) => { offsets.push(Buffer.byteLength(pdf)); pdf += `${index + 1} 0 obj\n${object}\nendobj\n` })
  const xref = Buffer.byteLength(pdf)
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.slice(1).map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`
  return Buffer.from(pdf)
}
const sample = samplePdf()
const samplePath = path.join(os.tmpdir(), 'frevo-synthetic-candidate-document.pdf')
await fs.writeFile(samplePath, sample)
const bytes = async req => { const chunks = []; for await (const chunk of req) chunks.push(chunk); return Buffer.concat(chunks) }
const json = async req => JSON.parse((await bytes(req)).toString('utf8') || '{}')
const plugin = {
  name: 'isolated-candidate-documents', enforce: 'pre',
  resolveId(id) { if (id === '/.codex-candidate-documents.tsx') return entry },
  async load(id) {
    const normal = id.split('?')[0].replaceAll('\\', '/')
    if (normal === entry.replaceAll('\\', '/')) { this.addWatchFile(path.join(here, 'fixtures/CandidateDocuments.fixture.tsx')); return fs.readFile(path.join(here, 'fixtures/CandidateDocuments.fixture.tsx'), 'utf8') }
    if (normal.endsWith('/src/services/apiBaseUrl.ts')) return 'export function resolveRuntimeApiBase(){return window.location.origin}'
  },
  configureServer(server) {
    server.middlewares.use(async (req, res, next) => {
      const url = new URL(req.url, `http://127.0.0.1:${port}`), route = url.pathname
      const reply = (body, status = 200) => { res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store' }); res.end(JSON.stringify(body)) }
      try {
        if (route === '/document-verification' || route.startsWith('/candidate-documents/')) { res.writeHead(200, { 'content-type': 'text/html' }); res.end(await server.transformIndexHtml(req.url, '<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="referrer" content="no-referrer"></head><body><div id="root"></div><script type="module" src="/.codex-candidate-documents.tsx"></script></body></html>')); return }
        if (route === '/__fixture/sample.pdf') { res.writeHead(200, { 'content-type': 'application/pdf', 'content-disposition': 'attachment; filename="synthetic-candidate-document.pdf"' }); res.end(sample); return }
        if (route === '/__fixture/state') return reply({ completed: [...completed], submissions, mailJobs: [...mailJobs.values()] })
        if (route === '/__fixture/reset' && req.method === 'POST') { completed.clear(); completed.add('complete'); failures.clear(); submissions.length = 0; mailJobs.clear(); return reply({ ok: true }) }
        if (!route.startsWith('/api/')) return next()
        if (route.startsWith('/api/public/recruitment/document-requests/')) {
          const token = decodeURIComponent(route.split('/').at(-1))
          if (req.method === 'GET') {
            if (token === 'load-error') return reply({ error: 'Synthetic load failure. Please try again.' }, 503)
            const status = completed.has(token) ? 'Completed' : token === 'expired' ? 'Expired' : token === 'unavailable' ? 'Unavailable' : 'Open'
            return reply({ status, message: '', expiresAtUtc: '2099-12-31T23:59:59Z', documents: status === 'Open' ? documents : [] })
          }
          if (req.method === 'POST') {
            if (completed.has(token)) return reply({ status: 'Completed', alreadySubmitted: true, message: 'Already submitted.' })
            if (token === 'expired') return reply({ error: 'This synthetic link expired.' }, 410)
            const form = await new Request(`http://127.0.0.1:${port}${route}`, { method: 'POST', headers: req.headers, body: await bytes(req) }).formData()
            if (token === 'retry' && !failures.has(token)) { failures.add(token); return reply({ error: 'Synthetic temporary failure. Your files are retained. Submit again to retry.' }, 503) }
            if (String(form.get('email')).trim().toLowerCase() !== 'synthetic@example.invalid') return reply({ error: 'Use synthetic@example.invalid for this local test.' }, 400)
            const file = form.get('files-proof')
            if (!file || typeof file === 'string' || !file.size) return reply({ error: 'Identity proof is required.' }, 400)
            const metadata = JSON.parse(String(form.get('metadata') || '{}'))
            submissions.push({ token, email: form.get('email'), metadata, files: [...form].filter(([, value]) => typeof value !== 'string').map(([key, value]) => ({ key, name: value.name, size: value.size })) })
            completed.add(token)
            return reply({ status: 'Completed', alreadySubmitted: false, message: 'Documents submitted.' })
          }
        }
        if (route.endsWith('/document-roster/document-request-fields') && req.method === 'GET') return reply({ items: fields })
        if (route.endsWith('/document-roster/mail-preview') && req.method === 'POST') return reply({ items: recipients })
        if (route.endsWith('/document-roster/mail-jobs') && req.method === 'POST') {
          const body = await json(req)
          const existing = mailJobs.get(body.requestId)
          if (existing) {
            if (JSON.stringify(existing.fixtureRequest) !== JSON.stringify(body)) return reply({ error: 'Synthetic request ID was reused with different selections.' }, 409)
            return reply(existing)
          }
          if (!Array.isArray(body.documentRequests) || !body.documentRequests.length) return reply({ error: 'Select at least one document.' }, 400)
          const job = { id: body.requestId, clientId: 55, clientName: 'Synthetic client', createdBy: 'Synthetic tester', dismissedAtUtc: null, isComplete: false, total: 3, completed: 0, sent: 0, failed: 0, skipped: 0, queued: 3, percentComplete: 0, items: recipients, fixtureRequest: body }
          mailJobs.set(body.requestId, job)
          if (url.searchParams.has('failFirst') || new URL(req.headers.referer || 'http://fixture').searchParams.has('mailRetry')) {
            if (!failures.has(body.requestId)) { failures.add(body.requestId); return reply({ error: 'Synthetic uncertain response. Retry to retrieve the same job.' }, 503) }
          }
          return reply(job)
        }
        return reply({ error: `Local fixture has no endpoint for ${req.method} ${route}. No external API was contacted.` }, 404)
      } catch (error) { return reply({ error: `Synthetic fixture error: ${error.message}` }, 500) }
    })
  },
}
const server = await createServer({ root, configFile: false, plugins: [plugin], server: { host: '127.0.0.1', port, strictPort: true }, cacheDir: path.join(os.tmpdir(), 'frevo-candidate-documents-vite-cache') })
await server.listen()
console.log(`Synthetic document verification: http://127.0.0.1:${port}/document-verification`)
console.log(`Public open: http://127.0.0.1:${port}/candidate-documents/open`)
console.log(`Test email: synthetic@example.invalid\nSample PDF: ${samplePath}`)
