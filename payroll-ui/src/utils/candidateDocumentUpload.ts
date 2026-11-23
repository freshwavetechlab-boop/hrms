import type { CandidateDocumentMetadata, CandidateDocumentRequirement } from '../services/publicCandidateDocumentService'

export type CandidateDocumentSelection<T extends Pick<File, 'name' | 'size'> = File> = {
  files: T[]
  documentNumber: string
  issueDate: string
  expiryDate: string
}
export const emptyDocumentSelection = (): CandidateDocumentSelection => ({ files: [], documentNumber: '', issueDate: '', expiryDate: '' })
export const maximumCandidateDocumentNumberLength = 180
export const maximumCandidateDocumentFiles = 64
export const maximumCandidateDocumentBytes = 100 * 1024 * 1024
const extension = (value: string) => (value.startsWith('.') ? value : `.${value}`).toLowerCase()
export const documentAccept = (document: CandidateDocumentRequirement) => document.allowedExtensions.map(extension).join(',')
export const documentSizeLabel = (bytes: number) => bytes >= 1024 * 1024 ? `${Math.round(bytes / 1024 / 1024 * 10) / 10} MB` : `${Math.ceil(bytes / 1024)} KB`

export function validateDocumentFiles(document: CandidateDocumentRequirement, files: Pick<File, 'name' | 'size'>[]) {
  const maximum = Math.max(1, document.maximumFileCount)
  if (files.length > maximum) return `${document.label}: choose up to ${maximum} file${maximum === 1 ? '' : 's'}.`
  const allowed = document.allowedExtensions.map(extension)
  for (const file of files) {
    if (!file.size) return `${document.label}: ${file.name} is empty. Choose another file.`
    if (document.maximumFileSizeBytes > 0 && file.size > document.maximumFileSizeBytes)
      return `${document.label}: each file must be no larger than ${documentSizeLabel(document.maximumFileSizeBytes)}.`
    const suffix = file.name.lastIndexOf('.') >= 0 ? file.name.slice(file.name.lastIndexOf('.')).toLowerCase() : ''
    if (allowed.length && !allowed.includes(suffix)) return `${document.label}: use ${allowed.join(', ')} files.`
  }
  return ''
}

export function validateCandidateDocuments(documents: CandidateDocumentRequirement[], email: string, selections: Record<string, CandidateDocumentSelection<Pick<File, 'name' | 'size'>>>) {
  if (!/^[^\s@<>;,]+@[^\s@<>;,]+\.[^\s@<>;,]+$/.test(email.trim())) return 'Enter the email address that received your offer letter.'
  const files = documents.flatMap(document => selections[document.id]?.files || [])
  if (files.length > maximumCandidateDocumentFiles) return `Choose up to ${maximumCandidateDocumentFiles} files in total.`
  if (files.reduce((sum, file) => sum + file.size, 0) > maximumCandidateDocumentBytes) return 'The combined size of your documents must be no larger than 100 MB.'
  for (const document of documents) {
    const selected = selections[document.id] || emptyDocumentSelection()
    const minimum = Math.max(document.required ? 1 : 0, document.minimumFileCount)
    if (selected.files.length < minimum) return `Upload ${minimum > 1 ? `at least ${minimum} files for ` : ''}${document.label}.`
    const fileError = validateDocumentFiles(document, selected.files)
    if (fileError) return fileError
    if (!selected.files.length) continue
    if (selected.documentNumber.trim().length > maximumCandidateDocumentNumberLength) return `${document.label}: document number must be ${maximumCandidateDocumentNumberLength} characters or fewer.`
    if (document.requiresDocumentNumber && !selected.documentNumber.trim()) return `Enter the document number for ${document.label}.`
    if (document.requiresIssueDate && !selected.issueDate) return `Enter the issue date for ${document.label}.`
    if (document.requiresExpiryDate && !selected.expiryDate) return `Enter the expiry date for ${document.label}.`
    if (selected.issueDate && selected.expiryDate && selected.expiryDate < selected.issueDate) return `${document.label}: expiry date cannot be before the issue date.`
  }
  if (!files.length) return 'Select at least one document to submit.'
  return ''
}

export function candidateDocumentMetadata(documents: CandidateDocumentRequirement[], selections: Record<string, CandidateDocumentSelection>): CandidateDocumentMetadata[] {
  return documents.filter(document => selections[document.id]?.files.length).map(document => {
    const selected = selections[document.id]
    return { id: document.id, documentNumber: selected.documentNumber.trim(), issueDate: selected.issueDate || undefined, expiryDate: selected.expiryDate || undefined }
  })
}
