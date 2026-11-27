import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type DragEvent, type KeyboardEvent } from 'react'
import { Alert, Button, Checkbox, Drawer, Empty, Input, Select, Space, Tag } from 'antd'
import { CloudUploadOutlined, DeleteOutlined, DownloadOutlined, LinkOutlined, ReloadOutlined } from '@ant-design/icons'
import { parseImportSourceSheets, selectImportHeader, type ImportPreviewSheet, type ImportSourceSheet } from '../utils/importPreview'
import SmartBulkSourcePreview from './SmartBulkSourcePreview'
import EmployeeCodeGeneration from './EmployeeCodeGeneration'
import type { Component, Structure } from '../types/payroll'
import { employeeSalaryInputFields } from '../utils/employeeSalaryImport'
import {
  autoMapBulkImportColumns,
  employeeCodeSourceColumns,
  bulkImportMappingColor,
  mapBulkImportColumn,
  normalizeBulkImportHeader,
  inferEmployeeCodeDigits,
  materializeBulkImportNameSplit,
  nextEmployeeCodePreview,
  prepareMappedBulkImport,
  type BulkImportDefinition,
  type BulkImportMapping,
  type BulkImportNameSplitStrategy,
  type BulkImportOperation,
  type MaterializedBulkImportNameSplit,
  type PreparedBulkImport
} from '../utils/smartBulkImport'
import './SmartBulkUploadMapper.css'

type ImportMode = 'mapped' | 'template'
type Connector = { code: string; path: string; color: string }
type SheetNameSplit = Omit<MaterializedBulkImportNameSplit, 'sheet'> & { originalSheet: ImportPreviewSheet }

type Props = {
  open: boolean
  definition: BulkImportDefinition
  onCancel: () => void
  onPrepared: (result: PreparedBulkImport) => void | Promise<void>
  onTemplateFile?: (file: File, operation: BulkImportOperation) => void | Promise<void>
  onDownloadTemplate?: (operation?: BulkImportOperation, selectedFieldCodes?: string[]) => void
  clientCode?: string
  existingEmployeeCodes?: string[]
  initialSalaryTemplateId?: string
  initialOperation?: BulkImportOperation
  salaryTemplates: Structure[]
  salaryComponents: Component[]
  salaryLoading: boolean
}

