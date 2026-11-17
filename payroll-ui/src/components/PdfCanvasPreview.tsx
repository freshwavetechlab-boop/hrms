import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Space, Spin } from 'antd'
import type { PDFDocumentLoadingTask, PDFDocumentProxy, RenderTask } from 'pdfjs-dist'
import { pdfCanvasGeometry, pdfPreviewByteLimit, pdfPreviewPageCount, pdfPreviewRenderIntent, pdfStateForBlob, withPdfPreviewTimeout } from '../utils/pdfCanvas'
import './PdfCanvasPreview.css'

type Loaded = { blob: Blob; pdf: PDFDocumentProxy; total: number; shown: number; limited: boolean }
export default function PdfCanvasPreview({ blob, title }: { blob: Blob | null | undefined; title: string }) {
  const [loaded, setLoaded] = useState<Loaded | null>(null)
  const [failure, setFailure] = useState<{ blob: Blob | null | undefined; error: string } | null>(null)
  const [revision, setRevision] = useState(0), [page, setPage] = useState(1), [width, setWidth] = useState(0)
  const container = useRef<HTMLDivElement | null>(null)
  const current = pdfStateForBlob(loaded, blob)
  const error = pdfStateForBlob(failure, blob)?.error || ''

  useEffect(() => {
    let active = true, task: PDFDocumentLoadingTask | undefined
    void (async () => {
      await Promise.resolve()
      if (!active) return
      setLoaded(null); setFailure(null); setPage(1)
      try {
        if (!blob || typeof blob.arrayBuffer !== 'function') throw Error('The PDF is not loaded. Close and reopen this preview to load it again.')
        if (!blob.size) throw Error('The PDF is empty. Download it again and retry.')
        if (blob.size > pdfPreviewByteLimit) throw Error('This PDF is too large for the inline preview. Use Download PDF to view the complete file.')
        const { loadLocalPdf } = await import('../utils/localPdf')
        if (!active) return
        const bytes = new Uint8Array(await blob.arrayBuffer())
        if (!active) return
        task = loadLocalPdf(bytes)
        const pdf = await withPdfPreviewTimeout(task.promise, () => { void task?.destroy().catch(() => undefined) })
        if (!active) return
        setLoaded({ blob, pdf, ...pdfPreviewPageCount(pdf.numPages) })
      } catch (reason) {
        if (active) setFailure({ blob, error: reason instanceof Error ? reason.message : 'Unable to render this PDF. Download the file or retry.' })
      }
    })()
    return () => { active = false; void task?.destroy().catch(() => undefined) }
  }, [blob, revision])

  useEffect(() => {
    const element = container.current
    if (!element) return
    const measure = () => setWidth(Math.max(0, Math.floor(element.clientWidth - 24)))
    const timer = setTimeout(measure, 0)
    const observer = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(measure) : null
    observer?.observe(element); window.addEventListener('resize', measure)
    return () => { clearTimeout(timer); observer?.disconnect(); window.removeEventListener('resize', measure) }
  }, [])

  return <section className="local-pdf-preview" aria-label={title}>
    <div className="local-pdf-toolbar">
      <span aria-live="polite">{current ? 'Page ' + page + ' of ' + current.total : error ? 'Preview unavailable' : 'Loading PDF…'}</span>
      {current && <Space size={6}><Button size="small" aria-label="Previous PDF page" disabled={page <= 1} onClick={() => setPage(value => Math.max(1, value - 1))}>Previous</Button><Button size="small" aria-label="Next PDF page" disabled={page >= current.shown} onClick={() => setPage(value => Math.min(current.shown, value + 1))}>Next</Button><small>Fit to width</small></Space>}
    </div>
    {error && <div className="local-pdf-error"><Alert type="error" showIcon message={error} /><Button onClick={() => setRevision(value => value + 1)}>Retry PDF preview</Button></div>}
    {current?.limited && <Alert type="info" showIcon message={'Inline preview shows the first ' + current.shown + ' of ' + current.total + ' pages. Download the PDF to view every page.'} />}
    <div className="local-pdf-pages" ref={container} tabIndex={0} aria-label="Scrollable PDF pages">
      {current && width > 0 ? <PdfCanvasPage key={page + ':' + width} pdf={current.pdf} pageNumber={page} pageCount={current.total} width={width} onRetry={() => setRevision(value => value + 1)} /> : !error && <div className="local-pdf-loading" role="status"><Spin /><span>{current ? 'Preview will appear when this panel is visible.' : 'Rendering PDF…'}</span></div>}
    </div>
  </section>
}

function PdfCanvasPage({ pdf, pageNumber, pageCount, width, onRetry }: { pdf: PDFDocumentProxy; pageNumber: number; pageCount: number; width: number; onRetry: () => void }) {
  const canvas = useRef<HTMLCanvasElement | null>(null)
  const [state, setState] = useState<{ ready: boolean; error: string }>({ ready: false, error: '' })
  useEffect(() => {
    let active = true, task: RenderTask | undefined
    const target = canvas.current
    void (async () => {
      try {
        const page = await withPdfPreviewTimeout(pdf.getPage(pageNumber), () => undefined)
        if (!active || !target) return
        const base = page.getViewport({ scale: 1 })
        const geometry = pdfCanvasGeometry(base.width, base.height, width, window.devicePixelRatio)
        target.width = geometry.pixelWidth; target.height = geometry.pixelHeight
        target.style.width = geometry.width + 'px'; target.style.height = geometry.height + 'px'
        if (!target.getContext('2d')) throw Error('Canvas rendering is unavailable in this browser. Download the PDF instead.')
        task = page.render({ canvas: target, intent: pdfPreviewRenderIntent, viewport: page.getViewport({ scale: geometry.scale }), transform: [geometry.outputScale, 0, 0, geometry.outputScale, 0, 0] })
        await withPdfPreviewTimeout(task.promise, () => task?.cancel())
        if (active) setState({ ready: true, error: '' })
      } catch (reason) {
        if (active) setState({ ready: false, error: reason instanceof Error ? reason.message : 'Unable to render this PDF page.' })
      }
    })()
    return () => { active = false; task?.cancel() }
  }, [pdf, pageNumber, width])
  return <div className="local-pdf-page">
    {!state.ready && !state.error && <div className="local-pdf-loading" role="status"><Spin /><span>Rendering page {pageNumber}…</span></div>}
    {state.error && <div className="local-pdf-error"><Alert type="error" showIcon message={state.error} /><Button onClick={onRetry}>Retry PDF preview</Button></div>}
    <canvas ref={canvas} role="img" aria-label={'PDF page ' + pageNumber + ' of ' + pageCount} hidden={!state.ready}>Use Download PDF to view this document.</canvas>
  </div>
}
