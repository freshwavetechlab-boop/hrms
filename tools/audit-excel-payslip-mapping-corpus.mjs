import fs from 'node:fs'
import path from 'node:path'
import { createHash } from 'node:crypto'
import { pathToFileURL } from 'node:url'
const root = process.argv[2]
const output = process.argv[3]
if (!root || !output) throw Error('Supply the authorized workbook folder and local audit output.')
const modulePath = process.env.HRMS_PLAYWRIGHT_MODULE || path.resolve('tools/dev-launcher/node_modules/playwright/test.mjs')
const { chromium } = await import(pathToFileURL(modulePath).href)
const origin = process.env.HRMS_TEST_UI_URL || 'http://localhost:5173'
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Local test UI only.')
const walk = directory => fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => entry.isDirectory() ? walk(path.join(directory, entry.name)) : /\.xlsx$/i.test(entry.name) && !entry.name.startsWith('~$') ? [path.join(directory, entry.name)] : [])
const files = walk(root).sort(), results = [], fingerprints = new Map()
const browser = await chromium.launch({ headless: true })
const context = await browser.newContext(), page = await context.newPage()
let apiRequests = 0, externalRequests = 0
await context.route('**/*', route => {
  const url = new URL(route.request().url())
  if (url.origin !== new URL(origin).origin) { externalRequests++; return route.abort() }
  if (url.pathname.startsWith('/api/')) { apiRequests++; return route.abort() }
  if (url.pathname === '/mapping-corpus-audit') return route.fulfill({ contentType: 'text/html', body: '<!doctype html><html><body>Read-only local workbook mapping audit</body></html>' })
  return route.continue()
})
try {
  await page.goto(origin + '/mapping-corpus-audit')
  for (const [i, file] of files.entries()) {
    const data = fs.readFileSync(file), sha256 = createHash('sha256').update(data).digest('hex')
    if (fingerprints.has(sha256)) {
      results.push({ fileKey: sha256.slice(0, 16), duplicateContent: true })
      continue
    }
    fingerprints.set(sha256, i)
    const result = await page.evaluate(async ({ bytes, fileKey }) => {
      try {
        const imp = await import('/src/utils/excelPayslipImport.ts')
        const mapping = await import('/src/utils/excelPayslipMapping.ts')
        const { suggestPayslipWorksheet } = await import('/src/utils/excelPayslipConsolidation.ts')
        const data = Uint8Array.from(atob(bytes), c => c.charCodeAt(0))
        const sheets = await imp.parseExcelPayslipFile(new File([data], 'source.xlsx'))
        let cells = 0, formulaCells = 0, missingCaches = 0, formulaCaptureErrors = 0, excelErrors = 0
        const functions = {}, captureErrorCategories = {}
        for (const sheet of sheets) for (const row of sheet.rows) for (const cell of row.cells) {
          if (!cell) continue
          cells++
          if (cell.missingCachedValue) missingCaches++
          if (cell.calculationError) {
            formulaCaptureErrors++
            const category = /shared/i.test(cell.calculationError) ? 'Shared formula capture' : /array/i.test(cell.calculationError) ? 'Array formula capture' : /expression/i.test(cell.calculationError) ? 'Missing expression' : 'Other formula capture'
            captureErrorCategories[category] = (captureErrorCategories[category] || 0) + 1
          }
          if (cell.error) excelErrors++
          if (!cell.formula && !cell.formulaText) continue
          formulaCells++
          const expression = (cell.formulaText || '').replace(/"(?:[^"]|"")*"/g, '""').replace(/'(?:[^']|'')+'!/g, 'SHEET!')
          for (const match of expression.matchAll(/\b([A-Z_][A-Z0-9._]*)\s*\(/gi)) {
            const name = match[1].toUpperCase()
            functions[name] = (functions[name] || 0) + 1
          }
        }
        const selected = suggestPayslipWorksheet(sheets)
        let salary = null
        if (selected) {
          const headers = imp.payslipHeaders(selected.sheet, selected.headerRow)
          const columns = imp.defaultPayslipMapping(headers, selected.sheet, selected.headerRow)
          const initial = imp.buildExcelPayslipRows(selected.sheet, selected.headerRow, columns)
          const suggestion = imp.applySuggestedPayslipBlankAmounts(selected.sheet, selected.headerRow, columns, initial.excludedRows)
          const after = imp.buildExcelPayslipRows(selected.sheet, selected.headerRow, suggestion.columns)
          const evidence = mapping.buildPayslipMappingEvidence(selected.sheet, selected.headerRow, suggestion.columns, '', initial.excludedRows, sheets)
          const categories = {}
          for (const issue of after.issues) {
            const category = /no saved result/i.test(issue.message) ? 'Missing formula cache' : /Saved Excel error/i.test(issue.message) ? 'Excel cell error' : /numeric amount/i.test(issue.message) ? 'Missing or invalid amount' : /repeated/i.test(issue.message) ? 'Repeated source code' : 'Mapping structure'
            categories[category] = (categories[category] || 0) + 1
          }
          salary = { sheetIndex: sheets.indexOf(selected.sheet), headerRow: selected.headerRow,
            candidateRows: initial.rows.length, issueCellsBefore: initial.issues.length, issueCellsAfter: after.issues.length,
            literalBlanksHandled: suggestion.blankCellCount, zeroPolicyColumns: suggestion.zeroedColumns.map(c => c.columnIndex),
            remainingCategories: categories, crossSheetEvidenceColumns: evidence.columns.filter(c => c.crossSheetDependencies.length).length,
            incompleteEvidenceColumns: evidence.columns.filter(c => c.dependencyEvidenceIncomplete).length }
        }
        return { fileKey, parsed: true, sheetCount: sheets.length, cells, formulaCells, missingCaches, formulaCaptureErrors, captureErrorCategories, excelErrors, functions, salary }
      } catch (error) {
        return { fileKey, parsed: false, errorType: error.name, reason: String(error.message).replace(/\d{6,}/g, '[identifier]').slice(0, 240) }
      }
    }, { bytes: data.toString('base64'), fileKey: sha256.slice(0, 16) })
    results.push(result)
    if ((i + 1) % 20 === 0) console.log(JSON.stringify({ inspectedFiles: i + 1, totalFiles: files.length }))
  }
  const unique = results.filter(r => !r.duplicateContent), parsed = unique.filter(r => r.parsed)
  const sum = key => parsed.reduce((n, r) => n + (r[key] || 0), 0)
  const salary = parsed.filter(r => r.salary)
  const summary = { files: files.length, uniqueContents: unique.length, duplicateContents: results.length - unique.length,
    parsed: parsed.length, parseFailures: unique.length - parsed.length, sheets: sum('sheetCount'), cells: sum('cells'), formulaCells: sum('formulaCells'),
    missingFormulaCaches: sum('missingCaches'), formulaCaptureErrors: sum('formulaCaptureErrors'), excelErrors: sum('excelErrors'),
    salaryTableCandidates: salary.length, candidateSourceRowsAcrossFilesNotUniquePeople: salary.reduce((n, r) => n + r.salary.candidateRows, 0),
    literalBlankComponentCellsHandled: salary.reduce((n, r) => n + r.salary.literalBlanksHandled, 0),
    candidatesWithZeroImportIssues: salary.filter(r => r.salary.issueCellsAfter === 0).length,
    apiRequests, externalRequests, sourceWrites: 0, masterWrites: 0, nativePayrollRuns: 0 }
  fs.mkdirSync(path.dirname(output), { recursive: true })
  fs.writeFileSync(output, JSON.stringify({ summary, results }, null, 2))
  console.log(JSON.stringify({ summary, output }, null, 2))
} finally { await context.close(); await browser.close() }
