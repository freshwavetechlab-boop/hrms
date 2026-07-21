import type { ImportPreviewSheet } from './importPreview'
import { buildXlsxBlob } from './xlsx'

export type BulkImportFieldType = 'text' | 'email' | 'number' | 'date' | 'boolean' | 'lookup'

export type BulkImportFieldDefinition = {
  code: string
  header: string
  label: string
  group: string
  type: BulkImportFieldType
  required?: boolean
  description?: string
  aliases?: string[]
  defaultValue?: string
}

export type BulkImportDefinition = {
  moduleCode: string
  moduleLabel: string
  targetSheetName: string
  fields: BulkImportFieldDefinition[]
}

export type BulkImportMapping = Record<string, number>

export type BulkImportOperation = 'insert' | 'update' | 'upsert'
export type BulkImportNameSplitStrategy = 'first-token' | 'last-token'

export type BulkImportTransformOptions = {
  operation?: BulkImportOperation
  employeeCodeGeneration?: {
    enabled: boolean
    prefix: string
    digits: number
    existingCodes?: string[]
  }
  nameSplit?: {
    sourceColumnIndex: number
    strategy: BulkImportNameSplitStrategy
    firstNameFieldCode?: string
    lastNameFieldCode?: string
  }
  preTransformedRows?: number
  sourceColumnTransforms?: Record<number, string>
}

export type MaterializedBulkImportNameSplit = {
  sheet: ImportPreviewSheet
  sourceColumnIndex: number
  sourceHeader: string
  firstNameColumnIndex: number
  lastNameColumnIndex: number
  strategy: BulkImportNameSplitStrategy
  splitRows: number
}

export type PreparedBulkImport = {
  file: File
  sourceFileName: string
  sourceSheetName: string
  sourceRows: number
  mappedFields: number
  skippedColumns: number
  mappings: BulkImportMapping
  outputHeaders: string[]
  columns: PreparedBulkImportColumn[]
  operation: BulkImportOperation
  generatedEmployeeCodes: number
  splitNames: number
}

export type PreparedBulkImportColumn = {
  targetCode: string
  targetHeader: string
  sourceColumnIndex?: number
  sourceHeader: string
  color: string
  kind: 'mapped' | 'default' | 'generated' | 'transformed'
}

export function normalizeBulkImportHeader(value: string) {
  return value.trim().toLowerCase().replace(/&/g, 'and').replace(/[^a-z0-9]+/g, '')
}

export function autoMapBulkImportColumns(sheet: ImportPreviewSheet, definition: BulkImportDefinition): BulkImportMapping {
  const source = sheet.headers.map((header, index) => ({ index, normalized: normalizeBulkImportHeader(header) }))
  const used = new Set<number>()
  const result: BulkImportMapping = {}
  for (const field of definition.fields) {
    const candidates = [field.header, field.label, field.code, ...(field.aliases ?? [])].map(normalizeBulkImportHeader).filter(Boolean)
    const match = source.find(column => !used.has(column.index) && candidates.includes(column.normalized))
    if (!match) continue
    result[field.code] = match.index
    used.add(match.index)
  }
  return result
}

export function mapBulkImportColumn(current: BulkImportMapping, targetCode: string, sourceColumnIndex: number) {
  const next: BulkImportMapping = {}
  for (const [code, index] of Object.entries(current)) {
    if (code !== targetCode && index !== sourceColumnIndex) next[code] = index
  }
  next[targetCode] = sourceColumnIndex
  return next
}

