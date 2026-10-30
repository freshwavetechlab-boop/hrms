import { Alert, Checkbox, Select, Space, Tag } from 'antd'
import { hasCompleteInternalWorkbookSources, workbookSourcesConfirmed, type CapturedWorkbook, type WorkbookMapping, type collectWorkbookDependencies } from '../utils/workbookDependencyCapture'

type Report = ReturnType<typeof collectWorkbookDependencies>
type Props = {
  books: CapturedWorkbook[]; mappings: WorkbookMapping[]; report: Report; disabled: boolean
  reviewedKey: string; referenceOnly: boolean
  onFiles: (files: File[]) => void; onMappings: (mappings: WorkbookMapping[]) => void
  onReview: (key: string) => void; onReferenceOnly: (value: boolean) => void
}
export default function ExcelWorkbookDependencies(props: Props) {
  const aliases = [...new Map(props.report.references.filter(r => r.kind === 'External').map(r => [`${r.fromWorkbookKey}|${r.reference}`, r])).values()]
  const hasIssues = props.report.issues.length > 0
  const internalSourcesIncluded = hasCompleteInternalWorkbookSources(props.report)
  const reviewed = workbookSourcesConfirmed(props.report, props.reviewedKey)
  const linkedSheets = [...new Set(props.report.references.map(r => r.targetSheet))]
  const internalOnly = props.books.length === 1 && !aliases.length && !hasIssues
  const links = [...new Map(props.report.references.map(r => [`${r.fromWorkbookKey}|${r.sheet}|${r.targetWorkbookKey || r.reference}|${r.targetSheet}`, r])).values()]
  return <section className="excel-workbook-dependencies" aria-label="Excel formula sources">
    <Alert type={hasIssues || !props.books.length ? 'warning' : 'info'} showIcon message={hasIssues ? 'Some formula inputs need attention' : props.report.needsReview ? 'Linked sheets found in your Excel' : 'Excel source read'} description={internalOnly
      ? `${props.books[0].sheets.length} worksheets are available in this file.${linkedSheets.length ? ` Formulas use ${linkedSheets.join(', ')}. These sheets are already included; no extra Excel file is needed.` : ' No other workbook is required by the mapped formulas.'}`
      : aliases.length ? 'Some formulas refer to a separate Excel file. Add that file and confirm which source it belongs to below.' : !props.books.length ? 'The original file is not attached to this saved source. Upload its original workbook to review formula inputs.' : 'Review the missing or unresolved formula inputs below.'} />
    {props.report.needsReview && internalSourcesIncluded && <Tag color="green">Linked sheets included automatically</Tag>}
    {props.report.needsReview && !internalSourcesIncluded && !hasIssues && <Checkbox disabled={props.disabled} checked={reviewed} onChange={event => props.onReview(event.target.checked ? props.report.reviewKey : '')}>Use these reviewed Excel files for formula inputs</Checkbox>}
    <details className="excel-workbook-source-details" open={hasIssues || undefined}>
      <summary>Formula source details (advanced) <Tag>{props.books.length} {props.books.length === 1 ? 'file' : 'files'}</Tag>{props.report.needsReview && <Tag color={reviewed && !hasIssues ? 'green' : 'orange'}>{internalSourcesIncluded ? 'Included' : reviewed && !hasIssues ? 'Reviewed' : 'Review needed'}</Tag>}</summary>
      <Space direction="vertical" style={{ width: '100%', paddingBlock: 12 }}>
        <p className="excel-payslip-muted">Original formulas and file versions are retained. This checks where inputs come from; payroll calculation is verified separately.</p>
        <label>{aliases.length ? 'Add the separate Excel file used by these formulas' : 'Add another source workbook (only if a formula needs it)'}<input aria-label="Supply dependency workbook" type="file" accept=".xlsx,.csv" multiple disabled={props.disabled || !props.books.length} onChange={event => { props.onFiles(Array.from(event.target.files || [])); event.target.value = '' }} /></label>
        {props.books.map(book => <div key={book.key}><strong>{book.fileName}</strong> <Tag>SHA-256 {book.sha256.slice(0, 12)}</Tag> {book.sheets.length} worksheets</div>)}
        {aliases.map(alias => {
          const mapping = props.mappings.find(m => m.fromWorkbookKey === alias.fromWorkbookKey && m.reference === alias.reference)
          const patch = (target: string, reviewed: boolean) => props.onMappings([...props.mappings.filter(m => m.fromWorkbookKey !== alias.fromWorkbookKey || m.reference !== alias.reference), { fromWorkbookKey: alias.fromWorkbookKey, reference: alias.reference, toWorkbookKey: target, reviewed }])
          return <div key={`${alias.fromWorkbookKey}|${alias.reference}`}>
            <p>{props.books.find(b => b.key === alias.fromWorkbookKey)?.fileName} → {alias.reference} / {alias.targetSheet}</p>
            <Select aria-label={`Map ${alias.reference} from ${alias.fromWorkbookKey}`} style={{ width: '100%' }} disabled={props.disabled} placeholder="Choose exact supplied workbook version" value={mapping?.toWorkbookKey || undefined} options={props.books.map(b => ({ value: b.key, label: `${b.fileName} · ${b.sha256.slice(0, 12)}` }))} onChange={value => patch(value, false)} />
            <Checkbox disabled={props.disabled || !mapping?.toWorkbookKey} checked={mapping?.reviewed === true} onChange={event => patch(mapping!.toWorkbookKey, event.target.checked)}>I reviewed this external alias and workbook version</Checkbox>
          </div>
        })}
        {!!links.length && <div aria-label="Linked worksheet summary">{links.map((r, i) => <div key={i}>{r.sheet} → {r.targetSheet}{r.kind === 'External' ? ` (${r.reference})` : ' (same Excel file)'}</div>)}</div>}
        {!!props.report.references.length && <details><summary>Show formula cells ({props.report.references.length})</summary><div style={{ maxHeight: 200, overflow: 'auto' }}>{props.report.references.map((r, i) => <div key={i}>{r.sheet}!{r.cell} → {r.reference} / {r.targetSheet}</div>)}</div></details>}
        {props.report.issues.map(issue => <Alert key={issue} type="warning" message={issue} />)}
        {(hasIssues || props.report.needsReview && !reviewed) && <Checkbox disabled={props.disabled} checked={props.referenceOnly} onChange={event => props.onReferenceOnly(event.target.checked)}>Save payslip amounts only; keep payroll setup blocked until formula inputs are resolved</Checkbox>}
      </Space>
    </details>
  </section>
}
