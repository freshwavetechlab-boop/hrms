import { useEffect, useMemo, useRef, useState, type DragEvent } from 'react'
import { CheckCircleOutlined, CloudUploadOutlined, FileExcelOutlined, SearchOutlined, UploadOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Input, InputNumber, Modal, Select, Spin, Tag } from 'antd'
import type { Component, Structure, WorkLocation } from '../types/payroll'
import type { SourceSalaryScenario } from '../services/payrollCalculationService'
import { extractSalaryTemplateExcel, simulateSalaryTemplateExcelScenario, type SalaryTemplateExcelExtractionRequest as SalaryTemplateExcelRequest, type SalaryTemplateExcelExtractionResult as SalaryTemplateExcelResult } from '../services/salaryTemplateExcelService'
import { getExcelPayslipProfile, suggestExcelPayslipMapping } from '../services/excelPayslipService'
import { defaultPayslipMapping, inferPayslipMonth, payslipColumnKinds, payslipHeaders, payslipHeaderSignature, type PayslipColumn, type PayslipColumnKind, type PayslipSheet } from '../utils/excelPayslipImport'
import { suggestPayslipWorksheet } from '../utils/excelPayslipConsolidation'
import { buildPayslipMappingEvidence } from '../utils/excelPayslipMapping'
import { captureWorkbook, collectWorkbookDependencies, workbookSourcesConfirmed, type CapturedWorkbook, type WorkbookMapping } from '../utils/workbookDependencyCapture'
import { analyzeSalaryTemplateSheet, buildSalaryTemplateExcelSource, selectedSalaryTemplateExcelBindings, suggestSalaryExcelHeaderRows, type SalaryTemplateExcelColumn } from '../utils/salaryTemplateExcel'
import './SalaryTemplateExcelExtractor.css'

