import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

function load(file, imports = {}) {
  const exports = {}, source = readFileSync(new URL('../src/' + file + '.ts', import.meta.url), 'utf8')
  new Function('exports', 'require', ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports, path => imports[path])
  return exports
}
const { validateCandidateDocuments: validate, validateDocumentFiles, candidateDocumentMetadata, documentAccept } = load('utils/candidateDocumentUpload')
const document = { id: 'syn-proof', label: 'Identity proof', required: true, minimumFileCount: 1, maximumFileCount: 2, maximumFileSizeBytes: 10000, allowedExtensions: ['pdf', '.jpg'], requiresDocumentNumber: true, requiresIssueDate: false, requiresExpiryDate: false }
const selected = () => ({ files: [{ name: 'synthetic.PDF', size: 9000 }], documentNumber: 'SYN-001', issueDate: '', expiryDate: '' })

test('candidate must enter email, required documents and only configured metadata', () => {
  assert.match(validate([document], '', {}), /email address/)
  assert.match(validate([document], 'synthetic@example.invalid', {}), /Identity proof/)
  assert.match(validate([document], 'synthetic@example.invalid', { [document.id]: { ...selected(), documentNumber: '' } }), /document number/)
  assert.equal(validate([document], 'synthetic@example.invalid', { [document.id]: selected() }), '')
  assert.match(validate([{ ...document, required: false, minimumFileCount: 0 }], 'synthetic@example.invalid', {}), /at least one document/)
})

test('optional documents can be omitted, with no number or dates required until a file is selected', () => {
  const optional = { ...document, id: 'optional', label: 'Optional proof', required: false, minimumFileCount: 0, requiresIssueDate: true, requiresExpiryDate: true }
  assert.equal(validate([document, optional], 'synthetic@example.invalid', { [document.id]: selected() }), '')
  assert.match(validate([optional], 'synthetic@example.invalid', { [optional.id]: { ...selected(), files: [], documentNumber: '', issueDate: '', expiryDate: '' } }), /at least one document/)
  assert.match(validate([optional], 'synthetic@example.invalid', { [optional.id]: { ...selected(), documentNumber: '' } }), /document number/)
})

test('aggregate file count and bytes allow exact boundaries and reject excess before upload', () => {
  const limit = 100 * 1024 * 1024
  const rule = { ...document, requiresDocumentNumber: false, maximumFileCount: 65, maximumFileSizeBytes: limit + 1 }
  const files = count => Array.from({ length: count }, (_, index) => ({ name: `proof-${index}.pdf`, size: 1 }))
  assert.equal(validate([rule], 'synthetic@example.invalid', { [rule.id]: { ...selected(), files: files(64) } }), '')
  assert.match(validate([rule], 'synthetic@example.invalid', { [rule.id]: { ...selected(), files: files(65) } }), /up to 64 files in total/)
  assert.equal(validate([rule], 'synthetic@example.invalid', { [rule.id]: { ...selected(), files: [{ name: 'proof.pdf', size: limit }] } }), '')
  const other = { ...rule, id: 'other' }
  const selections = { [rule.id]: { ...selected(), files: [{ name: 'proof.pdf', size: limit }] }, [other.id]: { ...selected(), files: [{ name: 'other.pdf', size: 1 }] } }
  assert.match(validate([rule, other], 'synthetic@example.invalid', selections), /combined size.*100 MB/)
  assert.equal(selections[rule.id].files[0].size, limit, 'validation keeps selected files available for correction')
})

test('document numbers match the 180-character server boundary', () => {
  assert.equal(validate([document], 'synthetic@example.invalid', { [document.id]: { ...selected(), documentNumber: 'A'.repeat(180) } }), '')
  assert.match(validate([document], 'synthetic@example.invalid', { [document.id]: { ...selected(), documentNumber: 'A'.repeat(181) } }), /180 characters or fewer/)
})