export function prepareMappedBulkImport(file: File, sheet: ImportPreviewSheet, definition: BulkImportDefinition, mappings: BulkImportMapping, options: BulkImportTransformOptions = {}): PreparedBulkImport {
  const operation = options.operation ?? 'upsert'
  const codeGeneration = options.employeeCodeGeneration?.enabled && operation !== 'update' ? options.employeeCodeGeneration : undefined
  const split = options.nameSplit
  const firstNameCode = split?.firstNameFieldCode ?? 'FirstName'
  const lastNameCode = split?.lastNameFieldCode ?? 'LastName'
  const transformedCodes = new Set<string>()
  if (split && mappings[firstNameCode] === undefined) transformedCodes.add(firstNameCode)
  if (split && mappings[lastNameCode] === undefined) transformedCodes.add(lastNameCode)
  if (codeGeneration) transformedCodes.add('EmployeeCode')
  const outputFields = definition.fields.filter(field => mappings[field.code] !== undefined || transformedCodes.has(field.code) || (operation !== 'update' && field.defaultValue !== undefined) || field.required)
  const outputHeaders = outputFields.map(field => field.header)
  const generatedCodes = buildEmployeeCodes(sheet.rows, mappings.EmployeeCode, codeGeneration)
  let splitNames = options.preTransformedRows ?? 0
  const outputRows = sheet.rows.map((row, rowIndex) => {
    const nameParts = split ? splitBulkImportName(row[split.sourceColumnIndex] ?? '', split.strategy) : null
    if (nameParts?.firstName || nameParts?.lastName) splitNames++
    return outputFields.map(field => {
      const sourceIndex = mappings[field.code]
      if (sourceIndex !== undefined) {
        const direct = row[sourceIndex] ?? ''
        if (field.code !== 'EmployeeCode' || direct.trim() || !codeGeneration) return direct
      }
      if (field.code === 'EmployeeCode' && codeGeneration) return generatedCodes[rowIndex] ?? ''
      if (field.code === firstNameCode && nameParts) return nameParts.firstName
      if (field.code === lastNameCode && nameParts) return nameParts.lastName
      return operation === 'update' ? '' : field.defaultValue ?? ''
    })
  })
  const mappedSourceColumns = new Set(Object.values(mappings))
  if (split) mappedSourceColumns.add(split.sourceColumnIndex)
  const sourceColumns = sheet.headers.length
  const outputName = `${withoutExtension(file.name)}-${definition.moduleCode.toLowerCase()}-mapped.xlsx`
  const columns = outputFields.map(field => {
    const sourceColumnIndex = mappings[field.code]
    const mapped = sourceColumnIndex !== undefined
    const generated = field.code === 'EmployeeCode' && Boolean(codeGeneration)
    const sourceTransform = sourceColumnIndex === undefined ? undefined : options.sourceColumnTransforms?.[sourceColumnIndex]
    const transformed = (transformedCodes.has(field.code) || Boolean(sourceTransform)) && !generated
    return {
      targetCode: field.code,
      targetHeader: field.header,
      sourceColumnIndex,
      sourceHeader: sourceTransform
        ? sourceTransform
        : mapped
          ? sheet.headers[sourceColumnIndex] || `Column ${sourceColumnIndex + 1}`
        : generated
          ? `Generated: ${codeGeneration?.prefix || ''}${'0'.repeat(Math.max(1, codeGeneration?.digits ?? 1))}`
          : transformed
            ? `Split from: ${sheet.headers[split!.sourceColumnIndex] || `Column ${split!.sourceColumnIndex + 1}`}`
            : `Default: ${field.defaultValue ?? ''}`,
      color: bulkImportMappingColor(definition.fields.indexOf(field)),
      kind: generated ? 'generated' as const : transformed ? 'transformed' as const : mapped ? 'mapped' as const : 'default' as const
    }
  })
  return {
    file: new File([buildXlsxBlob([{ name: definition.targetSheetName, rows: [outputHeaders, ...outputRows] }])], outputName, { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }),
    sourceFileName: file.name,
    sourceSheetName: sheet.name,
    sourceRows: outputRows.length,
    mappedFields: Object.keys(mappings).length,
    skippedColumns: Math.max(0, sourceColumns - mappedSourceColumns.size),
    mappings: { ...mappings },
    outputHeaders,
    columns,
    operation,
    generatedEmployeeCodes: generatedCodes.filter(Boolean).length,
    splitNames
  }
}

export function inferEmployeeCodeDigits(prefix: string, existingCodes: string[], fallback = 5) {
  const escaped = escapeRegExp(prefix.trim())
  if (!escaped) return fallback
  const widths = existingCodes.map(code => code.trim().match(new RegExp(`^${escaped}(\\d+)$`, 'i'))?.[1].length ?? 0).filter(Boolean)
  return widths.length ? Math.max(...widths) : fallback
}

export function nextEmployeeCodePreview(prefix: string, digits: number, existingCodes: string[]) {
  const normalizedPrefix = prefix.trim().toUpperCase()
  const width = clampDigits(digits)
  const { next } = employeeCodeSequence(normalizedPrefix, width, existingCodes)
  return `${normalizedPrefix}${String(next).padStart(width, '0')}`
}