export default function SmartBulkUploadMapper(p: Props) {
  const [mode, setMode] = useState<ImportMode>('mapped')
  const [operation, setOperation] = useState<BulkImportOperation>(p.initialOperation ?? 'insert')
  const [sourceFile, setSourceFile] = useState<File | null>(null)
  const [templateFile, setTemplateFile] = useState<File | null>(null)
  const [salaryTemplateId, setSalaryTemplateId] = useState<string>()
  const [salaryEffectiveFrom, setSalaryEffectiveFrom] = useState('')
  const [sourceSheets, setSourceSheets] = useState<ImportSourceSheet[]>([])
  const [sheets, setSheets] = useState<ImportPreviewSheet[]>([])
  const [activeSheetIndex, setActiveSheetIndex] = useState(0)
  const [mappings, setMappings] = useState<BulkImportMapping>({})
  const [selectedSource, setSelectedSource] = useState<number | null>(null)
  const [sourceSearch, setSourceSearch] = useState('')
  const [targetSearch, setTargetSearch] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [sourcePreviewOpen, setSourcePreviewOpen] = useState(false)
  const [connectors, setConnectors] = useState<Connector[]>([])
  const [generateEmployeeCodes, setGenerateEmployeeCodes] = useState(false)
  const [employeeCodeDigits, setEmployeeCodeDigits] = useState(5)
  const [sheetNameSplits, setSheetNameSplits] = useState<Record<number, SheetNameSplit>>({})
  const [templateFields, setTemplateFields] = useState<string[]>(p.definition.fields.map(field => field.code))
  const canvasRef = useRef<HTMLDivElement | null>(null)
  const wasOpenRef = useRef(false)
  const templateChosen = useRef(false)
  const sourceRefs = useRef(new Map<number, HTMLDivElement>())
  const targetRefs = useRef(new Map<string, HTMLDivElement>())
  const activeSheet = sheets[activeSheetIndex]
  const activeSource = sourceSheets[activeSheetIndex]
  const headerRowOptions = useMemo(() => activeSource?.rows.map(row => ({ value: row.rowNumber, label: `Row ${row.rowNumber}` })) ?? [], [activeSource])
  const activeNameSplit = sheetNameSplits[activeSheetIndex]
  const definitionFor = useCallback((template?: Structure): BulkImportDefinition => {
    if (!template) return p.definition
    const fields = p.definition.fields.filter(field => !['SalaryJson', 'SalaryTemplate', 'SalaryTemplateId'].includes(field.code))
    return { ...p.definition, fields: [...fields, ...employeeSalaryInputFields(template, p.salaryComponents)] }
  }, [p.definition, p.salaryComponents])
  const salaryTemplate = p.salaryTemplates.find(template => String(template.id) === salaryTemplateId)
  const definition = useMemo(() => definitionFor(salaryTemplate), [definitionFor, salaryTemplate])
  const salaryFieldsMapped = !!salaryTemplate && Object.keys(mappings).some(code => code === 'AnnualCtc' || code === 'MonthlyCtc' || code.startsWith('SALARY_INPUT:'))
  const missingSalaryDate = salaryFieldsMapped && !salaryEffectiveFrom

  const reset = useCallback(() => {
    templateChosen.current = false
    setMode('mapped')
    setOperation(p.initialOperation ?? 'insert')
    setSourceFile(null)
    setTemplateFile(null)
    setSalaryTemplateId(undefined)
    setSalaryEffectiveFrom('')
    setSourceSheets([])
    setSheets([])
    setActiveSheetIndex(0)
    setMappings({})
    setSelectedSource(null)
    setSourceSearch('')
    setTargetSearch('')
    setError('')
    setBusy(false)
    setSourcePreviewOpen(false)
    setConnectors([])
    setGenerateEmployeeCodes(false)
    setEmployeeCodeDigits(inferEmployeeCodeDigits(p.clientCode ?? '', p.existingEmployeeCodes ?? []))
    setSheetNameSplits({})
    setTemplateFields(p.definition.fields.map(field => field.code))
    sourceRefs.current.clear()
    targetRefs.current.clear()
  }, [p.clientCode, p.definition.fields, p.existingEmployeeCodes, p.initialOperation])

  useEffect(() => {
    if (p.open && !wasOpenRef.current) reset()
    wasOpenRef.current = p.open
  }, [p.open, reset])

  const mappedSourceColumns = useMemo(() => new Set(Object.values(mappings)), [mappings])
  const chooseEmployeeCodeSource = activeSheet && mappings.EmployeeCode === undefined && employeeCodeSourceColumns(activeSheet.headers, definition).length > 1
  const missingRequired = useMemo(() => definition.fields.filter(field => field.required && !(field.requiredForNew && operation === 'update') && mappings[field.code] === undefined && !(field.code === 'EmployeeCode' && generateEmployeeCodes && !chooseEmployeeCodeSource && operation !== 'update')), [generateEmployeeCodes, chooseEmployeeCodeSource, mappings, operation, definition.fields])
  const sourceColumns = useMemo(() => (activeSheet?.headers ?? []).map((header, index) => ({
    index,
    header: header.trim() || `Unnamed column ${index + 1}`,
    samples: uniqueSamples(activeSheet?.rows ?? [], index)
  })), [activeSheet])
  const visibleSources = sourceColumns.filter(column => matchesSearch([column.header, ...column.samples], sourceSearch))
  const visibleTargets = definition.fields.filter(field => matchesSearch([field.label, field.header, field.group, field.type, ...(field.aliases ?? [])], targetSearch))
  const groups = Array.from(new Set(visibleTargets.map(field => field.group)))

  const calculateConnectors = useCallback(() => {
    const canvas = canvasRef.current
    if (!canvas) { setConnectors([]); return }
    const canvasRect = canvas.getBoundingClientRect()
    const next: Connector[] = []
    for (const [code, sourceIndex] of Object.entries(mappings)) {
      const source = sourceRefs.current.get(sourceIndex)
      const target = targetRefs.current.get(code)
      if (!source || !target) continue
      const sourceRect = source.getBoundingClientRect()
      const targetRect = target.getBoundingClientRect()
      const startX = sourceRect.right - canvasRect.left
      const startY = sourceRect.top + sourceRect.height / 2 - canvasRect.top
      const endX = targetRect.left - canvasRect.left
      const endY = targetRect.top + targetRect.height / 2 - canvasRect.top
      const bend = Math.max(34, (endX - startX) * 0.48)
      const fieldIndex = definition.fields.findIndex(field => field.code === code)
      next.push({ code, color: bulkImportMappingColor(fieldIndex), path: `M ${startX} ${startY} C ${startX + bend} ${startY}, ${endX - bend} ${endY}, ${endX} ${endY}` })
    }
    setConnectors(next)
  }, [mappings, definition.fields])

  useLayoutEffect(() => {
    const frame = window.requestAnimationFrame(calculateConnectors)
    const observer = new ResizeObserver(calculateConnectors)
    if (canvasRef.current) observer.observe(canvasRef.current)
    window.addEventListener('resize', calculateConnectors)
    return () => { window.cancelAnimationFrame(frame); observer.disconnect(); window.removeEventListener('resize', calculateConnectors) }
  }, [calculateConnectors, sourceSearch, targetSearch, activeSheetIndex])

  const loadSourceFile = async (file: File) => {
    setBusy(true); setError('')
    try {
      const sources = (await parseImportSourceSheets(file)).filter(sheet => sheet.rows.length > 1)
      if (!sources.length) throw new Error('No worksheet with a header row and data was found.')
      const parsed = sources.map(sheet => selectImportHeader(sheet, sheet.rows[0].rowNumber))
      setSourceFile(file)
      setSourceSheets(sources)
      setSheets(parsed)
      setSheetNameSplits({})
      setActiveSheetIndex(0)
      setMappings(autoMapBulkImportColumns(parsed[0], definition, p.existingEmployeeCodes))
      setSelectedSource(null)
    } catch (cause) {
      setSourceFile(null); setSourceSheets([]); setSheets([]); setSheetNameSplits({}); setMappings({})
      setError(cause instanceof Error ? cause.message : 'The spreadsheet could not be read.')
    } finally { setBusy(false) }
  }

  const changeSheet = (index: number) => {
    setActiveSheetIndex(index)
    const nextMappings = autoMapBulkImportColumns(sheets[index], definition, p.existingEmployeeCodes)
    setMappings(nextMappings)
    setSelectedSource(null)
    setError('')
  }

  const changeHeaderRow = (rowNumber: number) => {
    if (!activeSource || rowNumber === activeSheet.headerRow) return
    const sheet = selectImportHeader(activeSource, rowNumber)
    setSheets(current => current.map((item, index) => index === activeSheetIndex ? sheet : item))
    setSheetNameSplits(current => Object.fromEntries(Object.entries(current).filter(([index]) => Number(index) !== activeSheetIndex)))
    setMappings(autoMapBulkImportColumns(sheet, definition, p.existingEmployeeCodes))
    setSelectedSource(null)
    setError('')
  }

  const headerRowSelector = (testId: string) => <label className="smart-bulk-header-row">
    <span>Header row</span>
    <Select aria-label="Header row" data-testid={testId} showSearch optionFilterProp="label" disabled={busy}
      value={activeSheet?.headerRow} options={headerRowOptions} onChange={changeHeaderRow} />
  </label>

  const changeSalaryTemplate = (id?: string) => {
    templateChosen.current = true
    const nextTemplate = p.salaryTemplates.find(template => String(template.id) === id)
    const nextDefinition = definitionFor(nextTemplate)
    setSalaryTemplateId(id)
    setSelectedSource(null)
    setError('')
    // Ordinary identity and profile mappings remain usable when the salary basis changes.
    const keep = new Set(nextDefinition.fields.filter(field => !field.code.startsWith('SALARY_INPUT:')).map(field => field.code))
    const next = Object.fromEntries(Object.entries(mappings).filter(([code]) => keep.has(code)))
    if (nextTemplate && activeSheet) {
      const used = new Set(Object.values(next))
      const suggestions = autoMapBulkImportColumns(activeSheet, nextDefinition, p.existingEmployeeCodes)
      for (const [code, index] of Object.entries(suggestions)) {
        if (code.startsWith('SALARY_INPUT:') && !used.has(index)) next[code] = index
      }
    }
    setMappings(next)
  }

  useEffect(() => {
    if (p.open && !busy && !templateChosen.current && p.initialSalaryTemplateId &&
        p.salaryTemplates.some(template => String(template.id) === p.initialSalaryTemplateId)) {
      changeSalaryTemplate(p.initialSalaryTemplateId)
    }
  }, [p.open, busy, p.salaryTemplates, p.initialSalaryTemplateId])

  const salaryMappingControls = <div className="smart-bulk-salary-controls">
    <label><span>Salary template</span>
      <Select aria-label="Salary template for mapping" placeholder="Choose to map salary inputs" allowClear showSearch optionFilterProp="label"
        loading={p.salaryLoading} disabled={busy} value={salaryTemplate?.id.toString()} onChange={changeSalaryTemplate}
        options={p.salaryTemplates.map(template => ({ value: String(template.id), label: template.name }))}
        notFoundContent={p.salaryLoading ? 'Loading templates…' : 'No matching saved salary template'} />
    </label>
    {salaryTemplate && <>
      <label><span>Salary effective from</span><Input aria-label="Salary effective from" type="date" value={salaryEffectiveFrom}
        required={salaryFieldsMapped} onChange={event => setSalaryEffectiveFrom(event.target.value)} /></label>
      <small>{operation !== 'update' && 'Choose an effective date to give new employees this template and its defaults. '}Map recurring amounts per employee. A blank keeps the saved amount or uses the template default. Enter 0 to pay zero.</small>
    </>}
  </div>

  const mapColumn = (sourceIndex: number, targetCode: string) => {
    setMappings(current => mapBulkImportColumn(current, targetCode, sourceIndex))
    setSelectedSource(null)
    setError('')
  }

  const unmapTarget = (targetCode: string) => setMappings(current => Object.fromEntries(Object.entries(current).filter(([code]) => code !== targetCode)))
  const unmapSource = (sourceIndex: number) => setMappings(current => Object.fromEntries(Object.entries(current).filter(([, index]) => index !== sourceIndex)))
  const targetForSource = (sourceIndex: number) => definition.fields.find(field => mappings[field.code] === sourceIndex)
  const fieldByCode = (code: string) => definition.fields.find(field => field.code === code)

  const splitPreviewColumn = (sourceIndex: number, strategy: BulkImportNameSplitStrategy) => {
    if (!activeSheet || activeNameSplit) return
    try {
      const result = materializeBulkImportNameSplit(activeSheet, sourceIndex, strategy)
      setSheets(current => current.map((sheet, index) => index === activeSheetIndex ? result.sheet : sheet))
      setSheetNameSplits(current => ({ ...current, [activeSheetIndex]: toSheetNameSplit(result, activeSheet) }))
      setMappings(current => shiftMappingsForSplit(current, sourceIndex))
      setSelectedSource(null)
      setError('')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The selected column could not be split.')
    }
  }

  const changePreviewSplitStrategy = (strategy: BulkImportNameSplitStrategy) => {
    if (!activeNameSplit) return
    try {
      const result = materializeBulkImportNameSplit(activeNameSplit.originalSheet, activeNameSplit.sourceColumnIndex, strategy)
      setSheets(current => current.map((sheet, index) => index === activeSheetIndex ? result.sheet : sheet))
      setSheetNameSplits(current => ({ ...current, [activeSheetIndex]: toSheetNameSplit(result, activeNameSplit.originalSheet) }))
      setError('')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The split rule could not be changed.')
    }
  }

  const undoPreviewSplit = () => {
    if (!activeNameSplit) return
    setSheets(current => current.map((sheet, index) => index === activeSheetIndex ? activeNameSplit.originalSheet : sheet))
    setMappings(current => restoreMappingsAfterSplit(current, activeNameSplit.sourceColumnIndex))
    setSheetNameSplits(current => Object.fromEntries(Object.entries(current).filter(([index]) => Number(index) !== activeSheetIndex)))
    setSelectedSource(null)
    setError('')
  }

  const onSourceKey = (event: KeyboardEvent<HTMLDivElement>, sourceIndex: number) => {
    if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); setSelectedSource(current => current === sourceIndex ? null : sourceIndex) }
  }
  const onTargetKey = (event: KeyboardEvent<HTMLDivElement>, targetCode: string) => {
    if ((event.key === 'Enter' || event.key === ' ') && selectedSource !== null) { event.preventDefault(); mapColumn(selectedSource, targetCode) }
  }
  const onTargetDrop = (event: DragEvent<HTMLDivElement>, targetCode: string) => {
    event.preventDefault()
    const rawSourceIndex = event.dataTransfer.getData('application/x-smart-bulk-source')
    const sourceIndex = Number(rawSourceIndex)
    if (rawSourceIndex !== '' && Number.isInteger(sourceIndex)) mapColumn(sourceIndex, targetCode)
  }

  const prepare = async () => {
    if (!sourceFile || !activeSheet || missingRequired.length || missingSalaryDate) return
    setBusy(true); setError('')
    try { await p.onPrepared(prepareMappedBulkImport(sourceFile, activeSheet, definition, mappings, {
      operation,
      employeeSalaryContext: salaryTemplate ? { templateId: String(salaryTemplate.id), effectiveFrom: salaryEffectiveFrom } : undefined,
      employeeCodeGeneration: { enabled: generateEmployeeCodes, prefix: p.clientCode ?? '', digits: employeeCodeDigits, existingCodes: p.existingEmployeeCodes ?? [] },
      preTransformedRows: activeNameSplit?.splitRows,
      sourceColumnTransforms: activeNameSplit ? {
        [activeNameSplit.firstNameColumnIndex]: `Split from: ${activeNameSplit.sourceHeader} / First Name`,
        [activeNameSplit.lastNameColumnIndex]: `Split from: ${activeNameSplit.sourceHeader} / Last Name`
      } : undefined
    })) }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Mapped spreadsheet could not be prepared.') }
    finally { setBusy(false) }
  }

  const reviewTemplate = async () => {
    if (!templateFile || !p.onTemplateFile) return
    setBusy(true); setError('')
    try { await p.onTemplateFile(templateFile, operation) }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Template could not be prepared.') }
    finally { setBusy(false) }
  }
  const cancel = () => { setSourcePreviewOpen(false); p.onCancel() }

  const footer = <div className="smart-bulk-footer">
    <div><b>{mode === 'mapped' ? `${Object.keys(mappings).length} fields mapped` : templateFile?.name || 'No template selected'}</b><span>{mode === 'mapped' && activeSheet ? `${activeSheet.rows.length} data rows ready for review` : 'Nothing is imported until the final preview is confirmed.'}</span></div>
    <Space wrap>
      <Button disabled={busy} onClick={cancel}>Cancel</Button>
      {mode === 'mapped'
        ? <Button data-testid="smart-bulk-continue" type="primary" loading={busy} disabled={!sourceFile || !activeSheet?.rows.length || !Object.keys(mappings).length || missingRequired.length > 0 || missingSalaryDate} onClick={() => void prepare()}>Review mapped data</Button>
        : <Button data-testid="smart-bulk-template-continue" type="primary" loading={busy} disabled={!templateFile || !p.onTemplateFile} onClick={() => void reviewTemplate()}>Review template</Button>}
    </Space>
  </div>

  return <Drawer
    className="smart-bulk-drawer"
    width="min(1440px,98vw)"
    placement="right"
    open={p.open}
    title={<div className="smart-bulk-title"><span>SMART BULK UPLOAD</span><b>{definition.moduleLabel}</b><small>Import a standard template or map columns from any Excel/CSV file.</small></div>}
    onClose={cancel}
    maskClosable={!busy}
    closable={!busy}
    footer={footer}
  >
    <div className="smart-bulk-shell" data-testid="smart-bulk-drawer">
      <section className="smart-bulk-operation" data-testid="smart-bulk-operation">
        <div><b>What should this import do?</b><span>Choose explicitly so blank cells never erase existing employee data.</span></div>
        <Select value={operation} onChange={setOperation} options={[
          { value: 'insert', label: 'Add new employees only' },
          { value: 'update', label: 'Update existing employees only' },
          { value: 'upsert', label: 'Add new + update existing' }
        ]} />
      </section>
      {operation === 'update' && <Alert type="info" showIcon message="Bulk update existing employees" description="Map the existing Employee Code from your spreadsheet or use the HRMS update workbook. Employee ID is optional. Blank cells keep current values. Grades must exist in the Employee Grade master. Valid categories are Highly skilled, Skilled, Semi-skilled and Unskilled." />}
      <div className="smart-bulk-mode-tabs" role="tablist" aria-label="Bulk upload method">
        <button type="button" role="tab" aria-selected={mode === 'mapped'} className={mode === 'mapped' ? 'active' : ''} onClick={() => setMode('mapped')}><LinkOutlined /><span><b>Map any spreadsheet</b><small>Upload different column names and connect them to HRMS fields.</small></span><em>Recommended</em></button>
        <button type="button" role="tab" aria-selected={mode === 'template'} className={mode === 'template' ? 'active' : ''} onClick={() => setMode('template')}><CloudUploadOutlined /><span><b>Use HRMS template</b><small>Upload the standard employee template without manual mapping.</small></span></button>
      </div>

      {error && <Alert type="error" showIcon closable message="Spreadsheet needs attention" description={error} onClose={() => setError('')} />}

      {mode === 'template' ? <TemplateUploadPanel file={templateFile} busy={busy} operation={operation} definition={p.definition} selectedFields={templateFields} onSelectedFields={setTemplateFields} onFile={setTemplateFile} onDownload={p.onDownloadTemplate} /> : <>
        <SourceUploadPanel file={sourceFile} busy={busy} onFile={file => void loadSourceFile(file)} onPreview={activeSheet ? () => setSourcePreviewOpen(true) : undefined} />
        {activeSheet && <>
          {salaryMappingControls}
          <section className="smart-bulk-summary" aria-label="Mapping summary">
            <div><span>Worksheet</span><Select aria-label="Worksheet" value={activeSheetIndex} onChange={changeSheet} options={sheets.map((sheet, index) => ({ value: index, label: `${sheet.name} (${sheet.rows.length} rows)` }))} /></div>
            <div>{headerRowSelector('smart-bulk-header-row')}</div>
            <div><span>Detected columns</span><b>{sourceColumns.length}</b></div>
            <div><span>Mapped fields</span><b className="success">{Object.keys(mappings).length}</b></div>
            <div data-testid="smart-bulk-unmapped-count"><span>Skipped columns</span><b>{Math.max(0, sourceColumns.length - mappedSourceColumns.size)}</b></div>
            <div><span>Required missing</span><b className={missingRequired.length ? 'danger' : 'success'}>{missingRequired.length}</b></div>
          </section>

          <EmployeeCodeGeneration enabled={generateEmployeeCodes} digits={employeeCodeDigits} clientCode={p.clientCode ?? ''} nextCode={nextEmployeeCodePreview(p.clientCode ?? '', employeeCodeDigits, p.existingEmployeeCodes ?? [])} generationDisabled={operation === 'update'} onEnabled={setGenerateEmployeeCodes} onDigits={setEmployeeCodeDigits}>
            {operation === 'update' && <small>Code generation is intentionally disabled in Update mode.</small>}
          </EmployeeCodeGeneration>

          <section className="smart-bulk-bucket" data-testid="smart-bulk-mapping-bucket">
            <header><div><b>Mapped pairs</b><span>Matching colours show which uploaded column feeds each HRMS field.</span></div><Button size="small" icon={<ReloadOutlined />} onClick={() => setMappings(autoMapBulkImportColumns(activeSheet, definition, p.existingEmployeeCodes))}>Auto-map again</Button></header>
            <div>{Object.entries(mappings).length ? Object.entries(mappings).map(([code, sourceIndex]) => {
              const field = fieldByCode(code)
              if (!field) return null
              const color = bulkImportMappingColor(definition.fields.indexOf(field))
              return <span className="smart-bulk-pair" data-testid="smart-bulk-mapping-row" data-field-code={field.code} key={code} style={{ '--map-color': color } as CSSProperties}><i />{sourceColumns[sourceIndex]?.header || `Column ${sourceIndex + 1}`}<strong>{'->'}</strong>{field.label}<button type="button" aria-label={`Remove ${field.label} mapping`} onClick={() => unmapTarget(code)}>x</button></span>
            }) : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No fields mapped yet" />}</div>
          </section>

          {missingRequired.length > 0 && <Alert type="warning" showIcon message="Map required fields before continuing" description={missingRequired.map(field => field.code === 'EmployeeCode' && chooseEmployeeCodeSource ? 'Employee Code · Select the column that identifies this client’s employees.' : field.label).join(', ')} />}

          <section className="smart-bulk-mapper" data-testid="smart-bulk-mapper">
            <header><div><b>Column mapping workspace</b><span>Drag a source column onto a target, or click a source and then its target.</span></div><Tag color={selectedSource === null ? 'default' : 'purple'}>{selectedSource === null ? 'Select a source column' : `${sourceColumns[selectedSource]?.header} selected`}</Tag></header>
            <div className="smart-bulk-map-scroll">
              <div className="smart-bulk-map-canvas" ref={canvasRef}>
                <svg className="smart-bulk-connectors" aria-hidden="true">{connectors.map(connector => <path data-testid="smart-bulk-connector" key={connector.code} d={connector.path} stroke={connector.color} />)}</svg>
                <div className="smart-bulk-column smart-bulk-source-list" data-testid="smart-bulk-source-list">
                  <div className="smart-bulk-column-head"><div><b>Uploaded columns</b><span>{sourceColumns.length} detected</span></div><Input allowClear value={sourceSearch} onChange={event => setSourceSearch(event.target.value)} placeholder="Find source column..." /></div>
                  {visibleSources.map(column => {
                    const target = targetForSource(column.index)
                    const targetIndex = target ? definition.fields.indexOf(target) : -1
                    const color = target ? bulkImportMappingColor(targetIndex) : '#94a3b8'
                    return <div
                      ref={node => { if (node) sourceRefs.current.set(column.index, node); else sourceRefs.current.delete(column.index) }}
                      key={`${column.index}-${column.header}`}
                      className={`smart-bulk-source-card${selectedSource === column.index ? ' selected' : ''}${target ? ' mapped' : ''}`}
                      style={{ '--map-color': color } as CSSProperties}
                      draggable
                      role="button"
                      tabIndex={0}
                      data-testid="smart-bulk-source-column"
                      data-source-column={column.header}
                      data-mapped={Boolean(target)}
                      onClick={() => setSelectedSource(current => current === column.index ? null : column.index)}
                      onKeyDown={event => onSourceKey(event, column.index)}
                      onDragStart={event => { event.dataTransfer.effectAllowed = 'link'; event.dataTransfer.setData('application/x-smart-bulk-source', String(column.index)); setSelectedSource(column.index) }}
                    ><i /><div><b>{column.header}</b><span>{column.samples.length ? column.samples.join(' / ') : 'No sample values'}</span></div><em>{target ? target.label : 'Skipped if unmapped'}</em></div>
                  })}
                </div>

                <div className="smart-bulk-arrow-guide"><LinkOutlined /><span>Drag or click</span></div>

                <div className="smart-bulk-column smart-bulk-target-list" data-testid="smart-bulk-target-list">
                  <div className="smart-bulk-column-head"><div><b>HRMS target fields</b><span>{definition.fields.length} available</span></div><Input allowClear value={targetSearch} onChange={event => setTargetSearch(event.target.value)} placeholder="Find HRMS field..." /></div>
                  {groups.map(group => <section className="smart-bulk-target-group" key={group}><h4>{group}</h4>{visibleTargets.filter(field => field.group === group).map(field => {
                    const sourceIndex = mappings[field.code]
                    const mapped = sourceIndex !== undefined
                    const color = bulkImportMappingColor(definition.fields.indexOf(field))
                    return <div
                      ref={node => { if (node) targetRefs.current.set(field.code, node); else targetRefs.current.delete(field.code) }}
                      key={field.code}
                      className={`smart-bulk-target-card${mapped ? ' mapped' : ''}${selectedSource !== null ? ' ready' : ''}`}
                      style={{ '--map-color': color } as CSSProperties}
                      role="button"
                      tabIndex={0}
                      data-testid="smart-bulk-target-field"
                      data-field-code={field.code}
                      data-mapped={mapped}
                      onClick={() => { if (selectedSource !== null) mapColumn(selectedSource, field.code) }}
                      onKeyDown={event => onTargetKey(event, field.code)}
                      onDragOver={event => { event.preventDefault(); event.dataTransfer.dropEffect = 'link' }}
                      onDrop={event => onTargetDrop(event, field.code)}
                    ><i /><div><b>{field.label}{field.required && <sup>Required</sup>}</b><span>{mapped ? `From: ${sourceColumns[sourceIndex]?.header}` : field.description || `${field.type} field${field.defaultValue !== undefined ? ` / Default ${field.defaultValue}` : ''}`}</span></div>{mapped && <button type="button" aria-label={`Remove ${field.label} mapping`} onClick={event => { event.stopPropagation(); unmapTarget(field.code) }}><DeleteOutlined /></button>}</div>
                  })}</section>)}
                </div>
              </div>
            </div>
          </section>
        </>}
      </>}
      {sourceFile && activeSheet && <SmartBulkSourcePreview key={`${activeSheetIndex}-${activeSheet.headerRow}`} headerRowSelector={headerRowSelector('smart-bulk-preview-header-row')} salaryControls={salaryMappingControls} open={p.open && sourcePreviewOpen} fileName={sourceFile.name} sheet={activeSheet} definition={definition} mappings={mappings} split={activeNameSplit} onMap={mapColumn} onUnmapSource={unmapSource} onSplitColumn={splitPreviewColumn} onSplitStrategyChange={changePreviewSplitStrategy} onUndoSplit={undoPreviewSplit} onClose={() => setSourcePreviewOpen(false)} />}
    </div>
  </Drawer>
}

