import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import type { PayrollSetupContext, PayrollSetupPreview, PayrollSetupRequest } from '../services/excelPayrollSetupService'
import { excelColumnLetter } from './excelPayslipImport'

export type SetupDiagnosis = {
  key: string; category: 'translation' | 'source' | 'identity' | 'location' | 'policy' | 'rounding' | 'salary' | 'api' | 'master' | 'unknown'
  title: string; explanation: string; tab: 'salary' | 'employees' | 'location' | 'policies' | 'review'; actionLabel: string
  sourceRows: number[]; columns: string[]; details: string[]; blocking: boolean
}
type Heading = Pick<SetupDiagnosis, 'key' | 'category' | 'title' | 'explanation' | 'tab' | 'actionLabel'>
const headings = {
  translation: {key:'formula-translation',category:'translation',title:'Automatic formula support needs review',explanation:'Some captured Excel formulas have not become supported payroll rules yet. This is a translation or dependency-mapping issue; it does not by itself mean the Excel formula or salary is wrong. Related missing-component messages are kept with this cause.',tab:'salary',actionLabel:'Review formula mapping'},
  source: {key:'source-evidence',category:'source',title:'Saved source evidence needs review',explanation:'A required saved cell, calculated result or linked workbook is missing, erroneous or not verified. Inspect the original captured evidence; a cached value or zero is not substituted.',tab:'salary',actionLabel:'Review source evidence'},
  coverage: {key:'source-total-coverage',category:'source',title:'Source total coverage needs review',explanation:'The saved total comparison cannot yet confirm which mapped component columns feed the source total. Review the deduction/earning columns and their total controls. This is missing comparison proof, not proof that a salary formula input or the Excel salary is wrong. Salary amounts remain unchanged.',tab:'salary',actionLabel:'Review total column mapping'},
  identity: {key:'employee-identity',category:'identity',title:'Employee identity needs review',explanation:'Some selected source employees are not uniquely resolved. Review their identifiers or matching decisions separately; sharing a contact value is not proof that two names are the same person.',tab:'employees',actionLabel:'Review employee matches'},
  location: {key:'work-location',category:'location',title:'Work location needs a choice',explanation:'Choose an actual client location or review creation of the named source location. No location is invented from salary amounts.',tab:'location',actionLabel:'Choose work location'},
  state: {key:'location-state',category:'location',title:'The new location needs its state',explanation:'The state for a new work location affects payroll policy. Supply the real state; it cannot be inferred from salary amounts.',tab:'location',actionLabel:'Review location state'},
  days: {key:'attendance-day-inputs',category:'policy',title:'Salary day mapping needs review',explanation:'Review the salary divisor and the meanings of payable and actual present days. Payable salary units are not automatically daily presence or leave credits.',tab:'location',actionLabel:'Review day mapping'},
  policy: {key:'attendance-policy',category:'policy',title:'A policy choice needs review',explanation:'A work-week, attendance membership, effective policy or statutory setting needs attention. Existing rules are retained until a reviewed choice is saved.',tab:'policies',actionLabel:'Review policy settings'},
  master: {key:'master-configuration',category:'master',title:'Existing Master configuration needs review',explanation:'An explicitly selected Master or template is unavailable, differs from the proposed setup or needs an applicability decision. A component that is simply new is not an error.',tab:'salary',actionLabel:'Review Master mapping'},
  rounding: {key:'currency-evidence',category:'rounding',title:'Currency or precision evidence needs review',explanation:'The comparison needs the actual source and payroll rounding stages. No amount is silently rounded, accepted or changed by this diagnosis.',tab:'review',actionLabel:'Review precision details'},
  salary: {key:'salary-comparison',category:'salary',title:'Calculated amounts differ from Excel',explanation:'Compare the mapped inputs, component formulas and source totals before saving. The original Excel amounts remain unchanged; a mismatch is not automatically treated as harmless rounding.',tab:'review',actionLabel:'Review salary comparison'},
  api: {key:'api-compatibility',category:'api',title:'The API or schema needs updating',explanation:'This review requires an available compatible API capability or database schema. Refresh or update the service before retrying; this is not an Excel salary correction.',tab:'review',actionLabel:'Review API compatibility'},
} satisfies Record<string, Heading>
const invalidCell = (cell: PayrollSetupContext['source']['rows'][number]['cells'][number] | undefined) => !cell || !!cell.error || !!cell.calculationError || !!cell.missingCachedValue
const normalized = (value: string) => value.trim().toLocaleLowerCase().replace(/\s+/g,' ')
const placeholder = /\bREVIEW_REQUIRED\b/i
const dependencyIssue = /REVIEW_REQUIRED|unknown\s+(?:component|variable|identifier|token)|(?:component|variable|identifier|dependency|input).*(?:not found|not defined|undefined|missing|unresolved)|(?:missing|unresolved).*(?:component|dependency|monthly|full.month salary input)|positive full.month salary input|closing function|cannot (?:parse|translate)/i
const sourceIssue = /#(?:VALUE!|REF!|DIV\/0!|N\/A|NAME\?|NUM!)|missing cached|captured calculation error|no authoritative source|source is reference only|(?:external|linked|original|supplied).{0,40}(?:workbook|dependency)|(?:source|captured).*(?:cell|output|data).*(?:missing|invalid|erroneous|not found)|source fingerprint.*(?:missing|stale)/i
const totalCoverageIssue = /(?:source|saved).{0,30}(?:deduction|gross|earning|net|total).{0,24}coverage|\braw[- ]component aggregate stage\b|\b(?:total|aggregate).{0,30}(?:coverage|stage proof)\b/i

