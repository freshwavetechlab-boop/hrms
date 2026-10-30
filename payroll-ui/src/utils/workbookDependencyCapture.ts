import {parseExcelPayslipFile,excelColumnLetter,type PayslipSheet,type PayslipColumn,type PayslipSourceRow} from './excelPayslipImport'

export type CapturedWorkbook = {key:string;sha256:string;fileName:string;originalId?:string;sheets:PayslipSheet[];file?:File}
export type WorkbookMapping = {fromWorkbookKey:string;reference:string;toWorkbookKey:string;reviewed:boolean}
export type WorkbookBinding = {workbookKey:string;sha256:string;fileName:string;originalId:string}
export type DependencySheet = {workbookKey:string;sheetName:string;sourceHash:string;columnCount:number;completeSheetCapture:boolean;rows:PayslipSourceRow[]}
export type WorkbookCaptureFields = {captureVersion:number;workbookKey:string;completeSheetCapture:boolean;dependencyReviewStatus:string;workbookFiles:WorkbookBinding[];dependencySheets:DependencySheet[];externalMappings:WorkbookMapping[];lookupTraces?:{formulaCell:string;tableRange:string;matchMode:string;selectedRow:number|null;matchingRows:number;status:string}[]}
export type DependencyReference = {fromWorkbookKey:string;sheet:string;cell:string;reference:string;kind:'Cross-sheet'|'External';targetWorkbookKey?:string;targetSheet:string}
const lower=(s:string)=>s.toLocaleLowerCase('en-US')
const index=(letters:string)=>[...letters.replace(/\$/g,'').toUpperCase()].reduce((v,c)=>v*26+c.charCodeAt(0)-64,0)-1
const pos=(address:string)=>{const m=/^\$?([A-Z]{1,3})\$?([1-9]\d{0,5})$/i.exec(address);if(!m)throw Error('Unsupported dependency address; review the worksheet range.');const column=index(m[1]),row=Number(m[2]);if(column>511||row>100000)throw Error('Dependency exceeds 512 columns / 100,000 rows.');return {column,row}}
const id=(book:string,sheet:string)=>`${book}|${lower(sheet)}`
export async function captureWorkbook(file:File):Promise<CapturedWorkbook> {
  if(file.size>32*1024*1024)throw Error('Workbook exceeds the 32 MiB capture limit; use a bounded source file.')
  const bytes=await file.arrayBuffer(),sha256=[...new Uint8Array(await crypto.subtle.digest('SHA-256',bytes))].map(b=>b.toString(16).padStart(2,'0')).join('')
  const sheets=await parseExcelPayslipFile(file)
  return {key:`wb_${sha256}`,sha256,fileName:file.name,sheets,file}
}
export function restoreCapturedWorkbooks(source:Partial<WorkbookCaptureFields>&{sheetName:string;columnCount:number;rows:PayslipSourceRow[]},root:PayslipSheet):CapturedWorkbook[] {
  return (source.workbookFiles||[]).map(file=>({key:file.workbookKey,sha256:file.sha256,fileName:file.fileName,originalId:file.originalId,
    sheets:[...(file.workbookKey===source.workbookKey?[root]:[]),...(source.dependencySheets||[]).filter(s=>s.workbookKey===file.workbookKey).map(s=>({name:s.sheetName,columnCount:s.columnCount,rows:s.rows}))]}))
}
function refs(formula:string) {
  const text=formula.replace(/"(?:[^"]|"")*"/g,m=>' '.repeat(m.length))
  const pattern=/(?<![A-Z0-9_])(?:(?<sheet>'(?:[^']|'')+'|(?:\[[^\]]+\])?[A-Z_][A-Z0-9_.]*)!)?(?<range>\$?[A-Z]{1,3}\$?[1-9]\d{0,5}(?::\$?[A-Z]{1,3}\$?[1-9]\d{0,5})?|\$?[A-Z]{1,3}:\$?[A-Z]{1,3})(?![A-Z0-9_(])/gi
  return [...text.matchAll(pattern)].map(m=>({sheet:m.groups?.sheet?.replace(/^'|'$/g,'').replace(/''/g,"'"),range:m.groups!.range}))
}
// Read only supplied in-memory sheets. This collector never evaluates formulas, follows links or fetches files.
export function collectWorkbookDependencies(root:PayslipSheet,rootKey:string,books:CapturedWorkbook[],mappings:WorkbookMapping[],columns:PayslipColumn[],headerRow:number,excludedRows:number[]=[]) {
  const issues:string[]=[],references:DependencyReference[]=[],captured=new Map<string,DependencySheet>(),visited=new Set<string>(),ranges=new Set<string>()
  const catalog=new Map<string,{book:CapturedWorkbook;sheet:PayslipSheet}>(),bookKeys=new Set<string>()
  if(books.length>20||mappings.length>20)throw Error('Use at most 20 supplied workbook mappings.')
  for(const book of books){if(!/^[a-f0-9]{64}$/.test(book.sha256)||book.key!==`wb_${book.sha256}`)throw Error('Original workbook fingerprint is unavailable; choose the original file.');if(bookKeys.has(book.key))throw Error('Duplicate workbook content; merge its supplied sheet metadata instead of guessing a version.');bookKeys.add(book.key)
    for(const sheet of book.sheets){const key=id(book.key,sheet.name);if(catalog.has(key))throw Error('Ambiguous worksheet names in a supplied workbook.');catalog.set(key,{book,sheet})}}
  const rootBook=books.find(b=>b.key===rootKey)
  if(!rootBook||!catalog.has(id(rootKey,root.name)))return {issues:['Select/re-supply the exact original calculation workbook and worksheet. No dependency provenance was invented.'],references,reviewKey:'unbound',needsReview:true,fields:null}
  catalog.set(id(rootKey,root.name),{book:rootBook,sheet:root})
  let work=0
  const walk=(book:CapturedWorkbook,sheet:PayslipSheet,address:string)=>{
    if(++work>250000)throw Error('Dependency collection exceeds its work limit. Review a smaller worksheet/range.')
    const p=pos(address),node=`${id(book.key,sheet.name)}!${excelColumnLetter(p.column)}${p.row}`
    if(visited.has(node))return;visited.add(node)
    const cell=sheet.rows.find(r=>r.sourceRow===p.row)?.cells[p.column];if(!cell?.formulaText)return
    for(const ref of refs(cell.formulaText)){
      let targetKey=book.key,targetSheet=ref.sheet||sheet.name,external=''
      if(targetSheet.startsWith('[')){const end=targetSheet.indexOf(']');external=targetSheet.slice(0,end+1);targetSheet=targetSheet.slice(end+1)
        const aliases=mappings.filter(m=>m.fromWorkbookKey===book.key&&m.reference===external)
        if(aliases.length!==1||!aliases[0].reviewed){references.push({fromWorkbookKey:book.key,sheet:sheet.name,cell:address,reference:external,kind:'External',targetSheet});issues.push(`${sheet.name}!${address}: ${external} needs one reviewed supplied-workbook mapping.`);continue}
        targetKey=aliases[0].toWorkbookKey
      }
      const target=catalog.get(id(targetKey,targetSheet))
      if(external||targetKey!==rootKey||lower(targetSheet)!==lower(root.name))references.push({fromWorkbookKey:book.key,sheet:sheet.name,cell:address,reference:external||targetSheet,kind:external?'External':'Cross-sheet',targetWorkbookKey:targetKey,targetSheet})
      if(!target){issues.push(`${sheet.name}!${address}: referenced worksheet is not supplied. Load/map its workbook; no cached-value substitute is used.`);continue}
      if(id(targetKey,targetSheet)!==id(rootKey,root.name))captured.set(id(targetKey,targetSheet),{workbookKey:targetKey,sheetName:target.sheet.name,sourceHash:target.book.sha256,columnCount:target.sheet.columnCount,completeSheetCapture:true,rows:target.sheet.rows})
      const [first,last=first]=ref.range.split(':'),whole=/^\$?[A-Z]{1,3}$/i.test(first)
      const start=whole?{column:index(first),row:1}:pos(first),end=whole?{column:index(last),row:Math.max(1,...target.sheet.rows.map(r=>r.sourceRow))}:pos(last)
      if(end.column<start.column||end.row<start.row||(end.column-start.column+1)*(end.row-start.row+1)>10000)throw Error('A dependency range exceeds 10,000 cells or is reversed; review a bounded range.')
      const rangeKey=`${id(targetKey,targetSheet)}!${ref.range}`;if(ranges.has(rangeKey))continue;ranges.add(rangeKey)
      for(const row of target.sheet.rows.filter(r=>r.sourceRow>=start.row&&r.sourceRow<=end.row))for(let c=start.column;c<=end.column;c++)if(row.cells[c]?.formulaText)walk(target.book,target.sheet,`${excelColumnLetter(c)}${row.sourceRow}`)
    }
  }
  const money=new Set(columns.filter(c=>['earning','deduction','employer','netPay','grossTotal','deductionTotal'].includes(c.kind)).map(c=>c.columnIndex))
  for(const row of root.rows.filter(r=>r.sourceRow>headerRow&&!excludedRows.includes(r.sourceRow)))for(const c of money)if(row.cells[c]?.formulaText)walk(rootBook,root,`${excelColumnLetter(c)}${row.sourceRow}`)
  const sheets=[...captured.values()]
  if(sheets.length>20||root.rows.reduce((n,r)=>n+r.cells.length,0)+sheets.reduce((n,s)=>n+s.rows.reduce((m,r)=>m+r.cells.length,0),0)>250000)throw Error('Captured dependencies exceed 20 sheets / 250,000 cells; use a bounded source scope.')
  const used=new Set([rootKey,...sheets.map(s=>s.workbookKey)]),workbookFiles=books.filter(b=>used.has(b.key)).map(b=>({workbookKey:b.key,fileName:b.fileName,sha256:b.sha256,originalId:b.originalId||''}))
  const externalMappings=mappings.filter(m=>used.has(m.fromWorkbookKey)&&used.has(m.toWorkbookKey)&&references.some(r=>r.fromWorkbookKey===m.fromWorkbookKey&&r.reference===m.reference)).map(m=>({...m}))
  // UI invalidation key only; authoritative freshness is checked by the server's full source/configuration plan hash.
  const data=JSON.stringify({root:root.name,formulaGraph:[...visited].sort(),formulas:[{book:rootKey,sheet:root},...sheets.map(s=>({book:s.workbookKey,sheet:{name:s.sheetName,rows:s.rows}}))].map(s=>({book:s.book,sheet:s.sheet.name,formulas:s.sheet.rows.flatMap(r=>r.cells.flatMap((c,i)=>c.formulaText?[r.sourceRow,i,c.formulaText,c.dataType]:[]))})),columns,headerRow,excludedRows,mappings,files:workbookFiles.map(f=>({key:f.workbookKey,hash:f.sha256})),sheets:sheets.map(s=>({key:s.workbookKey,name:s.sheetName,hash:s.sourceHash}))});let hash=2166136261;for(let i=0;i<data.length;i++)hash=Math.imul(hash^data.charCodeAt(i),16777619)
  const fields:WorkbookCaptureFields={captureVersion:1,workbookKey:rootKey,completeSheetCapture:true,dependencyReviewStatus:'Pending',workbookFiles,dependencySheets:sheets,externalMappings,lookupTraces:[]}
  return {issues:[...new Set(issues)],references,reviewKey:`ui-${hash>>>0}`,needsReview:references.length>0,fields}
}

// Same-file sheets are selected together with their original workbook. A complete,
// issue-free capture needs no second acknowledgement; external aliases still do.
export function hasCompleteInternalWorkbookSources(report:ReturnType<typeof collectWorkbookDependencies>) {
  const fields=report.fields
  if(!fields||report.issues.length||!fields.completeSheetCapture||fields.workbookFiles.length!==1||fields.externalMappings.length)return false
  const root=fields.workbookFiles[0]
  if(root.workbookKey!==fields.workbookKey||root.workbookKey!==`wb_${root.sha256}`||!/^[a-f0-9]{64}$/.test(root.sha256))return false
  return report.references.every(ref=>ref.kind==='Cross-sheet'&&ref.fromWorkbookKey===root.workbookKey&&ref.targetWorkbookKey===root.workbookKey)
    &&fields.dependencySheets.every(sheet=>sheet.workbookKey===root.workbookKey&&sheet.sourceHash===root.sha256&&sheet.completeSheetCapture)
}

export function workbookSourcesConfirmed(report:ReturnType<typeof collectWorkbookDependencies>,reviewedKey:string) {
  return !!report.fields&&!report.issues.length&&(!report.needsReview||hasCompleteInternalWorkbookSources(report)||reviewedKey===report.reviewKey)
}
