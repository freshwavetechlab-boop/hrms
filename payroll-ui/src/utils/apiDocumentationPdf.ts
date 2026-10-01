import { jsPDF } from 'jspdf'
import autoTable from 'jspdf-autotable'
import type { ApiCatalogRow } from '../data/apiCatalog'
import type { ApiDocument } from '../services/apiDocumentationService'

type Node = {
  $ref?: string; type?: string; format?: string; description?: string; required?: string[] | boolean
  properties?: Record<string, Node>; items?: Node; allOf?: Node[]; enum?: unknown[]
  minItems?: number; maxItems?: number; minLength?: number; maxLength?: number
  minimum?: number; maximum?: number; example?: unknown; default?: unknown
  name?: string; in?: string; schema?: Node; content?: Record<string, Node>
  examples?: Record<string, { value?: unknown }>
}

// Export the selected OpenAPI operation, never the live tester's credentials or response.
export function downloadApiDocumentationPdf(row: ApiCatalogRow, document: ApiDocument, url: string) {
  const operation = document.paths[row.path]?.[row.method.toLowerCase()]
  if (!operation) throw new Error('Documentation for this API is unavailable.')
  const resolve = (node: Node): Node => node.$ref?.startsWith('#/')
    ? node.$ref.slice(2).split('/').reduce<unknown>((value, key) => (value as Record<string, unknown> | undefined)?.[key.replace(/~1/g, '/').replace(/~0/g, '~')], document) as Node || node : node
  const fields = (source: Node, prefix = '', depth = 0, seen: string[] = []): string[][] => {
    if (depth > 8 || (source.$ref && seen.includes(source.$ref))) return []
    const schema = resolve(source), next = source.$ref ? [...seen, source.$ref] : seen
    if (schema.allOf) return schema.allOf.flatMap(part => fields(part, prefix, depth + 1, next))
    if (schema.type === 'array' && schema.items) return fields(schema.items, prefix + '[]', depth + 1, next)
    return Object.entries(schema.properties || {}).flatMap(([name, property]) => {
      const field = resolve(property), path = prefix ? prefix + '.' + name : name
      const rules = [field.description, field.enum && 'Allowed: ' + field.enum.join(', '), ...(['minItems', 'maxItems', 'minLength', 'maxLength', 'minimum', 'maximum'] as const).filter(key => field[key] != null).map(key => key + ': ' + field[key])].filter(Boolean).join('\n')
      return [[path, field.type || (field.properties ? 'object' : ''), Array.isArray(schema.required) && schema.required.includes(name) ? 'Yes' : 'No', rules], ...fields(property, path, depth + 1, next)]
    })
  }
  const sample = (source: Node, depth = 0): unknown => {
    if (depth > 8) return null
    const schema = resolve(source)
    if (schema.example !== undefined) return schema.example
    if (schema.default !== undefined) return schema.default
    if (schema.enum?.length) return schema.enum[0]
    if (schema.allOf) return Object.assign({}, ...schema.allOf.map(part => sample(part, depth + 1)))
    if (schema.properties) return Object.fromEntries(Object.entries(schema.properties).map(([key, value]) => [key, sample(value, depth + 1)]))
    if (schema.items) return [sample(schema.items, depth + 1)]
    if (schema.type === 'integer' || schema.type === 'number') return 1
    if (schema.type === 'boolean') return true
    return schema.format === 'binary' ? '<FILE>' : schema.format === 'date-time' ? '2026-09-01T09:00:00+05:30' : schema.format === 'date' ? '2026-09-01' : 'string'
  }
  const pdf = new jsPDF({ format: 'a4', unit: 'mm' })
  let y = 18
  const table = (title: string, head: string[], body: string[][], code = false) => {
    const minimumHeight = code ? 22 + (body[0]?.[0].split('\n').length || 1) * 3.3 : 35
    if (y + minimumHeight > pdf.internal.pageSize.getHeight() - 18) { pdf.addPage(); y = 18 }
    pdf.setFont('helvetica', 'bold'); pdf.setFontSize(12); pdf.setTextColor(25, 43, 65)
    pdf.text(title, 14, y); y += 5
    autoTable(pdf, { startY: y, head: [head], body: body.map(cells => cells.map(cell => cell.replace(/[\u2013\u2014]/g, '-'))), rowPageBreak: 'avoid', margin: { top: 14, bottom: 18, left: 14, right: 14 },
      styles: { font: code ? 'courier' : 'helvetica', fontSize: code ? 8 : 9, cellPadding: 3, overflow: 'linebreak' },
      headStyles: { fillColor: [8, 121, 176], font: 'helvetica', fontStyle: 'bold' }, alternateRowStyles: { fillColor: [245, 248, 252] } })
    y = (pdf as jsPDF & { lastAutoTable: { finalY: number } }).lastAutoTable.finalY + 10
  }
  const machine = row.path === '/api/integrations/attendance/punches'
  table('Frevo HRMS - API documentation', ['Item', 'Details'], [
    ['Method', row.method], ['URL', url], ['Purpose', row.purpose],
    ['Authentication', machine ? 'Authorization: Bearer <YOUR_ATTENDANCE_API_KEY>' : 'Your HRMS login session or authorized user Bearer token.'],
    ...(machine ? [['Generate the key', 'Workflows > API Catalog > API keys > Generate API key. Select the registered punch machine.'], ['Machine setup', 'Settings > Leave & Attendance > Attendance > Punch machines. Map the device to its client and work location.'], ['Punch-in / punch-out', 'Use this endpoint for both IN and OUT. Each log needs a unique punchId. Reuse that same ID when retrying.']] : []),
    ...(operation.description ? [['Details', operation.description]] : []),
  ])
  const parameters = [...(document.paths[row.path].parameters as unknown as Node[] || []), ...(operation.parameters as Node[] || [])].map(resolve)
  if (parameters.length) table('Parameters', ['Name', 'Location / type', 'Required', 'Details'], parameters.map(p => [p.name || '', [p.in, resolve(p.schema || {}).type].filter(Boolean).join(' / '), p.required ? 'Yes' : 'No', p.description || resolve(p.schema || {}).description || '']))
  const body = resolve(operation.requestBody as Node || {})
  const media = body.content?.['application/json'] || Object.values(body.content || {})[0]
  if (media) {
    const contentType = body.content?.['application/json'] ? 'application/json' : Object.keys(body.content || {})[0]
    const schema = media.schema || {}
    const requestFields = fields(schema)
    if (requestFields.length) table('Request body - ' + contentType, ['Field', 'Type', 'Required', 'Details'], requestFields)
    const example = media.example ?? Object.values(media.examples || {})[0]?.value ?? sample(schema)
    table('Example request body', ['Replace sample values with your actual data'], [[JSON.stringify(example, null, 2)]], true)
  }
  const responses = Object.entries(operation.responses as Record<string, Node> || {}).map(([status, response]) => [status, resolve(response)] as const)
  if (responses.length) table('HTTP responses', ['Status', 'Meaning'], responses.map(([status, response]) => [status, response.description || '']))
  for (const [status, response] of responses.filter(([status]) => /^2/.test(status))) {
    const schema = (response.content?.['application/json'] || Object.values(response.content || {})[0])?.schema
    const responseFields = schema ? fields(schema) : []
    if (responseFields.length) table(status + ' response fields', ['Field', 'Type', 'Required', 'Details'], responseFields)
  }
  const pages = pdf.getNumberOfPages()
  for (let page = 1; page <= pages; page++) {
    pdf.setPage(page); pdf.setFont('helvetica', 'normal'); pdf.setFontSize(8); pdf.setTextColor(100)
    pdf.text('Frevo HRMS | ' + row.method + ' ' + row.path, 14, pdf.internal.pageSize.getHeight() - 8)
    pdf.text(page + ' / ' + pages, pdf.internal.pageSize.getWidth() - 14, pdf.internal.pageSize.getHeight() - 8, { align: 'right' })
  }
  pdf.save('api-' + row.method.toLowerCase() + '-' + row.path.replace(/^\/api\//, '').replace(/[^a-z0-9-]+/gi, '-') + '.pdf')
}