/** Read-only explanations for any client/layout. Source and server validation remain authoritative. */
export function diagnoseSetup(batch: ExcelPayslipBatch, context: PayrollSetupContext, request: PayrollSetupRequest, preview?: PayrollSetupPreview | null): SetupDiagnosis[] {
  const selected = batch.rows.filter(row => request.rowIds.includes(row.id))
  const sourceRows = new Map(selected.map(row => [row.id,row.sourceRow]))
  const groups = new Map<string,SetupDiagnosis>()
  const column = (index: number | undefined) => index == null || index < 0 ? '' : `${excelColumnLetter(index)} · ${context.source.columns.find(item=>item.columnIndex===index)?.sourceHeader || 'Source column'}`
  const add = (heading: Heading, detail: string, rows: number[] = [], columns: string[] = [], blocking = true) => {
    const item = groups.get(heading.key) || {...heading,sourceRows:[],columns:[],details:[],blocking:false}
    item.blocking ||= blocking
    item.sourceRows = [...new Set([...item.sourceRows,...rows.filter(row=>Number.isInteger(row)&&row>0)])].sort((a,b)=>a-b)
    item.columns = [...new Set([...item.columns,...columns.filter(Boolean)])]
    if(detail && !item.details.includes(detail)) item.details.push(detail)
    groups.set(item.key,item)
  }
  const unsupported = new Set<string>()
  for(const component of request.components) for(const row of selected) {
    const rule = component.rowFormulas?.[row.id] ?? component.formula
    if(!placeholder.test(rule || '')) continue
    unsupported.add(component.code.toUpperCase())
    const output = component.expectedColumnIndex ?? component.sourceColumnIndex
    const captured = context.source.rows.find(item=>item.sourceRow===row.sourceRow)?.cells[output ?? -1]
    add(headings.translation,`${component.code} · source row ${row.sourceRow}: payroll rule ${rule}${captured?.formulaText ? `; captured Excel formula ${captured.formulaText}` : ''}`,[row.sourceRow],[column(output),column(component.sourceColumnIndex)])
  }
  // Follow only dependencies on a known unsupported component. An unrelated
  // unknown identifier must remain a separate diagnostic, never be hidden.
  const dependent = new Set(unsupported)
  const codes=new Set(request.components.map(component=>component.code.toUpperCase())),downstream=new Map<string,Set<string>>()
  for(const component of request.components)for(const token of new Set([component.formula,...Object.values(component.rowFormulas || {})].join(' ').toUpperCase().match(/\b[A-Z][A-Z0-9_]*\b/g) || [])) {
    for(const code of new Set([token,token.replace(/_(?:MONTHLY|EARNED)$/,'')]))if(codes.has(code)) {
      const uses=downstream.get(code) || new Set<string>();uses.add(component.code.toUpperCase());downstream.set(code,uses)
    }
  }
  const queue=[...unsupported]
  for(let index=0;index<queue.length;index++)for(const code of downstream.get(queue[index]) || [])if(!dependent.has(code)){dependent.add(code);queue.push(code)}
  const relevantColumns = new Set<number>([request.payrollDaysColumnIndex,request.payableDaysColumnIndex,request.presentDaysColumnIndex,request.monthlyCtcColumnIndex,...request.components.flatMap(component=>[component.sourceColumnIndex,component.expectedColumnIndex])].filter((value):value is number=>value != null && value>=0))
  for(const row of selected) {
    const captured = context.source.rows.filter(item=>item.sourceRow===row.sourceRow)
    if(captured.length!==1){add(headings.source,`Source row ${row.sourceRow}: ${captured.length ? 'several captured rows share this row number' : 'authoritative source cells are missing'}`,[row.sourceRow]);continue}
    for(const index of relevantColumns) {
      const cell=captured[0].cells[index]
      if(!invalidCell(cell))continue
      add(headings.source,`${excelColumnLetter(index)}${row.sourceRow}: ${!cell?'captured cell is missing':cell.error?String(cell.error):cell.calculationError?String(cell.calculationError):'formula result was not captured'}`,[row.sourceRow],[column(index)])
    }
    const decision=request.employeeDecisions.find(item=>item.rowId===row.id)
    const employee=context.employees.find(item=>item.id===decision?.matchedEmployeeId)
    const roster=context.roster?.rows.find(item=>item.rowId===row.id)
    if(!preview && (roster?.status==='Review' || !decision?.matchedEmployeeId && !decision?.allowCreate))add(headings.identity,`Source row ${row.sourceRow}: ${roster?.message || 'Review the existing match or the proposed new employee.'}`,[row.sourceRow])
    if(employee)continue // Existing organisation data is not relocated by setup.
    const locationCell=request.sourceWorkLocationColumnIndex == null ? undefined : captured[0].cells[request.sourceWorkLocationColumnIndex]
    const sourceName=locationCell && !invalidCell(locationCell) ? locationCell.value.trim() : ''
    const defaultLocation=context.workLocations.find(item=>item.id===request.defaultWorkLocationId)
    const name=sourceName || defaultLocation?.name || (request.defaultWorkLocationName && context.workLocations.some(item=>normalized(item.name)===normalized(request.defaultWorkLocationName || '')) ? request.defaultWorkLocationName : '')
    if(!name && (!locationCell || !invalidCell(locationCell)))add(headings.location,`Source row ${row.sourceRow}: no resolved source location or active default location.`,[row.sourceRow],[column(request.sourceWorkLocationColumnIndex)])
    if(sourceName && !context.workLocations.some(item=>normalized(item.name)===normalized(sourceName))) {
      if(!request.createMissingWorkLocations)add(headings.location,`Source row ${row.sourceRow}: source location ${sourceName} has no active client match.`,[row.sourceRow],[column(request.sourceWorkLocationColumnIndex)])
      else {
        const stateColumn=context.source.columns.find(item=>/^(?:STATE|WORKSTATE|LOCATIONSTATE)$/i.test(item.sourceHeader.replace(/[^a-z]/gi,'')))
        const state=stateColumn ? captured[0].cells[stateColumn.columnIndex] : undefined
        if(!request.defaultWorkLocationState?.trim() && !(state && !invalidCell(state) && state.value.trim()))add(headings.state,`Source row ${row.sourceRow}: state is missing for the new location ${sourceName}.`,[row.sourceRow],[column(stateColumn?.columnIndex)])
      }
    }
  }
  if(!request.workWeek.trim())add(headings.policy,'No work-week policy has been selected.')
  if(request.payrollDaysColumnIndex<0 && !(request.fixedPayrollDays && request.fixedPayrollDays>=1&&request.fixedPayrollDays<=31))add(headings.days,'Select the salary divisor source column or a reviewed fixed divisor.')
  if(request.payableDaysColumnIndex<0)add(headings.days,'Select the source payable-days column.')
  if(!request.templateScope?.divisorMeaning)add(headings.days,'Choose whether the source divisor means calendar days, fixed contract days or working days.')
  if(request.importAttendance && request.presentDaysColumnIndex==null && !request.importPayableOnlyAttendance)add(headings.days,'Source attendance import has no actual-present mapping or explicit payable-only review. Payable units are not actual presence.')
  if(context.migrationRequired || preview?.migrationRequired)add(headings.api,'The current payroll setup schema migration is required.')
  if(request.recurringBlankInputReview?.version===2 && context.rowScopedRecurringBlankInputReviewSupported!==true)add(headings.api,'The current API does not advertise row-scoped recurring blank-input review version 2.')
  for(const component of request.components)if(component.existingComponentId && !context.components.some(master=>master.id===component.existingComponentId))add(headings.master,`${component.code}: the explicitly selected component Master is not present in the current context.`,[],[column(component.expectedColumnIndex ?? component.sourceColumnIndex)])
  for(const issue of preview?.issues || []) {
    const message=issue.message,code=issue.code?.toUpperCase() || ''
    const rows=issue.rowId && sourceRows.has(issue.rowId) ? [sourceRows.get(issue.rowId)!] : []
    const component=request.components.find(item=>item.code.toUpperCase()===code)
    let columns=component ? [column(component.expectedColumnIndex),column(component.sourceColumnIndex)] : []
    const detail=`${rows.length?`Source row ${rows[0]} · `:''}${issue.code?`${issue.code}: `:''}${message}`
    const blocking=/^(?:error|blocked|blocking)$/i.test(issue.severity)
    let heading: Heading
    if(/schema|migration|updated API|restart.{0,20}API|API.{0,30}(?:version|capability|compatib)|rollout|reader.{0,20}version/i.test(message))heading=headings.api
    else if(totalCoverageIssue.test(message)) {
      heading=headings.coverage
      columns=[...columns,...context.source.columns.filter(item=>['deductionTotal','grossTotal','netPay'].includes(request.sourceColumnRoles?.[item.columnIndex] || item.kind)).map(item=>column(item.columnIndex)),...request.components.filter(item=>/deduction/i.test(item.category)).map(item=>column(item.expectedColumnIndex))]
    }
    else if(sourceIssue.test(message))heading=headings.source
    else if(unsupported.size && dependencyIssue.test(message) && (dependent.has(code)||/REVIEW_REQUIRED|positive full.month salary input/i.test(message)||[...unsupported].some(token=>message.toUpperCase().includes(token))))heading=headings.translation
    else if(/unsupported|not supported|closing function|cannot (?:parse|translate)|formula translation|REVIEW_REQUIRED/i.test(message))heading=headings.translation
    else if(dependencyIssue.test(message) && !/positive full.month salary input/i.test(message))heading={...headings.translation,key:`formula-dependency:${message}`,title:'A payroll formula dependency is unresolved'}
    else if(/identity|identifier|employee.{0,30}(?:match|not belong|no longer belongs|same name|already exists|not employed)|same employee|several employee|work email|bank account|government|unmatched employee|name.{0,30}(?:differs|match)/i.test(message))heading=headings.identity
    else if(/state.{0,40}(?:location|missing)|location.{0,40}state/i.test(message))heading=headings.state
    else if(/work location|source location|location.{0,30}(?:active|client|scope|review)/i.test(message))heading=headings.location
    else if(/net pay.*differ|source comparison differs|(?:amount|salary|gross|deduction|net).*(?:mismatch|does not match|differ from)|(?:mismatch|difference).*(?:amount|salary|gross|deduction|net)/i.test(message))heading=headings.salary
    else if(/rounding|precision|decimal|currency stage/i.test(message))heading=headings.rounding
    else if(/work.week|weekly.off|leave|attendance polic|attendance.group|attendance.*membership|policy.{0,40}(?:scope|effective|review|assumption)|statutory|exemption|tax regime/i.test(message))heading=headings.policy
    else if(/present|payable|denominator|payroll days|day meaning|attendance mapping/i.test(message))heading=headings.days
    else if(/component master|global component|shared master|template.{0,70}(?:reuse|create|duplicate|applicability|scope|already|unavailable)|salary assignment|full.month salary input|source financial output.*mapping/i.test(message))heading=headings.master
    else heading={key:`unknown:${message}`,category:'unknown',title:'Another setup check needs review',explanation:'The API returned the exact check below. It has not been reclassified or hidden; review its details before continuing.',tab:'review',actionLabel:'Review calculation details'}
    add(heading,detail,rows,columns,blocking)
  }
  return [...groups.values()]
}
