import type { BulkUploadPreviewState } from '../components/BulkUploadPreviewModal'
import { buildXlsxBlob } from './xlsx'

export function previewToXlsxFile(preview: BulkUploadPreviewState, fallbackName = 'bulk-upload-preview.xlsx') {
  const sheets = preview.sheets?.length
    ? preview.sheets.map(sheet => ({ name: sheet.name, rows: [sheet.headers, ...sheet.rows] }))
    : [{ name: 'Import', rows: [preview.headers, ...preview.rows] }]
  const base = (preview.fileName || fallbackName).replace(/\.(xlsx|csv)$/i, '')
  return new File([buildXlsxBlob(sheets)], `${base}-preview.xlsx`, { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
}
