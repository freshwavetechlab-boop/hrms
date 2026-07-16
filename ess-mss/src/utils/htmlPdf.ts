import html2canvas from 'html2canvas'
import { jsPDF } from 'jspdf'

const waitForImages = async (document: Document) => {
  const images = Array.from(document.images)
  await Promise.all(images.map(image => image.complete
    ? Promise.resolve()
    : new Promise<void>(resolve => {
        image.addEventListener('load', () => resolve(), { once: true })
        image.addEventListener('error', () => resolve(), { once: true })
      })))
}

const pdfName = (fileName: string) => fileName.replace(/\.html?$/i, '').replace(/\.pdf$/i, '') + '.pdf'

export async function downloadHtmlPdf(html: string, fileName: string) {
  const iframe = window.document.createElement('iframe')
  iframe.setAttribute('aria-hidden', 'true')
  Object.assign(iframe.style, {
    position: 'fixed',
    left: '-100000px',
    top: '0',
    width: '820px',
    height: '1160px',
    border: '0',
    opacity: '0',
    pointerEvents: 'none'
  })
  const loaded = new Promise<void>(resolve => iframe.addEventListener('load', () => resolve(), { once: true }))
  iframe.srcdoc = html
  window.document.body.appendChild(iframe)

  try {
    await loaded
    const frameDocument = iframe.contentDocument
    if (!frameDocument) throw new Error('Payslip document could not be prepared.')
    await waitForImages(frameDocument)
    await new Promise<void>(resolve => window.requestAnimationFrame(() => window.requestAnimationFrame(() => resolve())))

    const target = (frameDocument.querySelector('.slip') ?? frameDocument.body) as HTMLElement
    const canvas = await html2canvas(target, {
      backgroundColor: '#ffffff',
      logging: false,
      scale: 2,
      useCORS: true,
      windowHeight: Math.max(target.scrollHeight, 1120),
      windowWidth: Math.max(target.scrollWidth, 794)
    })
    const pdf = new jsPDF({ format: 'a4', orientation: 'portrait', unit: 'mm' })
    const margin = 8
    const pageWidth = pdf.internal.pageSize.getWidth()
    const pageHeight = pdf.internal.pageSize.getHeight()
    const contentWidth = pageWidth - margin * 2
    const contentHeight = pageHeight - margin * 2
    const imageHeight = canvas.height * contentWidth / canvas.width
    const image = canvas.toDataURL('image/png')
    let renderedHeight = 0

    pdf.addImage(image, 'PNG', margin, margin, contentWidth, imageHeight)
    renderedHeight += contentHeight
    while (renderedHeight < imageHeight) {
      pdf.addPage()
      pdf.addImage(image, 'PNG', margin, margin - renderedHeight, contentWidth, imageHeight)
      renderedHeight += contentHeight
    }
    pdf.save(pdfName(fileName))
  } finally {
    iframe.remove()
  }
}