function SourceUploadPanel(p: { file: File | null; busy: boolean; onFile: (file: File) => void; onPreview?: () => void }) {
  return <div className="smart-bulk-source-upload-row"><FileDropZone testId="smart-bulk-file-input" title={p.file ? p.file.name : 'Drop any Excel or CSV file here'} detail={p.file ? `${formatBytes(p.file.size)} / Columns will be detected from the selected worksheet.` : 'Different column names are supported. Your original file is never changed.'} busy={p.busy} onFile={p.onFile} />{p.file && p.onPreview && <Button data-testid="smart-bulk-source-preview-open" className="smart-bulk-source-preview-open" onClick={p.onPreview}>Preview, split & map</Button>}</div>
}

function TemplateUploadPanel(p: { file: File | null; busy: boolean; operation: BulkImportOperation; definition: BulkImportDefinition; selectedFields: string[]; onSelectedFields: (fields: string[]) => void; onFile: (file: File) => void; onDownload?: (operation?: BulkImportOperation, selectedFieldCodes?: string[]) => void }) {
  const requiredCodes = p.definition.fields.filter(field => field.required && !(field.requiredForNew && p.operation === 'update')).map(field => field.code)
  const effectiveFields = Array.from(new Set([...requiredCodes, ...p.selectedFields]))
  return <section className="smart-bulk-template-panel">
    <div><span>SELECTIVE TEMPLATE</span><h3>Download only the fields you need</h3><p>{p.operation === 'update' ? 'The update workbook includes Employee ID and current values. Unselected and blank fields remain unchanged.' : 'Required identity fields are included automatically. Pick any additional columns for new employees.'}</p>
      <Select mode="multiple" allowClear value={p.selectedFields} onChange={p.onSelectedFields} maxTagCount="responsive" placeholder="Select employee fields" options={p.definition.fields.map(field => ({ value: field.code, label: `${field.group} / ${field.label}`, disabled: requiredCodes.includes(field.code) }))} />
      {p.onDownload && <Button data-testid="smart-bulk-selective-template-download" icon={<DownloadOutlined />} disabled={!effectiveFields.length} onClick={() => p.onDownload?.(p.operation, effectiveFields)}>Download selected template</Button>}
    </div>
    <FileDropZone testId="smart-bulk-template-file-input" title={p.file ? p.file.name : 'Drop completed HRMS template here'} detail={p.file ? formatBytes(p.file.size) : 'Excel (.xlsx) and CSV files are accepted.'} busy={p.busy} onFile={p.onFile} />
  </section>
}