export type SalaryTemplateExtractedDraft = {
  structure: Structure
  newComponents: Component[]
  sourceScenario: SourceSalaryScenario
  source: { fileName: string; sheetName: string; month: string; workLocationLabel?: string }
}
type Props = { clientId: number; clientName: string; components: Component[]; locations?: WorkLocation[]; canCreateComponentMasters?: boolean; onClose: () => void; onUseDraft: (draft: SalaryTemplateExtractedDraft) => void }
type ColumnChoice = { selected: boolean; kind: string; code: string; name: string; existingComponentId?: string; inputMode?: 'monthly' | 'period' }
const roles = [
  ['earning', 'Earning'], ['deduction', 'Employee deduction'], ['employer', 'Employer benefit'], ['input', 'Formula input'],
  ['payrollDays', 'Payroll divisor days'], ['presentDays', 'Actual present days'], ['payableDays', 'Payable days'],
  ['monthlyCtc', 'Monthly CTC input'], ['annualCtc', 'Annual CTC input'],
  ['grossTotal', 'Gross control total'], ['deductionTotal', 'Deduction control total'], ['netPay', 'Net control total'], ['ignore', 'Ignore'],
].map(([value, label]) => ({ value, label }))
const money = (value?: number | null) => value == null ? 'Not supplied' : value.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 6 })
const currentMonth = () => { const now = new Date(); return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}` }
const financial = (kind: string) => ['earning', 'deduction', 'employer', 'input'].includes(kind)
const errorMessage = (error: unknown) => error instanceof Error ? error.message : String(error)
const unusedCode = (code: string, components: Component[]) => {
  const occupied = new Set(components.map(component => component.code.toUpperCase()))
  if (!occupied.has(code.toUpperCase())) return code
  const root = `${code.slice(0, 40)}_XL_SOURCE`; let next = root, index = 2
  while (occupied.has(next.toUpperCase())) next = `${root}_${index++}`
  return next
}

function defaults(columns: SalaryTemplateExcelColumn[]): Record<number, ColumnChoice> {
  return Object.fromEntries(columns.map(column => [column.columnIndex, { selected: column.suggestedRole !== 'ignore', kind: column.suggestedRole, code: column.suggestedCode, name: column.label, existingComponentId: column.matchedComponentId, inputMode: 'monthly' }]))
}

function extractedDraft(result: SalaryTemplateExcelResult, components: Component[], source: SalaryTemplateExtractedDraft['source'], clientId: number, excelSource: Structure['excelSource']): SalaryTemplateExtractedDraft {
  if (!result.canCreateDraft || !result.scenario?.snapshot) throw new Error('Extract and validate this source before using its template draft.')
  const existing = new Map(components.map(component => [String(component.id), component]))
  const used = new Set(components.map(component => component.id)), remap = new Map<string, string>(), newComponents: Component[] = []
  let nextId = Date.now()
  for (const candidate of result.components) {
    const master = candidate.existingComponentId ? existing.get(String(candidate.existingComponentId)) : undefined
    if (candidate.existingComponentId && !master) throw new Error('An existing salary component changed. Extract again before using this draft.')
    if (master) {
      if (master.code !== candidate.code || master.category !== candidate.category) throw new Error('The selected shared component does not match this source role. Re-extract with a compatible component.')
      if (candidate.requiresEmployeeInput && (master.value.trim() || master.formula.trim())) throw new Error('Choose a separate blank input component; this shared master has a default amount or formula.')
      remap.set(String(candidate.id), String(master.id)); continue
    }
    while (used.has(nextId)) nextId++
    const id = nextId++; used.add(id); remap.set(String(candidate.id), String(id))
    newComponents.push({ id, code: candidate.code, name: candidate.name, category: candidate.category, componentRole: candidate.componentRole,
      componentType: 'Salary', statutoryType: candidate.statutoryType, payType: candidate.payType || 'Fixed Pay', calculationType: candidate.calculationType,
      value: candidate.value || '', formula: candidate.formula || '', baseComponent: candidate.baseComponent || '',
      taxable: candidate.taxable, ctc: candidate.category === 'Earning', proRata: candidate.proRata, fbp: false, restrictFbp: false,
      epf: '', esi: false, recurring: true, scheduled: false, investmentType: '', correctionOf: '', active: true, priority: String(candidate.priority),
    })
  }
  const lines = result.lines.map(line => ({ ...line, componentId: remap.get(String(line.componentId)) || String(line.componentId) }))
  const scenario = result.scenario
  const snapshot = { ...scenario.snapshot!, components: scenario.snapshot!.components.map(component => ({ ...component, id: remap.get(String(component.id)) || String(component.id) })), lines: scenario.snapshot!.lines.map(line => ({ ...line, componentId: remap.get(String(line.componentId)) || String(line.componentId) })) }
  const sourceScenario: SourceSalaryScenario = { mode: 'Template', clientId, templateId: '', payPeriod: scenario.payPeriod, snapshot, payrollDays: scenario.payrollDays!, presentDays: scenario.presentDays!, payableDays: scenario.payableDays!, annualCtc: scenario.annualCtc ?? 0,
    amountOverrides: Object.fromEntries(Object.entries(scenario.amountOverrides || {}).map(([key, value]) => [remap.get(key) || key, value])),
  }
  return { structure: { id: 0, clientId: String(clientId), name: result.templateName, annualCtc: '', active: true, lines, excelSource }, newComponents, sourceScenario, source }
}

export default function SalaryTemplateExcelExtractor({ clientId, clientName, components, locations = [], canCreateComponentMasters = false, onClose, onUseDraft }: Props) {
  const fileInput = useRef<HTMLInputElement>(null), generation = useRef(0)
  const [fileName, setFileName] = useState(''), [sheets, setSheets] = useState<PayslipSheet[]>([]), [sheetIndex, setSheetIndex] = useState(0)
  const [books, setBooks] = useState<CapturedWorkbook[]>([]), [mappings, setMappings] = useState<WorkbookMapping[]>([]), [reviewedCaptureKey, setReviewedCaptureKey] = useState(''), [mappingNotice, setMappingNotice] = useState('')
  const [headerRow, setHeaderRow] = useState(1), [sampleRow, setSampleRow] = useState<number>()
  const [month, setMonth] = useState(currentMonth), [locationLabel, setLocationLabel] = useState(''), [templateName, setTemplateName] = useState('')
  const [fixedPayrollDays, setFixedPayrollDays] = useState<number | null>(null), [payrollDaysCell, setPayrollDaysCell] = useState('')
  const nameEdited = useRef(false)
  const [choices, setChoices] = useState<Record<number, ColumnChoice>>({}), [columnSearch, setColumnSearch] = useState('')
  const [profileLoading, setProfileLoading] = useState(false), [mappingState, setMappingState] = useState<{ key: string; columns: PayslipColumn[] } | null>(null)
  const [busy, setBusy] = useState(''), [error, setError] = useState(''), [dragOver, setDragOver] = useState(false), [variantReviewed, setVariantReviewed] = useState(false)
  const [result, setResult] = useState<SalaryTemplateExcelResult | null>(null), [presentDays, setPresentDays] = useState<number | null>(null), [whatIf, setWhatIf] = useState<SalaryTemplateExcelResult['simulation']>(null)
  const sheet = sheets[sheetIndex]
  const headerValues = JSON.stringify(sheet ? payslipHeaders(sheet, headerRow) : [])
  const headers = useMemo<string[]>(() => JSON.parse(headerValues), [headerValues])
  const mappingKey = `${clientId}:${headerValues}`
  const mappedColumns = mappingState?.key === mappingKey ? mappingState.columns : undefined
  const analysis = useMemo(() => sheet ? analyzeSalaryTemplateSheet(sheet, headerRow, sampleRow, components, sheets, mappedColumns) : null, [sheet, headerRow, sampleRow, components, sheets, mappedColumns])
  const headerCandidates = useMemo(() => sheet ? suggestSalaryExcelHeaderRows(sheet) : [], [sheet])
  const dependencyReport = useMemo(() => {
    if (!sheet || !books[0]) return null
    try {
      const source = buildSalaryTemplateExcelSource(sheet, headerRow)
      const kinds: Record<string, PayslipColumnKind> = { earning: 'earning', deduction: 'deduction', employer: 'employer', netPay: 'netPay', grossTotal: 'grossTotal', deductionTotal: 'deductionTotal', ignore: 'ignore' }
      const columns = source.columns.map(column => ({ ...column, kind: kinds[choices[column.columnIndex]?.selected ? choices[column.columnIndex].kind : 'ignore'] || 'info' as PayslipColumnKind }))
      return collectWorkbookDependencies(sheet, books[0].key, books, mappings, columns, headerRow)
    } catch (caught) { return { issues: [errorMessage(caught)], references: [], reviewKey: 'invalid', needsReview: true, fields: null } }
  }, [sheet, headerRow, books, mappings, choices])
  const sourceConfirmed = !!dependencyReport && workbookSourcesConfirmed(dependencyReport, reviewedCaptureKey)
  const dependencyAliases = [...new Map((dependencyReport?.references || []).filter(reference => reference.kind === 'External').map(reference => [`${reference.fromWorkbookKey}|${reference.reference}`, reference])).values()]
  const requestKey = JSON.stringify({ clientId, sheetIndex, headerRow, sampleRow: analysis?.sampleRow, choices, month, templateName, locationLabel, fixedPayrollDays, payrollDaysCell, capture: dependencyReport?.reviewKey, reviewedCaptureKey })
  const extractionKey = useRef('')
  const currentResult = result && extractionKey.current === requestKey ? result : null
  const selectedCount = analysis?.columns.filter(column => choices[column.columnIndex]?.selected && choices[column.columnIndex]?.kind !== 'ignore').length ?? 0
  // Excel's used range can include hundreds of formatting-only columns. Keep
  // their coordinates in the capture while showing actual headers/data/dependencies.
  const sourceColumns = useMemo(() => {
    if (!analysis || !sheet) return []
    const required = new Set(analysis.columns.flatMap(column => column.dependencies))
    return analysis.columns.filter(column => required.has(column.columnIndex) || sheet.rows.some(row => {
      if (row.sourceRow < headerRow) return false
      const cell = row.cells[column.columnIndex]
      return !!cell && (!!cell.value.trim() || !!cell.formulaText || !!cell.error || !!cell.calculationError || !!cell.missingCachedValue)
    }))
  }, [analysis, sheet, headerRow])
  const shownColumns = sourceColumns.filter(column => !columnSearch.trim() || `${column.header} ${column.letter} ${choices[column.columnIndex]?.code || ''}`.toLowerCase().includes(columnSearch.trim().toLowerCase()))
  const activeResult = whatIf || currentResult?.simulation
  const previewRows = sheet ? [...new Map([...sheet.rows.slice(0, 6), ...sheet.rows.filter(row => row.sourceRow >= headerRow - 2 && row.sourceRow <= headerRow + 3)].map(row => [row.sourceRow, row])).values()].sort((a, b) => a.sourceRow - b.sourceRow) : []
  const previewColumns = Math.min(12, sheet?.columnCount || 0)
  const invalidate = () => { generation.current++; setResult(null); setWhatIf(null); setPresentDays(null); setError(''); setBusy(''); setVariantReviewed(false) }
  useEffect(() => () => { generation.current++ }, [])
  useEffect(() => { if (analysis) setChoices(defaults(analysis.columns)) }, [analysis])
  useEffect(() => {
    if (!sheet || !clientId || !headers.length) return
    let active = true
    const controller = new AbortController(), editVersion = generation.current
    const valid = (columns: PayslipColumn[]) => columns.length === headers.length && columns.every((column, index) => column.columnIndex === index && column.sourceHeader === headers[index] && payslipColumnKinds.some(kind => kind.value === column.kind))
    setProfileLoading(true)
    void Promise.resolve().then(async () => {
      try {
        const signature = await payslipHeaderSignature(headers)
        if (!active || generation.current !== editVersion) return
        const embedded = sheet.template
        if (embedded?.clientId === clientId && embedded.headerRow === headerRow && valid(embedded.columns)) {
          invalidate(); setMappingState({ key: mappingKey, columns: embedded.columns }); setMappingNotice('Saved worksheet mapping restored, using the same header preset as Excel Payslips.'); return
        }
        const response = await getExcelPayslipProfile(clientId, signature, controller.signal)
        if (!active || generation.current !== editVersion) return
        if (response.ok && response.data?.headerSignature === signature && Array.isArray(response.data.columns) && valid(response.data.columns)) {
          invalidate(); setMappingState({ key: mappingKey, columns: response.data.columns }); setMappingNotice('Saved client mapping restored for these exact column headings. Source formulas and salary inputs are retained.'); return
        }
        setMappingState(null)
        if (!response.ok && response.status !== 404) setMappingNotice('Saved client mapping is temporarily unavailable. Source-based suggestions remain available; the saved preset has not been changed.')
      } catch (caught) { if (active && generation.current === editVersion && !controller.signal.aborted) setMappingNotice(`Saved client mapping is unavailable: ${errorMessage(caught)}`) }
      finally { if (active) setProfileLoading(false) }
    })
    return () => { active = false; controller.abort() }
  }, [mappingKey, headerRow, sheet]) // eslint-disable-line react-hooks/exhaustive-deps

  const changeHeader = (row: number) => { invalidate(); setHeaderRow(row); setSampleRow(undefined); setFixedPayrollDays(null); setPayrollDaysCell('') }
  const openSheet = (index: number, workbook = sheets) => {
    const next = workbook[index], candidates = suggestSalaryExcelHeaderRows(next)
    invalidate(); setSheetIndex(index); setHeaderRow(candidates[0]?.row || next.rows[0]?.sourceRow || 1); setSampleRow(undefined); setFixedPayrollDays(null); setPayrollDaysCell('')
    const sourceMonth = inferPayslipMonth(next) || month
    setMonth(sourceMonth); nameEdited.current = false
    setTemplateName(`${clientName} · ${next.name}${locationLabel ? ` · ${locationLabel}` : ''} · ${sourceMonth}`.slice(0, 180))
  }
  const loadFile = async (file?: File) => {
    if (!file) return
    invalidate(); const token = ++generation.current; setBusy('Reading workbook…')
    try {
      const captured = await captureWorkbook(file), workbook = captured.sheets
      if (token !== generation.current) return
      setFileName(file.name); setBooks([captured]); setMappings([]); setReviewedCaptureKey(''); setSheets(workbook); const preferred = suggestPayslipWorksheet(workbook); openSheet(Math.max(0, workbook.findIndex(item => item === preferred?.sheet)), workbook); setBusy('')
    } catch (caught) { if (token === generation.current) { setError(errorMessage(caught)); setBusy('') } }
  }
  const dropFile = (event: DragEvent<HTMLDivElement>) => { event.preventDefault(); setDragOver(false); void loadFile(event.dataTransfer.files[0]) }
  const changeContext = (nextMonth: string, nextLocation: string) => {
    invalidate(); setMonth(nextMonth); setLocationLabel(nextLocation)
    if (sheet && !nameEdited.current) setTemplateName(`${clientName} · ${sheet.name}${nextLocation ? ` · ${nextLocation}` : ''} · ${nextMonth}`.slice(0, 180))
  }
  const updateChoice = (index: number, patch: Partial<ColumnChoice>) => { invalidate(); setChoices(current => {
    const previous = current[index], next = { ...current, [index]: { ...previous, ...patch } }
    if (patch.code && previous?.code !== patch.code && financial(next[index].kind)) {
      for (const dependency of analysis?.columns[index]?.dependencies || []) if (current[dependency]?.kind === 'input' && current[dependency].code === previous?.code) next[dependency] = { ...current[dependency], code: patch.code }
    }
    return next
  }) }
  const addDependencyFiles = async (files: File[]) => {
    if (!files.length) return
    invalidate(); const token = ++generation.current; setBusy('Reading supplied source workbooks…')
    try { const captured = await Promise.all(files.map(captureWorkbook)); if (token !== generation.current) return; setBooks(current => [...new Map([...current, ...captured].map(book => [book.key, book])).values()]); setReviewedCaptureKey(''); setBusy('') }
    catch (caught) { if (token === generation.current) { setBusy(''); setError(errorMessage(caught)) } }
  }
  const suggestRoles = async () => {
    if (!sheet || !analysis) return
    const token = ++generation.current, controller = new AbortController(); setBusy('Reviewing columns with the existing Excel mapping service…'); setError('')
    try {
      const baseline = defaultPayslipMapping(analysis.columns.map(column => column.header), sheet, headerRow)
      const response = await suggestExcelPayslipMapping(clientId, buildPayslipMappingEvidence(sheet, headerRow, baseline, '', [], sheets), controller.signal)
      if (token !== generation.current) return
      setBusy('')
      if (!response.ok || !response.data || response.data.method !== 'ai') { setMappingNotice('AI suggestions are unavailable. Existing source-based suggestions are retained.'); return }
      const suggestion = response.data
      if (suggestion.columns.length !== analysis.columns.length || suggestion.columns.some((column, index) => column.columnIndex !== index || !payslipColumnKinds.some(kind => kind.value === column.kind))) { setMappingNotice('Mapping suggestions did not match the source columns. Current roles are retained.'); return }
      // Re-run the shared source discovery from the supplied roles. A computed
      // helper previously guessed as Input must be allowed to become an earning
      // or a control total; proven monthly bases are derived again from evidence.
      invalidate(); setMappingState({ key: mappingKey, columns: suggestion.columns.map((column, index) => ({ ...column, sourceHeader: headers[index] })) })
      setMappingNotice(`Existing Excel mapping suggestions applied. ${suggestion.reviewColumnIndexes.length ? `${suggestion.reviewColumnIndexes.length} columns need review.` : 'Review the roles and extract the formulas.'}`)
    } catch (caught) { if (token === generation.current) { setBusy(''); setMappingNotice(`Suggestions are unavailable: ${errorMessage(caught)}`) } }
  }
  const extract = async () => {
    if (!sheet || !analysis || !analysis.sampleRow) return
    if (profileLoading || bindingIssues.length) { setError(profileLoading ? 'Wait for the saved client mapping to finish loading.' : bindingIssues.map(issue => issue.message).join(' ')); return }
    setError(''); setBusy('Extracting formulas and checking the native calculation…'); setWhatIf(null); setPresentDays(null)
    const token = ++generation.current
    try {
      const source = buildSalaryTemplateExcelSource(sheet, headerRow)
      if (!dependencyReport?.fields || !sourceConfirmed) throw new Error('Supply and review the Excel formula sources before extracting a reusable template.')
      Object.assign(source, dependencyReport.fields, { dependencyReviewStatus: 'Reviewed' })
      const selectedColumns = selectedBindings
      const hasDaysColumn = selectedColumns.some(column => column.kind === 'payrollDays')
      const response = await extractSalaryTemplateExcel({ clientId, templateName: templateName.trim(), month, source, sampleRow: analysis.sampleRow, selectedColumns, ...(!hasDaysColumn && fixedPayrollDays !== null ? { fixedPayrollDays } : {}), ...(!hasDaysColumn && payrollDaysCell.trim() ? { payrollDaysCell: payrollDaysCell.trim() } : {}) })
      if (token !== generation.current) return
      setBusy('')
      if (!response.ok || !response.data) { setError(response.error || 'The formula source could not be extracted.'); return }
      extractionKey.current = requestKey; setResult(response.data)
    } catch (caught) { if (token === generation.current) { setBusy(''); setError(errorMessage(caught)) } }
  }
  const simulate = async () => {
    if (!currentResult?.scenario || presentDays == null) return
    const token = ++generation.current; setBusy('Calculating the temporary present-days scenario…'); setError('')
    const scenario = { ...currentResult.scenario, presentDays, payableDays: presentDays }
    try {
      const response = await simulateSalaryTemplateExcelScenario(scenario)
      if (token !== generation.current) return
      setBusy(''); if (response.ok && response.data) setWhatIf(response.data); else setError(response.error || 'Review the simulation inputs.')
    } catch (caught) { if (token === generation.current) { setBusy(''); setError(errorMessage(caught)) } }
  }
  const useDraft = () => {
    if (!currentResult || !sheet || !canUse || busy) return
    try { onUseDraft(extractedDraft(currentResult, components, { fileName, sheetName: sheet.name, month, ...(locationLabel ? { workLocationLabel: locationLabel } : {}) }, clientId, { version: 1, fileName, sheetName: sheet.name, headerRow, sampleRow: analysis!.sampleRow, payPeriod: month, ...(locationLabel ? { workLocationLabel: locationLabel } : {}), columns: analysis!.columns.filter(column => choices[column.columnIndex]?.selected && choices[column.columnIndex].kind !== 'ignore').map(column => ({ columnIndex: column.columnIndex, sourceHeader: column.header, kind: choices[column.columnIndex].kind, code: choices[column.columnIndex].code, originalFormula: column.sampleFormula, nativeFormula: currentResult.components.find(candidate => candidate.columnIndex === column.columnIndex)?.formula || '' })) })) }
    catch (caught) { setError(errorMessage(caught)) }
  }
  const selectedBindings = useMemo(() => selectedSalaryTemplateExcelBindings((analysis?.columns || []).map(column => { const choice = choices[column.columnIndex] || defaults([column])[column.columnIndex]; return { columnIndex: column.columnIndex, code: choice.code, name: choice.name, kind: choice.selected ? choice.kind : 'ignore', existingComponentId: choice.existingComponentId, inputMode: choice.inputMode } })) as SalaryTemplateExcelRequest['selectedColumns'], [analysis, choices])
  const bindingIssues = useMemo(() => {
    const issues: { columnIndices: number[]; message: string }[] = []
    const describe = (index: number) => { const column = analysis?.columns[index]; return column ? `${column.letter} · ${column.header || column.label}` : `Column ${index + 1}` }
    const group = (columns: typeof selectedBindings, label: string) => {
      for (const code of [...new Set(columns.map(column => column.code.toUpperCase()))]) {
        const duplicates = columns.filter(column => column.code.toUpperCase() === code)
        if (duplicates.length > 1) issues.push({ columnIndices: duplicates.map(column => column.columnIndex), message: `${duplicates.map(column => describe(column.columnIndex)).join(' and ')} both use ${label} “${code}”. Keep one binding or correct the column roles.` })
      }
    }
    group(selectedBindings.filter(column => column.kind === 'input'), 'the same formula input')
    group(selectedBindings.filter(column => ['earning', 'deduction', 'employer'].includes(column.kind)), 'the same salary component code')
    for (const role of ['payrollDays', 'presentDays', 'payableDays', 'monthlyCtc', 'annualCtc', 'netPay', 'grossTotal', 'deductionTotal']) {
      const repeated = selectedBindings.filter(column => column.kind === role)
      if (repeated.length > 1) issues.push({ columnIndices: repeated.map(column => column.columnIndex), message: `${repeated.map(column => describe(column.columnIndex)).join(' and ')} are all marked “${roles.find(option => option.value === role)?.label}”. Select the single source control for this template.` })
    }
    if (analysis && !selectedBindings.some(column => column.kind === 'earning')) issues.push({ columnIndices: [], message: 'Select at least one source earning column before extracting a salary template.' })
    return issues
  }, [analysis, selectedBindings])
  const canExtract = !!analysis?.sampleRow && selectedCount > 0 && !!templateName.trim() && /^\d{4}-(0[1-9]|1[0-2])$/.test(month) && sourceConfirmed && !profileLoading && !bindingIssues.length
  const needsNewMasters = !!currentResult?.components.some(component => !component.existingComponentId)
  const unsafeDefaults = currentResult?.components.some(candidate => candidate.requiresEmployeeInput && candidate.existingComponentId && !!components.filter(component => String(component.id) === candidate.existingComponentId).some(component => component.value.trim() || component.formula.trim()))
  const collidingCodes = currentResult?.components.filter(candidate => !candidate.existingComponentId && components.some(master => master.code.toUpperCase() === candidate.code.toUpperCase())).map(candidate => candidate.code) || []
  const canUse = !!currentResult?.canCreateDraft && !!currentResult.scenario && (!analysis?.hasFormulaVariants || variantReviewed) && (!needsNewMasters || canCreateComponentMasters) && !unsafeDefaults && !collidingCodes.length
  const sourceReview = sheet && dependencyReport && (dependencyReport.references.length > 0 || dependencyReport.issues.length > 0) ? <details className="salary-excel-source-review" open={dependencyReport.issues.length > 0 || undefined}>
    <summary>Linked formula sources <Tag>{books.length} supplied file{books.length === 1 ? '' : 's'}</Tag><Tag color={sourceConfirmed ? 'green' : 'orange'}>{sourceConfirmed ? 'Included' : 'Source needed'}</Tag></summary>
    <div><p>Same-file worksheets are included automatically. For an external reference, supply the exact file used by the formula.</p>
      <label className="salary-excel-field"><span>Add a linked source workbook</span><input aria-label="Supply extraction dependency workbook" type="file" accept=".xlsx,.csv" multiple disabled={!!busy} onChange={event => { void addDependencyFiles(Array.from(event.target.files || [])); event.target.value = '' }} /></label>
      {books.map(book => <div className="salary-excel-source-file" key={book.key}><strong>{book.fileName}</strong><Tag>{book.sha256.slice(0, 12)}</Tag><span>{book.sheets.length} sheets</span></div>)}
      {dependencyAliases.map(alias => { const mapping = mappings.find(item => item.fromWorkbookKey === alias.fromWorkbookKey && item.reference === alias.reference); const patch = (key: string, reviewed: boolean) => { invalidate(); setReviewedCaptureKey(''); setMappings(current => [...current.filter(item => item.fromWorkbookKey !== alias.fromWorkbookKey || item.reference !== alias.reference), { fromWorkbookKey: alias.fromWorkbookKey, reference: alias.reference, toWorkbookKey: key, reviewed }]) }; return <div className="salary-excel-source-alias" key={`${alias.fromWorkbookKey}:${alias.reference}`}><strong>{alias.reference} / {alias.targetSheet}</strong><Select aria-label={`Supplied file for ${alias.reference}`} value={mapping?.toWorkbookKey || undefined} disabled={!!busy} placeholder="Choose exact supplied workbook" options={books.map(book => ({ value: book.key, label: `${book.fileName} · ${book.sha256.slice(0, 12)}` }))} onChange={value => patch(value, false)} /><Checkbox checked={mapping?.reviewed === true} disabled={!!busy || !mapping?.toWorkbookKey} onChange={event => patch(mapping!.toWorkbookKey, event.target.checked)}>This is the exact workbook for this reference</Checkbox></div> })}
      {dependencyReport.issues.map((issue, index) => <Alert key={index} type="warning" message={issue} />)}
      {!dependencyReport.issues.length && !sourceConfirmed && <Checkbox disabled={!!busy} checked={reviewedCaptureKey === dependencyReport.reviewKey} onChange={event => { invalidate(); setReviewedCaptureKey(event.target.checked ? dependencyReport.reviewKey : '') }}>Use these supplied files as the formula sources</Checkbox>}
    </div>
  </details> : null
  const divisorReview = sheet && analysis && !Object.values(choices).some(choice => choice.selected && choice.kind === 'payrollDays') ? <div className="salary-excel-divisor-review"><div><strong>Payroll divisor</strong><p>No payroll-day column is selected. Use the divisor from this source; it may be 26, 30 or calendar days.</p></div><label className="salary-excel-field"><span>Fixed source days</span><InputNumber aria-label="Source payroll divisor days" min={1} max={31} precision={0} value={fixedPayrollDays} placeholder="e.g. 26" disabled={!!busy} onChange={value => { invalidate(); setFixedPayrollDays(value) }} /></label><label className="salary-excel-field"><span>Or shared source cell</span><Input aria-label="Source payroll divisor cell" value={payrollDaysCell} placeholder="e.g. L2" disabled={!!busy} onChange={event => { invalidate(); setPayrollDaysCell(event.target.value) }} /></label></div> : null
  return <Modal open title={<span className="salary-excel-title"><FileExcelOutlined />Extract salary template from Excel</span>} width={1280} zIndex={1600} className="salary-excel-extractor" onCancel={onClose} footer={<div className="salary-excel-footer"><span>No employee, attendance or payroll records are imported here.</span><div><Button onClick={onClose}>Cancel</Button>{sheet && <Button icon={<SearchOutlined />} loading={!!busy} disabled={!canExtract} onClick={() => void extract()}>Extract & simulate</Button>}<Button type="primary" disabled={!canUse || !!busy} onClick={useDraft}>Use template draft</Button></div></div>}>
    <div className="salary-excel-client"><span>Client</span><strong>{clientName}</strong></div>
    <div className="salary-excel-steps"><span className={!sheet ? 'is-current' : ''}><b>1</b> Upload workbook</span><span className={sheet && !currentResult ? 'is-current' : ''}><b>2</b> Choose headers & columns</span><span className={currentResult ? 'is-current' : ''}><b>3</b> Check formulas & use draft</span></div>
    <input ref={fileInput} type="file" hidden accept=".xlsx,.csv" aria-label="Salary workbook file" onChange={event => { void loadFile(event.target.files?.[0]); event.currentTarget.value = '' }} />
    {error && <Alert showIcon type="error" message={error} />}
    {mappingNotice && <Alert showIcon type="info" message={mappingNotice} />}
    {profileLoading && <div className="salary-excel-mapping-status" role="status"><Spin size="small" /> Loading the saved mapping for these column headings…</div>}
    {!!bindingIssues.length && <Alert showIcon type="warning" message="Review the highlighted column bindings before extracting" description={<ul className="salary-excel-binding-issues">{bindingIssues.map((issue, index) => <li key={index}>{issue.message}</li>)}</ul>} />}
    {sourceReview}
    {divisorReview}
    {needsNewMasters && !canCreateComponentMasters && <Alert type="warning" showIcon message="An administrator must add the new component masters before this template can be saved." description="You can inspect the extraction and simulate it here. Existing shared masters remain unchanged." />}
    {unsafeDefaults && <Alert type="warning" showIcon message="A selected shared component has an existing default amount or formula." description="Choose its suggested separate source-input component, then extract again. Employee salary inputs must stay separate from a shared default." />}
    {!!collidingCodes.length && <Alert type="warning" showIcon message={`Component codes already exist: ${collidingCodes.join(', ')}`} description="Select the matching shared component or choose a separate source component, then extract again." />}
    {!sheet ? <div className={`salary-excel-upload${dragOver ? ' is-dragover' : ''}`} onDragOver={event => { event.preventDefault(); setDragOver(true) }} onDragLeave={() => setDragOver(false)} onDrop={dropFile}><CloudUploadOutlined /><h3>Start with your existing salary sheet</h3><p>Upload the workbook with its original formulas. Choose the header row and the columns you want to turn into a salary template.</p><Button icon={<UploadOutlined />} disabled={!!busy} onClick={() => fileInput.current?.click()}>Choose Excel file</Button><small>XLSX or CSV · up to 30 MB · formulas need an XLSX workbook</small>{busy && <div role="status"><Spin size="small" /> {busy}</div>}</div> : <>
      <div className="salary-excel-filebar"><FileExcelOutlined /><div><strong>{fileName}</strong><small>{sheets.length} worksheet{sheets.length === 1 ? '' : 's'} · original formulas retained</small></div><Button size="small" disabled={!!busy} onClick={() => void suggestRoles()}>Use suggested mapping</Button><Button size="small" disabled={!!busy} onClick={() => fileInput.current?.click()}>Replace file</Button></div>
      <div className="salary-excel-context"><label className="salary-excel-field"><span>Worksheet</span><Select aria-label="Source worksheet" value={sheetIndex} disabled={!!busy} options={sheets.map((item, index) => ({ value: index, label: item.name }))} onChange={index => openSheet(index)} /></label><label className="salary-excel-field"><span>Pay month</span><input aria-label="Extraction pay month" type="month" value={month} disabled={!!busy} onChange={event => changeContext(event.target.value, locationLabel)} /></label><label className="salary-excel-field"><span>Location label (optional)</span><input aria-label="Extraction location label" list={`salary-excel-locations-${clientId}`} value={locationLabel} disabled={!!busy} placeholder="Select or type a location" onChange={event => changeContext(month, event.target.value)} /><datalist id={`salary-excel-locations-${clientId}`}>{locations.filter(location => location.clientId === clientId && location.isActive).map(location => <option key={location.id} value={location.name} />)}</datalist></label><label className="salary-excel-field"><span>Template name</span><Input aria-label="Extracted template name" value={templateName} disabled={!!busy} onChange={event => { invalidate(); nameEdited.current = true; setTemplateName(event.target.value) }} /></label></div>
      <section className="salary-excel-section"><header><div><h3>Choose the header row</h3><p>Click the row containing the column names. Original Excel row numbers are kept.</p></div><div className="salary-excel-section-controls"><label className="salary-excel-field"><span>Header row</span><Select aria-label="Excel header row" showSearch value={headerRow} disabled={!!busy} options={[...new Map([...headerCandidates.map(item => ({ value: item.row, label: item.label })), ...sheet.rows.slice(0, 80).map(row => ({ value: row.sourceRow, label: `Row ${row.sourceRow}` }))].map(item => [item.value, item])).values()]} optionFilterProp="label" onChange={changeHeader} /></label><label className="salary-excel-field"><span>Sample employee row</span><Select aria-label="Excel sample employee row" value={analysis?.sampleRow} disabled={!!busy} options={analysis?.sampleRows.map(row => ({ value: row.sourceRow, label: row.label }))} onChange={value => { invalidate(); setSampleRow(value) }} /></label></div></header><div className="salary-excel-preview-scroll"><table className="salary-excel-preview-table" aria-label="Workbook header-row preview"><thead><tr><th>Row</th>{Array.from({ length: previewColumns }, (_, index) => <th key={index}>{String.fromCharCode(65 + index)}</th>)}</tr></thead><tbody>{previewRows.map(row => <tr key={row.sourceRow} className={row.sourceRow === headerRow ? 'is-header' : ''} onClick={() => { if (!busy) changeHeader(row.sourceRow) }}><td><button type="button" disabled={!!busy} aria-label={`Use row ${row.sourceRow} as headers`} onClick={event => { event.stopPropagation(); changeHeader(row.sourceRow) }}>{row.sourceRow}</button></td>{Array.from({ length: previewColumns }, (_, index) => <td key={index} title={row.cells[index]?.formulaText || row.cells[index]?.value || ''}><span>{row.cells[index]?.value || (row.cells[index]?.formulaText ? `=${row.cells[index].formulaText}` : '—')}</span></td>)}</tr>)}</tbody></table></div></section>
      <section className="salary-excel-section"><header><div className="salary-excel-columns-heading"><h3>Columns & formulas</h3><Tag>{selectedCount} selected</Tag><Tag>{analysis?.candidateRowCount || 0} data rows</Tag></div><div className="salary-excel-column-tools"><Input aria-label="Find Excel column" placeholder="Find a column" prefix={<SearchOutlined />} allowClear value={columnSearch} onChange={event => setColumnSearch(event.target.value)} /><Button type="text" size="small" disabled={!!busy} onClick={() => { invalidate(); setChoices(current => Object.fromEntries(Object.entries(current).map(([key, value]) => [key, { ...value, selected: value.kind !== 'ignore' }]))) }}>Select suggested</Button><Button type="text" size="small" disabled={!!busy} onClick={() => { invalidate(); setChoices(current => Object.fromEntries(Object.entries(current).map(([key, value]) => [key, { ...value, selected: false }]))) }}>Clear selection</Button></div></header><div className="salary-excel-columns-scroll"><table className="salary-excel-columns-table" aria-label="Extracted Excel columns"><thead><tr><th><Checkbox aria-label="Select all shown Excel columns" disabled={!!busy || !shownColumns.length} checked={!!shownColumns.length && shownColumns.every(column => choices[column.columnIndex]?.selected)} indeterminate={shownColumns.some(column => choices[column.columnIndex]?.selected) && !shownColumns.every(column => choices[column.columnIndex]?.selected)} onChange={event => { invalidate(); setChoices(current => ({ ...current, ...Object.fromEntries(shownColumns.map(column => [column.columnIndex, { ...current[column.columnIndex], selected: event.target.checked }])) })) }} /></th><th>Source column</th><th>Use as</th><th>Salary component</th><th>Sample amount</th><th>Original Excel → payroll formula</th><th>Check</th></tr></thead><tbody>{shownColumns.map(column => {
        const choice = choices[column.columnIndex] || defaults([column])[column.columnIndex], comparison = currentResult?.comparisons.find(item => item.columnIndex === column.columnIndex)
        const native = currentResult?.components.find(item => item.columnIndex === column.columnIndex)
        const masterOptions = components.filter(component => component.active).map(component => ({ value: String(component.id), label: `${component.name} · ${component.code}` }))
        return <tr key={column.columnIndex} className={choice.selected ? '' : 'is-excluded'}><td><Checkbox aria-label={`Include ${column.letter} ${column.header}`} disabled={!!busy} checked={choice.selected} onChange={event => updateChoice(column.columnIndex, { selected: event.target.checked })} /></td><td><div className="salary-excel-column-name"><strong>{column.label}</strong><small>{column.letter} · {column.header || 'Unnamed column'}</small></div></td><td><div className="salary-excel-role"><Select aria-label={`Role for column ${column.letter}`} disabled={!!busy} value={choice.kind} options={roles} onChange={kind => updateChoice(column.columnIndex, { kind, selected: kind !== 'ignore', existingComponentId: undefined })} />{['earning', 'deduction', 'employer'].includes(choice.kind) && !column.hasFormula && <><Select size="small" aria-label={`Amount basis for column ${column.letter}`} disabled={!!busy} value={choice.inputMode || 'monthly'} options={[{ value: 'monthly', label: 'Monthly rate · prorate by days' }, { value: 'period', label: 'Period amount · no second proration' }]} onChange={inputMode => updateChoice(column.columnIndex, { inputMode })} /><small>{choice.inputMode === 'period' ? 'Already-earned source input; attendance is not applied again.' : 'Full-month base; attendance adjusts earned pay.'}</small></>}</div></td><td>{financial(choice.kind) ? <Select aria-label={`Component for column ${column.letter}`} showSearch optionFilterProp="label" disabled={!!busy} value={choice.existingComponentId || ''} options={[{ value: '', label: `New · ${choice.code}` }, ...masterOptions]} onChange={value => { const master = components.find(component => String(component.id) === value); updateChoice(column.columnIndex, { existingComponentId: value || undefined, code: master?.code || unusedCode(column.suggestedCode, components), name: master?.name || column.label }) }} /> : <span className="salary-excel-footnote">Control / information</span>}</td><td><span className="salary-excel-plain-value">{column.sampleValue || 'Not supplied'}</span></td><td>{column.sampleFormula ? <code title={column.sampleFormula}>{column.sampleFormula.startsWith('=') ? '' : '='}{column.sampleFormula}</code> : <span className="salary-excel-footnote">Input value</span>}{native?.formula && <code>→ {native.formula}</code>}{column.totalFormulaVariants > 1 && <div className="salary-excel-column-notes">{column.totalFormulaVariants} formula patterns in this column</div>}</td><td>{comparison ? comparison.status === 'Matched' ? <span className="salary-excel-native-match"><CheckCircleOutlined />Source matched</span> : <span className="salary-excel-native-mismatch">Difference {money(comparison.variance)}</span> : column.issues.length ? <span className="salary-excel-column-notes">{column.issues.join(' ')}</span> : <span className="salary-excel-footnote">{choice.selected && choice.kind !== 'ignore' ? 'Ready to extract' : 'Excluded'}</span>}</td></tr>
      })}</tbody></table>{!shownColumns.length && <div className="salary-excel-empty-columns">No matching columns.</div>}</div></section>
      {analysis?.hasFormulaVariants && <Alert type="warning" showIcon message="More than one formula pattern exists in this sheet" description={<><p>This draft uses the selected sample row’s formula pattern. Choose a representative employee row for the location/rule you want.</p><Checkbox checked={variantReviewed} onChange={event => setVariantReviewed(event.target.checked)}>Use this selected formula pattern for the draft</Checkbox></>} />}
      {!!analysis?.issues.length && <ul className="salary-excel-issues">{analysis.issues.map((issue, index) => <li key={index}>{issue}</li>)}</ul>}
      {busy && <div role="status"><Spin size="small" /> {busy}</div>}
      {currentResult && <section className="salary-excel-section"><header><div><h3>{currentResult.canCreateDraft ? 'Native calculation checked' : 'Review the source before using a draft'}</h3><p>The source sample is checked before any template is saved.</p></div>{currentResult.scenario && <div className="salary-excel-result-head"><InputNumber aria-label="Extracted sample present days" min={0} max={currentResult.scenario.payrollDays || 31} step={0.5} value={presentDays} placeholder="Present days" onChange={setPresentDays} /><Button size="small" disabled={presentDays == null || !!busy} onClick={() => void simulate()}>Simulate days</Button></div>}</header>{activeResult && <div className="salary-excel-result-summary">{([['Gross earnings', activeResult.gross], ['Deductions', activeResult.deductions], ['Employer benefits', activeResult.benefits], ['Net payable', activeResult.net]] as const).map(([label, value]) => <div key={label}><span>{label}</span><strong>₹ {money(value)}</strong></div>)}</div>}{currentResult.issues.length > 0 && <ul className="salary-excel-issues">{currentResult.issues.map((issue, index) => <li key={index}>{issue.message}</li>)}</ul>}{whatIf?.issues.filter(issue => issue.severity === 'Error').map((issue, index) => <Alert key={index} type="error" message={`${issue.component}: ${issue.message}`} description={issue.action} />)}</section>}
      <p className="salary-excel-footnote">Formula inputs stay separate from earned salary. Source totals are checks, not duplicate earnings. Using a draft opens the existing template editor; Save template remains a separate action.</p>
    </>}
  </Modal>
}
