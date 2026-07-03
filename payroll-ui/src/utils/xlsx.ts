type Sheet = { name: string; rows: string[][] }

const encoder = new TextEncoder()
const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
  return c >>> 0
})

export function downloadXlsx(fileName: string, sheets: Sheet[]) {
  const files: Record<string, string> = {
    '[Content_Types].xml': contentTypes(sheets.length),
    '_rels/.rels': '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
    'xl/_rels/workbook.xml.rels': workbookRels(sheets.length),
    'xl/styles.xml': '<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="1"><fill><patternFill patternType="none"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="1"><xf/></cellXfs></styleSheet>',
    'xl/workbook.xml': workbookXml(sheets),
    ...Object.fromEntries(sheets.map((sheet, index) => [`xl/worksheets/sheet${index + 1}.xml`, sheetXml(sheet.rows)]))
  }
  const blob = new Blob([zip(files)], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
  const link = document.createElement('a')
  link.href = URL.createObjectURL(blob)
  link.download = fileName
  link.style.display = 'none'
  document.body.appendChild(link)
  link.click()
  window.setTimeout(() => { URL.revokeObjectURL(link.href); link.remove() }, 500)
}

function zip(files: Record<string, string>) {
  const locals: Uint8Array[] = [], centrals: Uint8Array[] = []
  let offset = 0
  for (const [name, text] of Object.entries(files)) {
    const data = encoder.encode(text), filename = encoder.encode(name), crc = crc32(data)
    const local = header(30 + filename.length + data.length, view => {
      u32(view, 0, 0x04034b50); u16(view, 4, 20); u16(view, 6, 0); u16(view, 8, 0); u16(view, 10, 0); u16(view, 12, 0); u32(view, 14, crc); u32(view, 18, data.length); u32(view, 22, data.length); u16(view, 26, filename.length); u16(view, 28, 0)
    })
    local.set(filename, 30); local.set(data, 30 + filename.length); locals.push(local)
    const central = header(46 + filename.length, view => {
      u32(view, 0, 0x02014b50); u16(view, 4, 20); u16(view, 6, 20); u16(view, 8, 0); u16(view, 10, 0); u16(view, 12, 0); u16(view, 14, 0); u32(view, 16, crc); u32(view, 20, data.length); u32(view, 24, data.length); u16(view, 28, filename.length); u16(view, 30, 0); u16(view, 32, 0); u16(view, 34, 0); u16(view, 36, 0); u32(view, 38, 0); u32(view, 42, offset)
    })
    central.set(filename, 46); centrals.push(central); offset += local.length
  }
  const centralSize = centrals.reduce((sum, item) => sum + item.length, 0)
  const end = header(22, view => { u32(view, 0, 0x06054b50); u16(view, 4, 0); u16(view, 6, 0); u16(view, 8, centrals.length); u16(view, 10, centrals.length); u32(view, 12, centralSize); u32(view, 16, offset); u16(view, 20, 0) })
  const zip = new Uint8Array(offset + centralSize + end.length)
  let zipOffset = 0
  for (const part of [...locals, ...centrals, end]) { zip.set(part, zipOffset); zipOffset += part.length }
  return new Blob([zip.buffer])
}

function header(size: number, write: (view: DataView) => void) { const bytes = new Uint8Array(size); write(new DataView(bytes.buffer)); return bytes }
function u16(view: DataView, offset: number, value: number) { view.setUint16(offset, value, true) }
function u32(view: DataView, offset: number, value: number) { view.setUint32(offset, value >>> 0, true) }
function crc32(data: Uint8Array) { let crc = 0xffffffff; for (const b of data) crc = crcTable[(crc ^ b) & 255] ^ (crc >>> 8); return (crc ^ 0xffffffff) >>> 0 }
function esc(value: string) { return String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' }[ch]!)) }
function col(n: number) { let s = ''; while (n > 0) { n--; s = String.fromCharCode(65 + (n % 26)) + s; n = Math.floor(n / 26) } return s }
function contentTypes(count: number) { return `<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>${Array.from({ length: count }, (_, i) => `<Override PartName="/xl/worksheets/sheet${i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>`).join('')}</Types>` }
function workbookRels(count: number) { return `<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">${Array.from({ length: count }, (_, i) => `<Relationship Id="rId${i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet${i + 1}.xml"/>`).join('')}<Relationship Id="rId${count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>` }
function workbookXml(sheets: Sheet[]) { return `<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>${sheets.map((sheet, i) => `<sheet name="${esc(sheet.name)}" sheetId="${i + 1}" r:id="rId${i + 1}"/>`).join('')}</sheets></workbook>` }
function sheetXml(rows: string[][]) { return `<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>${rows.map((row, r) => `<row r="${r + 1}">${row.map((cell, c) => `<c r="${col(c + 1)}${r + 1}" t="inlineStr"><is><t>${esc(cell)}</t></is></c>`).join('')}</row>`).join('')}</sheetData></worksheet>` }
