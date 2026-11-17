import { getDocument, GlobalWorkerOptions } from 'pdfjs-dist/legacy/build/pdf.mjs'
import workerUrl from 'pdfjs-dist/legacy/build/pdf.worker.min.mjs?url'

// Worker and standard fonts are emitted with this application. No CDN or
// uploaded PDF URL is used by the renderer.
GlobalWorkerOptions.workerSrc = workerUrl
const fontAssets = import.meta.glob('../../node_modules/pdfjs-dist/standard_fonts/*', { eager: true, query: '?url', import: 'default' }) as Record<string, string>
const fonts = Object.fromEntries(Object.entries(fontAssets).map(([file, url]) => [file.split('/').pop()!, url]))
class BundledPdfDataFactory {
  async fetch({ kind, filename }: { kind: string; filename: string }) {
    const url = kind === 'standardFontDataUrl' ? fonts[filename] : undefined
    if (!url) throw Error('This PDF requires an unsupported local rendering resource.')
    const response = await fetch(url)
    if (!response.ok) throw Error('A bundled PDF font could not be loaded.')
    return new Uint8Array(await response.arrayBuffer())
  }
}
export const loadLocalPdf = (data: Uint8Array) => getDocument({
  data, BinaryDataFactory: BundledPdfDataFactory, useWorkerFetch: false, useWasm: false,
  disableWorkerRendering: true, enableXfa: false, useSystemFonts: true,
})