test('file limits and allowed types are checked without losing any selected file', () => {
  const files = selected().files
  assert.equal(validateDocumentFiles(document, files), '')
  assert.equal(documentAccept(document), '.pdf,.jpg')
  assert.match(validateDocumentFiles(document, [{ name: 'synthetic.pdf', size: 0 }]), /empty/)
  assert.match(validateDocumentFiles(document, [{ name: 'synthetic.exe', size: 100 }]), /use .pdf, .jpg/)
  assert.match(validateDocumentFiles(document, [{ name: 'synthetic.pdf', size: 10001 }]), /no larger/)
  assert.match(validateDocumentFiles(document, [...files, ...files, ...files]), /up to 2/)
  assert.deepEqual(files, [{ name: 'synthetic.PDF', size: 9000 }])
})

test('required dates and ordered issue/expiry dates remain explicit candidate inputs', () => {
  const rule = { ...document, requiresIssueDate: true, requiresExpiryDate: true }
  assert.match(validate([rule], 'synthetic@example.invalid', { [rule.id]: selected() }), /issue date/)
  assert.match(validate([rule], 'synthetic@example.invalid', { [rule.id]: { ...selected(), issueDate: '2026-01-02', expiryDate: '2026-01-01' } }), /cannot be before/)
  assert.equal(validate([rule], 'synthetic@example.invalid', { [rule.id]: { ...selected(), issueDate: '2026-01-01', expiryDate: '2027-01-01' } }), '')
})

test('metadata includes only selected known requirements and never adds a login or client', () => {
  const metadata = candidateDocumentMetadata([document], { [document.id]: selected(), unknown: selected() })
  assert.deepEqual(metadata, [{ id: document.id, documentNumber: 'SYN-001', issueDate: undefined, expiryDate: undefined }])
  assert.deepEqual(candidateDocumentMetadata([document], {}), [])
})

test('public GET and one final multipart POST do not send credentials or authentication headers', async () => {
  const service = load('services/publicCandidateDocumentService', { './apiClient': { apiUrl: path => 'https://example.invalid' + path } })
  const originalFetch = globalThis.fetch, originalWindow = globalThis.window
  const requests = []
  globalThis.window = { setTimeout, clearTimeout }
  globalThis.fetch = async (url, options) => { requests.push({ url, options }); return new Response(JSON.stringify({ status: 'Completed', alreadySubmitted: true, message: 'Already submitted', documents: [] }), { status: 200 }) }
  try {
    const loaded = await service.getCandidateDocumentRequest('synthetic/link')
    assert.equal(loaded.data.status, 'Completed')
    const file = new File(['synthetic document'], 'synthetic.pdf', { type: 'application/pdf' })
    const saved = await service.submitCandidateDocuments('synthetic/link', ' synthetic@example.invalid ', [{ id: document.id, documentNumber: 'SYN-001' }], { [document.id]: [file] })
    assert.equal(saved.data.alreadySubmitted, true)
    for (const request of requests) {
      assert.match(request.url, /\/api\/public\/recruitment\/document-requests\/synthetic%2Flink$/)
      assert.equal(request.options.credentials, 'omit'); assert.equal(request.options.referrerPolicy, 'no-referrer'); assert.equal(request.options.cache, 'no-store')
      assert.deepEqual(request.options.headers, { Accept: 'application/json' })
    }
    const body = requests[1].options.body
    assert.equal(body.get('email'), 'synthetic@example.invalid')
    assert.equal(body.get(`files-${document.id}`).name, 'synthetic.pdf')
    assert.deepEqual(JSON.parse(body.get('metadata')), { documents: [{ id: document.id, documentNumber: 'SYN-001' }] })
    assert.deepEqual([...body.keys()].sort(), ['email', `files-${document.id}`, 'metadata'].sort())
  } finally { globalThis.fetch = originalFetch; globalThis.window = originalWindow }
})