export function splitBulkImportName(value: string, strategy: BulkImportNameSplitStrategy) {
  const parts = value.trim().split(/\s+/).filter(Boolean)
  if (!parts.length) return { firstName: '', lastName: '' }
  if (parts.length === 1) return { firstName: parts[0], lastName: '' }
  return strategy === 'first-token'
    ? { firstName: parts[0], lastName: parts.slice(1).join(' ') }
    : { firstName: parts.slice(0, -1).join(' '), lastName: parts.at(-1) ?? '' }
}

export function materializeBulkImportNameSplit(sheet: ImportPreviewSheet, sourceColumnIndex: number, strategy: BulkImportNameSplitStrategy): MaterializedBulkImportNameSplit {
  if (!Number.isInteger(sourceColumnIndex) || sourceColumnIndex < 0 || sourceColumnIndex >= sheet.headers.length) {
    throw new Error('Select a valid uploaded column to split.')
  }
  const sourceHeader = sheet.headers[sourceColumnIndex]?.trim() || `Column ${sourceColumnIndex + 1}`
  const firstHeader = uniqueSplitHeader(sheet.headers, sourceColumnIndex, 'First Name', sourceHeader)
  const lastHeader = uniqueSplitHeader(sheet.headers, sourceColumnIndex, 'Last Name', sourceHeader)
  let splitRows = 0
  const rows = sheet.rows.map(row => {
    const parts = splitBulkImportName(row[sourceColumnIndex] ?? '', strategy)
    if (parts.firstName || parts.lastName) splitRows++
    return [...row.slice(0, sourceColumnIndex), parts.firstName, parts.lastName, ...row.slice(sourceColumnIndex + 1)]
  })
  return {
    sheet: {
      ...sheet,
      headers: [...sheet.headers.slice(0, sourceColumnIndex), firstHeader, lastHeader, ...sheet.headers.slice(sourceColumnIndex + 1)],
      rows
    },
    sourceColumnIndex,
    sourceHeader,
    firstNameColumnIndex: sourceColumnIndex,
    lastNameColumnIndex: sourceColumnIndex + 1,
    strategy,
    splitRows
  }
}

function buildEmployeeCodes(rows: string[][], sourceIndex: number | undefined, configuration?: NonNullable<BulkImportTransformOptions['employeeCodeGeneration']>) {
  if (!configuration) return rows.map(() => '')
  const prefix = configuration.prefix.trim().toUpperCase()
  const digits = clampDigits(configuration.digits)
  const providedCodes = sourceIndex === undefined ? [] : rows.map(row => row[sourceIndex] ?? '').filter(value => value.trim())
  const used = new Set([...(configuration.existingCodes ?? []), ...providedCodes].map(value => value.trim().toUpperCase()).filter(Boolean))
  let { next } = employeeCodeSequence(prefix, digits, [...used])
  return rows.map(row => {
    const provided = sourceIndex === undefined ? '' : (row[sourceIndex] ?? '').trim()
    if (provided) return ''
    let candidate = ''
    do candidate = `${prefix}${String(next++).padStart(digits, '0')}`
    while (used.has(candidate.toUpperCase()))
    used.add(candidate.toUpperCase())
    return candidate
  })
}

function employeeCodeSequence(prefix: string, digits: number, existingCodes: string[]) {
  const expression = prefix ? new RegExp(`^${escapeRegExp(prefix)}(\\d+)$`, 'i') : /^(\d+)$/
  const maximum = existingCodes.reduce((value, code) => {
    const match = code.trim().match(expression)
    return match ? Math.max(value, Number(match[1]) || 0) : value
  }, 0)
  return { next: maximum + 1, digits }
}

function clampDigits(value: number) {
  return Math.max(1, Math.min(12, Math.trunc(Number(value) || 1)))
}

function uniqueSplitHeader(headers: string[], replacedIndex: number, desired: string, sourceHeader: string) {
  const desiredKey = normalizeBulkImportHeader(desired)
  const duplicate = headers.some((header, index) => index !== replacedIndex && normalizeBulkImportHeader(header) === desiredKey)
  return duplicate ? `${desired} (split from ${sourceHeader})` : desired
}

function escapeRegExp(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

export function bulkImportMappingColor(fieldIndex: number) {
  const palette = ['#5b4ee8', '#0f9f8f', '#1976d2', '#d97706', '#db2777', '#7c3aed', '#0891b2', '#65a30d', '#dc2626', '#4f46e5']
  return palette[Math.abs(fieldIndex) % palette.length]
}

function withoutExtension(value: string) {
  return value.replace(/\.[^.]+$/i, '') || 'bulk-import'
}