function FileDropZone(p: { testId: string; title: string; detail: string; busy: boolean; onFile: (file: File) => void }) {
  const take = (files: FileList | null) => { const file = files?.[0]; if (file) p.onFile(file) }
  return <label className={`smart-bulk-drop-zone${p.busy ? ' busy' : ''}`} onDragOver={event => event.preventDefault()} onDrop={event => { event.preventDefault(); take(event.dataTransfer.files) }}>
    <input data-testid={p.testId} type="file" disabled={p.busy} accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" onChange={event => { take(event.target.files); event.currentTarget.value = '' }} />
    <CloudUploadOutlined /><div><b>{p.title}</b><span>{p.detail}</span></div><em>Choose file</em>
  </label>
}

function uniqueSamples(rows: string[][], index: number) {
  return Array.from(new Set(rows.map(row => (row[index] ?? '').trim()).filter(Boolean))).slice(0, 3)
}

function matchesSearch(values: string[], search: string) {
  const needle = normalizeBulkImportHeader(search)
  return !needle || values.some(value => normalizeBulkImportHeader(value).includes(needle))
}

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function shiftMappingsForSplit(current: BulkImportMapping, sourceIndex: number): BulkImportMapping {
  return Object.fromEntries(Object.entries(current).flatMap(([code, index]) => index === sourceIndex ? [] : [[code, index > sourceIndex ? index + 1 : index]]))
}

function restoreMappingsAfterSplit(current: BulkImportMapping, sourceIndex: number): BulkImportMapping {
  return Object.fromEntries(Object.entries(current).flatMap(([code, index]) => index === sourceIndex || index === sourceIndex + 1 ? [] : [[code, index > sourceIndex + 1 ? index - 1 : index]]))
}

function toSheetNameSplit(result: MaterializedBulkImportNameSplit, originalSheet: ImportPreviewSheet): SheetNameSplit {
  const { sheet: _sheet, ...split } = result
  return { ...split, originalSheet }
}
