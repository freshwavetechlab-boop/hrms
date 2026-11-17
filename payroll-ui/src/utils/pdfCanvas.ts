export const pdfPreviewPageLimit = 30
export const pdfPreviewByteLimit = 30 * 1024 * 1024
export const pdfPreviewTimeoutMs = 20_000
// Print intent renders the final payslip without depending on animation frames,
// which embedded/background browsers can suspend indefinitely.
export const pdfPreviewRenderIntent = 'print' as const
export function withPdfPreviewTimeout<T>(promise: Promise<T>, cancel: () => void, timeoutMs = pdfPreviewTimeoutMs): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => {
      reject(Error('PDF preview took too long. Retry the preview or use Download PDF.'))
      try { cancel() } catch { /* A failed cleanup must not hide the timeout. */ }
    }, timeoutMs)
  })
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer))
}
export function pdfStateForBlob<T extends { blob: Blob | null | undefined }>(state: T | null | undefined, blob: Blob | null | undefined): T | null {
  return state && state.blob === blob ? state : null
}

export function pdfCanvasGeometry(pageWidth: number, pageHeight: number, availableWidth: number, pixelRatio = 1) {
  if (![pageWidth, pageHeight, availableWidth].every(value => Number.isFinite(value) && value > 0)) throw Error('This PDF page has an invalid size.')
  const scale = Math.min(availableWidth / pageWidth, 1.75)
  const width = Math.max(1, Math.floor(pageWidth * scale)), height = Math.max(1, Math.floor(pageHeight * scale))
  const outputScale = Math.min(Math.max(1, Number.isFinite(pixelRatio) ? pixelRatio : 1), 2, Math.sqrt(8_000_000 / (width * height)))
  return { scale, width, height, outputScale, pixelWidth: Math.max(1, Math.floor(width * outputScale)), pixelHeight: Math.max(1, Math.floor(height * outputScale)) }
}

export function pdfPreviewPageCount(count: number) {
  if (!Number.isInteger(count) || count < 1) throw Error('This PDF has no readable pages.')
  return { total: count, shown: Math.min(count, pdfPreviewPageLimit), limited: count > pdfPreviewPageLimit }
}
