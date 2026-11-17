import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import ts from 'typescript'
const require = createRequire(import.meta.url)
const source = fs.readFileSync(new URL('../src/utils/pdfCanvas.ts', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText
const { pdfCanvasGeometry, pdfPreviewPageCount, pdfPreviewByteLimit, pdfStateForBlob, pdfPreviewRenderIntent, withPdfPreviewTimeout } = await import('data:text/javascript;base64,' + Buffer.from(compiled).toString('base64'))
const { getDocument } = await import('pdfjs-dist/legacy/build/pdf.mjs')
const { createCanvas } = await import('@napi-rs/canvas')
const standardFontDataUrl = path.join(path.dirname(require.resolve('pdfjs-dist/package.json')), 'standard_fonts/').replaceAll('\\', '/')

function samplePdf() {
  const stream = 'q 0.97 0.89 0.90 rg 25 750 545 25 re f Q\nBT /F1 12 Tf 80 760 Td (SALARY SLIP - October 2026) Tj ET\nBT /F1 10 Tf 40 710 Td (Synthetic employee) Tj 0 -25 Td (Earnings       Deductions) Tj 0 -25 Td (Salary 1000    PF 100) Tj ET\n25 675 545 60 re S\n'
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold >>',
    '<< /Length ' + Buffer.byteLength(stream) + ' >>\nstream\n' + stream + 'endstream']
  let text = '%PDF-1.4\n', offsets = [0]
  objects.forEach((body, index) => { offsets.push(Buffer.byteLength(text)); text += (index + 1) + ' 0 obj\n' + body + '\nendobj\n' })
  const xref = Buffer.byteLength(text)
  text += 'xref\n0 6\n0000000000 65535 f \n' + offsets.slice(1).map(offset => String(offset).padStart(10, '0') + ' 00000 n \n').join('')
  text += 'trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n' + xref + '\n%%EOF\n'
  return new Uint8Array(Buffer.from(text))
}
test('fit-to-width sizing stays inside mobile/tablet/desktop and bounds HiDPI canvas memory', () => {
  for (const width of [300, 720, 1200]) {
    const result = pdfCanvasGeometry(595, 842, width, 3)
    assert.ok(result.width <= width); assert.ok(result.outputScale <= 2)
    assert.ok(result.pixelWidth * result.pixelHeight <= 8_000_000)
  }
  assert.throws(() => pdfCanvasGeometry(0, 842, 300), /invalid size/)
  assert.throws(() => pdfCanvasGeometry(595, 842, 0), /invalid size/)
})
test('empty and excessive page counts are explicit and complete download remains separate', () => {
  assert.throws(() => pdfPreviewPageCount(0), /no readable pages/)
  assert.deepEqual(pdfPreviewPageCount(1), { total: 1, shown: 1, limited: false })
  assert.deepEqual(pdfPreviewPageCount(52), { total: 52, shown: 30, limited: true })
  assert.equal(pdfPreviewByteLimit, 30 * 1024 * 1024)
})
test('initial null state, an unset PDF during HMR, and a changed Blob never expose old document/error state', () => {
  assert.equal(pdfStateForBlob(null, undefined), null)
  assert.equal(pdfStateForBlob(undefined, undefined), null)
  assert.equal(pdfStateForBlob(null, null), null)
  const first = new Blob(['first']), second = new Blob(['second'])
  const state = { blob: first, error: 'old response' }
  assert.equal(pdfStateForBlob(state, second), null)
  assert.equal(pdfStateForBlob(state, undefined), null)
  assert.equal(pdfStateForBlob(state, first), state)
})
test('locally installed PDF.js renders actual PDF bytes with serif text, pink header and nonblank canvas', async () => {
  const task = getDocument({ data: samplePdf(), standardFontDataUrl, useWorkerFetch: false, useWasm: false, disableWorkerRendering: true })
  try {
    const pdf = await task.promise, page = await pdf.getPage(1), viewport = page.getViewport({ scale: 1 })
    assert.equal(pdf.numPages, 1)
    const text = (await page.getTextContent()).items.map(item => item.str || '').join(' ')
    assert.match(text, /SALARY SLIP/); assert.match(text, /Earnings/)
    const canvas = createCanvas(Math.ceil(viewport.width), Math.ceil(viewport.height))
    await page.render({ canvas, viewport }).promise
    const pixels = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data
    let ink = 0, pink = 0
    for (let i = 0; i < pixels.length; i += 4) {
      if (pixels[i] < 100 && pixels[i + 1] < 100 && pixels[i + 2] < 100) ink++
      if (pixels[i] > pixels[i + 1] + 8 && pixels[i + 2] > pixels[i + 1] && pixels[i + 1] > 180) pink++
    }
    assert.ok(ink > 500); assert.ok(pink > 5_000)
  } finally { await task.destroy() }
})
test('page cancellation and document destroy reject stale work instead of reusing a canvas', async () => {
  const task = getDocument({ data: samplePdf(), standardFontDataUrl, useWorkerFetch: false, useWasm: false, disableWorkerRendering: true })
  const pdf = await task.promise, page = await pdf.getPage(1), viewport = page.getViewport({ scale: 1 })
  const canvas = createCanvas(Math.ceil(viewport.width), Math.ceil(viewport.height))
  const rendering = page.render({ canvas, viewport })
  rendering.cancel()
  await assert.rejects(rendering.promise, error => error.name === 'RenderingCancelledException')
  await task.destroy()
})

test('print-ready preview completes when an embedded browser never services animation frames', async () => {
  const task = getDocument({ data: samplePdf(), standardFontDataUrl, useWorkerFetch: false, useWasm: false, disableWorkerRendering: true })
  const priorWindow = globalThis.window
  let scheduledFrames = 0, cancelledFrames = 0
  try {
    const pdf = await task.promise, page = await pdf.getPage(1), viewport = page.getViewport({ scale: 1 })
    globalThis.window = { requestAnimationFrame: () => { scheduledFrames++; return scheduledFrames }, cancelAnimationFrame: () => { cancelledFrames++ } }
    const displayCanvas = createCanvas(Math.ceil(viewport.width), Math.ceil(viewport.height))
    const stalled = page.render({ canvas: displayCanvas, viewport, intent: 'display' })
    await assert.rejects(withPdfPreviewTimeout(stalled.promise, () => stalled.cancel(), 150), /took too long/)
    assert.ok(scheduledFrames > 0); assert.ok(cancelledFrames > 0)
    const framesBeforePrint = scheduledFrames
    const printCanvas = createCanvas(Math.ceil(viewport.width), Math.ceil(viewport.height))
    const rendered = page.render({ canvas: printCanvas, viewport, intent: pdfPreviewRenderIntent })
    await withPdfPreviewTimeout(rendered.promise, () => rendered.cancel(), 5_000)
    assert.equal(scheduledFrames, framesBeforePrint)
    const pixels = printCanvas.getContext('2d').getImageData(0, 0, printCanvas.width, printCanvas.height).data
    let dark = 0
    for (let i = 0; i < pixels.length; i += 4) if (pixels[i] < 100 && pixels[i + 1] < 100 && pixels[i + 2] < 100) dark++
    assert.ok(dark > 500)
  } finally {
    if (priorWindow === undefined) delete globalThis.window
    else globalThis.window = priorWindow
    await task.destroy()
  }
})

test('preview timeout cancels unfinished work once; completed work clears its deadline', async () => {
  let cancelled = 0
  assert.equal(await withPdfPreviewTimeout(Promise.resolve('ready'), () => { cancelled++ }, 10), 'ready')
  await new Promise(resolve => setTimeout(resolve, 20))
  assert.equal(cancelled, 0)
  await assert.rejects(withPdfPreviewTimeout(new Promise(() => {}), () => { cancelled++; throw Error('cleanup failed') }, 10), /Retry the preview/)
  assert.equal(cancelled, 1)
})
