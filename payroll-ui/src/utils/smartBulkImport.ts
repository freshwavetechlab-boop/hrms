import type { ImportPreviewSheet } from './importPreview'
import { buildXlsxBlob } from './xlsx'
import { normalizeEmployeeSalaryAmount, resolveEmployeeCtc } from './employeeCtc'

export type BulkImportFieldType = 'text' | 'email' | 'number' | 'date' | 'boolean' | 'lookup'

export type BulkImportFieldDefinition = {
  code: string
  header: string
  label: string
  group: string
  type: BulkImportFieldType
  required?: boolean
  requiredForNew?: boolean
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
  employeeSalaryContext?: { templateId: string; effectiveFrom: string }
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

export function employeeCodeSourceColumns(headers: string[], definition: BulkImportDefinition) {
  if (definition.moduleCode !== 'EMPLOYEE') return []
  const field = definition.fields.find(item => item.code === 'EmployeeCode')
  if (!field) return []
  const names = new Set([field.code, field.header, field.label, ...(field.aliases ?? [])].map(normalizeBulkImportHeader))
  return headers.flatMap((name, index) => names.has(normalizeBulkImportHeader(name)) ? [index] : [])
}

function bestEmployeeCodeColumn(rows: string[][], indices: number[], existingCodes: readonly string[]) {
  if (indices.length < 2) return indices[0]
  const master = new Set(existingCodes.map(code => code.trim().toUpperCase()).filter(Boolean))
  if (!master.size) return undefined
  const scores = indices.map(index => {
    const codes = new Set(rows.map(row => (row[index] ?? '').trim().toUpperCase()))
    return { index, count: [...codes].filter(code => master.has(code)).length }
  }).sort((left, right) => right.count - left.count)
  return scores[0].count > scores[1].count ? scores[0].index : undefined
}

export function autoMapBulkImportColumns(sheet: ImportPreviewSheet, definition: BulkImportDefinition, existingEmployeeCodes: readonly string[] = []): BulkImportMapping {
  const source = sheet.headers.map((header, index) => ({ index, normalized: normalizeBulkImportHeader(header.match(/\[(CUSTOM:[A-Z0-9_]+:[A-Z0-9_]+:[A-Z0-9_]+|SALARY_INPUT:\d+)\]\s*$/i)?.[1] || header) }))
  const used = new Set<number>()
  const result: BulkImportMapping = {}
  const salaryAliases = new Map<string, number>()
  for (const field of definition.fields.filter(field => field.code.startsWith('SALARY_INPUT:'))) {
    for (const alias of new Set(field.aliases?.map(normalizeBulkImportHeader))) salaryAliases.set(alias, (salaryAliases.get(alias) ?? 0) + 1)
  }
  for (const field of definition.fields) {
    if (definition.moduleCode === 'EMPLOYEE' && field.code === 'EmployeeCode') {
      const indices = employeeCodeSourceColumns(sheet.headers, definition).filter(index => !used.has(index))
      const index = bestEmployeeCodeColumn(sheet.rows, indices, existingEmployeeCodes)
      if (index !== undefined) { result[field.code] = index; used.add(index) }
      continue
    }
    const candidates = [field.header, ...(!field.code.startsWith('CUSTOM:') ? [field.label] : []), field.code, ...(field.aliases ?? [])].map(normalizeBulkImportHeader).filter(Boolean)
    const isSalary = field.code.startsWith('SALARY_INPUT:')
    const matches = source.filter(column => !(definition.moduleCode === 'EMPLOYEE' && column.normalized === 'ctc')
      && !used.has(column.index) && candidates.includes(column.normalized)
      && (!isSalary || (salaryAliases.get(column.normalized) ?? 0) <= 1))
    // A sheet can contain separate monthly and earned columns both labelled BASIC.
    // Let the user pick one instead of silently taking whichever appears first.
    const marked = isSalary ? matches.filter(column => column.normalized === normalizeBulkImportHeader(field.code)) : []
    const eligible = marked.length ? marked : matches
    const match = !isSalary || eligible.length === 1 ? eligible[0] : undefined
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
  if (codeGeneration && mappings.EmployeeCode === undefined && employeeCodeSourceColumns(sheet.headers, definition).length > 1) {
    throw new Error('Select the Employee Code source column before generating blank employee codes.')
  }
  const split = options.nameSplit
  const firstNameCode = split?.firstNameFieldCode ?? 'FirstName'
  const lastNameCode = split?.lastNameFieldCode ?? 'LastName'
  const transformedCodes = new Set<string>()
  if (split && mappings[firstNameCode] === undefined) transformedCodes.add(firstNameCode)
  if (split && mappings[lastNameCode] === undefined) transformedCodes.add(lastNameCode)
  if (codeGeneration) transformedCodes.add('EmployeeCode')
  const mapEmployeeCtc = definition.moduleCode === 'EMPLOYEE' && (mappings.AnnualCtc !== undefined || mappings.MonthlyCtc !== undefined)
  const isCtcField = (code: string) => mapEmployeeCtc && (code === 'AnnualCtc' || code === 'MonthlyCtc')
  // Upsert must remain sparse: an unmapped default such as Active or Portal
  // Access must not overwrite an existing employee. Insert mode may materialize
  // defaults because every row is guaranteed to be new.
  const outputFields = definition.fields.filter(field => mappings[field.code] !== undefined || isCtcField(field.code) || transformedCodes.has(field.code) || (operation === 'insert' && field.defaultValue !== undefined) || field.required)
  const outputHeaders = outputFields.map(field => field.header)
  const salaryFields = outputFields.filter(field => field.code.startsWith('SALARY_INPUT:')
    || (isCtcField(field.code) && options.employeeSalaryContext && mappings[field.code] !== undefined))
  const assignNewHires = operation !== 'update' && !!options.employeeSalaryContext?.effectiveFrom
  const salaryContext = salaryFields.length || assignNewHires ? options.employeeSalaryContext : undefined
  if (salaryFields.length || assignNewHires) {
    if (!salaryContext || !/^[1-9]\d*$/.test(salaryContext.templateId) || !/^\d{4}-\d{2}-\d{2}$/.test(salaryContext.effectiveFrom)) {
      throw new Error('Choose a salary template and effective date for the mapped salary inputs.')
    }
    if (['SalaryJson', 'SalaryTemplate', 'SalaryTemplateId'].some(code => mappings[code] !== undefined)) {
      throw new Error('Remove the separate Salary Json or Salary Template mapping before mapping individual salary inputs.')
    }
    outputHeaders.push('Salary Template Id', 'Salary Effective From')
  }
  const generatedCodes = buildEmployeeCodes(sheet.rows, mappings.EmployeeCode, codeGeneration)
  let splitNames = options.preTransformedRows ?? 0
  const outputRows = sheet.rows.map((row, rowIndex) => {
    const nameParts = split ? splitBulkImportName(row[split.sourceColumnIndex] ?? '', split.strategy) : null
    if (nameParts?.firstName || nameParts?.lastName) splitNames++
    const ctc = mapEmployeeCtc ? resolveEmployeeCtc(row[mappings.AnnualCtc] ?? '', row[mappings.MonthlyCtc] ?? '', !salaryContext) : null
    const values = outputFields.map(field => {
      if (ctc && field.code === 'AnnualCtc') return ctc.annual
      if (ctc && field.code === 'MonthlyCtc') return ctc.monthly
      const sourceIndex = mappings[field.code]
      if (sourceIndex !== undefined) {
        const direct = row[sourceIndex] ?? ''
        if (definition.moduleCode === 'EMPLOYEE' && field.code.startsWith('SALARY_INPUT:')) return normalizeEmployeeSalaryAmount(direct)
        if (field.code !== 'EmployeeCode' || direct.trim() || !codeGeneration) return direct
      }
      if (field.code === 'EmployeeCode' && codeGeneration) return generatedCodes[rowIndex] ?? ''
      if (field.code === firstNameCode && nameParts) return nameParts.firstName
      if (field.code === lastNameCode && nameParts) return nameParts.lastName
      return operation === 'insert' ? field.defaultValue ?? '' : ''
    })
    if (salaryContext) {
      const hasSalaryInput = salaryFields.some(field => row[mappings[field.code]]?.trim())
      const useTemplate = hasSalaryInput || assignNewHires
      values.push(useTemplate ? salaryContext.templateId : '', useTemplate ? salaryContext.effectiveFrom : '')
    }
    return values
  })
  const mappedSourceColumns = new Set(Object.values(mappings))
  if (split) mappedSourceColumns.add(split.sourceColumnIndex)
  const sourceColumns = sheet.headers.length
  const outputName = `${withoutExtension(file.name)}-${definition.moduleCode.toLowerCase()}-mapped.xlsx`
  const columns: PreparedBulkImportColumn[] = outputFields.map(field => {
    const sourceColumnIndex = mappings[field.code]
    const mapped = sourceColumnIndex !== undefined
    const derivedCtc = !mapped && isCtcField(field.code)
    const generated = derivedCtc || field.code === 'EmployeeCode' && Boolean(codeGeneration)
    const sourceTransform = sourceColumnIndex === undefined ? undefined : options.sourceColumnTransforms?.[sourceColumnIndex]
    const transformed = (transformedCodes.has(field.code) || Boolean(sourceTransform)) && !generated
    return {
      targetCode: field.code,
      targetHeader: field.header,
      sourceColumnIndex,
      sourceHeader: derivedCtc
        ? field.code === 'MonthlyCtc' ? 'Annual CTC ÷ 12' : 'Monthly CTC × 12'
        : sourceTransform
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
  if (salaryContext) {
    const color = bulkImportMappingColor(definition.fields.length)
    columns.push(
      { targetCode: 'SalaryTemplateId', targetHeader: 'Salary Template Id', sourceHeader: 'Selected salary template', kind: 'generated', color },
      { targetCode: 'SalaryEffectiveFrom', targetHeader: 'Salary Effective From', sourceHeader: 'Selected salary effective date', kind: 'generated', color },
    )
  }
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

export function nextEmployeeCodePreview(prefix: string, digits: number, existingCodes: string[], minimumNextSequence?: string) {
  const normalizedPrefix = prefix.trim().toUpperCase()
  const width = clampDigits(digits)
  const { next } = employeeCodeSequence(normalizedPrefix, width, existingCodes)
  const reserved = minimumNextSequence && /^\d+$/.test(minimumNextSequence) ? BigInt(minimumNextSequence) : 0n
  return `${normalizedPrefix}${String(next > reserved ? next : reserved).padStart(width, '0')}`
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

export function buildEmployeeCodes(rows: string[][], sourceIndex: number | undefined, configuration?: NonNullable<BulkImportTransformOptions['employeeCodeGeneration']>) {
  if (!configuration) return rows.map(() => '')
  const prefix = configuration.prefix.trim().toUpperCase()
  const digits = clampDigits(configuration.digits)
  const providedCodes = sourceIndex === undefined ? [] : rows.map(row => row[sourceIndex] ?? '').filter(value => value.trim())
  const used = new Set([...(configuration.existingCodes ?? []), ...providedCodes].map(value => value.trim().toUpperCase()).filter(Boolean))
  let { next } = employeeCodeSequence(prefix, digits, [...used])
  return rows.map(row => {
    const provided = sourceIndex === undefined ? '' : (row[sourceIndex] ?? '').trim()
    if (provided) return ''
    let candidate: string
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
    const suffix = match ? BigInt(match[1]) : 0n
    return suffix > value ? suffix : value
  }, 0n)
  return { next: maximum + 1n, digits }
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
