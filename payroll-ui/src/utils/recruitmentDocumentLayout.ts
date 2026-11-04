import type { DocumentInput } from '../services/recruitmentDocumentService'
export const documentLayoutMarker = '<!-- frevo-document-layout:v1 -->'

export function readDocumentLayout(text: string) {
  const source = text.trim().startsWith(documentLayoutMarker) ? text.trim().slice(documentLayoutMarker.length) : text
  const data = JSON.parse(source)
  if (data?.version !== 1 || !Array.isArray(data.inputs) || !Array.isArray(data.sections) || !data.sections.length) throw new Error('Upload a version 1 document layout with inputs and sections.')
  const body = documentLayoutMarker + '\n' + JSON.stringify(data)
  if (new TextEncoder().encode(body).length > 64000) throw new Error('The layout exceeds 64 KB. Use paragraph/table blocks to keep it compact.')
  return { body, fields: data.inputs as DocumentInput[], pages: data.sections.reduce((sum: number, s: { pages?: number }) => sum + (s.pages || 1), 0) as number }
}
export const documentHeaderKey = (text: string) => text.normalize('NFKC').replace(/[^\p{L}\p{N}]/gu, '').toLowerCase()
export function suggestRosterMapping(fields: DocumentInput[], headers: string[]) {
  return Object.fromEntries(fields.map(field => {
    const aliases = new Set(field.sourceHeaders.map(documentHeaderKey))
    const matches = headers.flatMap((h, i) => h.trim() && aliases.has(documentHeaderKey(h)) ? [i] : [])
    return [field.key, matches.length === 1 ? matches[0] : -1]
  }))
}
