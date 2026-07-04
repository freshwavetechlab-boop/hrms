import { useMemo, useState } from 'react'
import { Modal } from 'antd'
import { apiCatalog, type ApiCatalogRow } from '../data/apiCatalog'
import DataTable from './DataTable'
import SearchSelect from './SearchSelect'

export default function ApiCatalog() {
  const modules = useMemo(() => Array.from(new Set(apiCatalog.map(row => row.module))).sort(), [])
  const [module, setModule] = useState('')
  const [candidate, setCandidate] = useState('')
  const [selected, setSelected] = useState<ApiCatalogRow | null>(null)
  const rows = apiCatalog.filter(row => (!module || row.module === module) && (!candidate || row.workflowCandidate === candidate))
  const docs = selected ? describeApi(selected) : null

  return <section className="card workflow-admin">
    <header><div><h3>API Catalog</h3><p>Use these request paths when deciding which screen action should start a workflow.</p></div></header>
    <section className="approval-stages">
      <div className="approval-stages-heading"><div><h3>Payroll approval example</h3><p>Use this rule when payroll lock should go to approval.</p></div><span>Recommended</span></div>
      <div className="request-details">
        <div><span>Screen action</span><b>Submit payroll for approval</b></div>
        <div><span>Request type</span><b>POST</b></div>
        <div><span>Request path</span><b>/api/pay-runs/{'{id}'}/submit</b></div>
        <div><span>Record type</span><b>PayRun</b></div>
        <div><span>Record number comes from</span><b>URL value</b></div>
        <div><span>Record number field</span><b>id</b></div>
        <div><span>Find client using</span><b>Lookup from table</b></div>
        <div><span>Table name</span><b>payruns</b></div>
        <div><span>Match this column</span><b>Id</b></div>
        <div><span>Client column</span><b>ClientId</b></div>
      </div>
    </section>
    <section className="approval-stages">
      <div className="approval-stages-heading"><div><h3>Available APIs</h3><p>Filter by module or workflow suitability, then copy the request type and path into workflow setup.</p></div><span>{rows.length} APIs</span></div>
      <div className="grid">
        <label><span>Module</span><SearchSelect value={module} onChange={setModule} options={[{ value: '', label: 'All modules' }, ...modules.map(item => ({ value: item, label: item }))]} /></label>
        <label><span>Workflow suitability</span><SearchSelect value={candidate} onChange={setCandidate} options={[{ value: '', label: 'All APIs' }, { value: 'Yes', label: 'Recommended for workflow' }, { value: 'Possible', label: 'Possible workflow candidate' }, { value: 'No', label: 'Usually not workflow' }]} /></label>
      </div>
      <DataTable rows={rows} emptyText="No API routes found." exportFileName="api-catalog" pageSizeOptions={[25, 50, 100]} columns={[
        { key: 'module', label: 'Module', width: '150px' },
        { key: 'method', label: 'Type', width: '90px' },
        { key: 'path', label: 'Request path', width: '310px' },
        { key: 'purpose', label: 'Purpose', width: '300px' },
        { key: 'workflowCandidate', label: 'Workflow', width: '130px' },
        { key: 'notes', label: 'Notes', width: '260px' }
      ]} actions={row => <button type="button" className="secondary" onClick={() => setSelected(row)}>Docs</button>} />
    </section>
    <Modal title={selected ? `${selected.method} ${selected.path}` : 'API documentation'} open={Boolean(selected)} onCancel={() => setSelected(null)} footer={null} width={920}>
      {selected && docs && <div className="workflow-trail">
        <header><div><h3>{selected.purpose}</h3><p>{selected.module} / Workflow: {selected.workflowCandidate}</p></div></header>
        <h4>How to use</h4>
        <div className="request-details">
          <div><span>Method</span><b>{selected.method}</b></div>
          <div><span>Path</span><b>{selected.path}</b></div>
          <div><span>Auth</span><b>Logged-in user session or bearer token</b></div>
          <div><span>Content type</span><b>{docs.contentType}</b></div>
        </div>
        <h4>Parameters</h4>
        <DataTable rows={docs.parameters} emptyText="No parameters." exportFileName="api-parameters" pageSizeOptions={[10, 25]} columns={[
          { key: 'name', label: 'Name' },
          { key: 'from', label: 'From' },
          { key: 'required', label: 'Required' },
          { key: 'definition', label: 'Definition' },
          { key: 'example', label: 'Example' }
        ]} />
        <h4>Request body</h4>
        <div className="request-details">{docs.body.map(item => <div key={item.label}><span>{item.label}</span><b>{item.text}</b></div>)}</div>
        <h4>Returns</h4>
        <div className="request-details">{docs.returns.map(item => <div key={item.label}><span>{item.label}</span><b>{item.text}</b></div>)}</div>
        <h4>Workflow setup hint</h4>
        <div className="request-details">
          <div><span>Use as workflow trigger?</span><b>{docs.workflowHint}</b></div>
          <div><span>Record number source</span><b>{docs.recordHint}</b></div>
          <div><span>Client lookup</span><b>{docs.clientHint}</b></div>
        </div>
      </div>}
    </Modal>
  </section>
}

type ParameterDoc = { name: string; from: string; required: string; definition: string; example: string }
type DetailDoc = { label: string; text: string }

const definitions: Record<string, string> = {
  id: 'Primary numeric identifier of the selected record.',
  taskId: 'Workflow task identifier assigned to the approver.',
  instanceId: 'Workflow instance identifier.',
  action: 'Workflow decision value such as Approved, Rejected, or Sent Back.',
  code: 'Report or master code.',
  kind: 'Tax engine row category.',
  stepCode: 'Setup checklist step code.',
  payRunId: 'Payroll run identifier.',
  employeeId: 'Employee identifier.',
  jobId: 'Background import job identifier.',
  clientId: 'Client identifier.',
  workLocationId: 'Work location identifier.',
  month: 'Payroll/attendance month in YYYY-MM format.',
  year: 'Calendar year.',
  fromDate: 'Report start date in YYYY-MM-DD format.',
  toDate: 'Report end date in YYYY-MM-DD format.',
  department: 'Department name filter.',
  status: 'Transaction status filter.',
  payPeriod: 'Payroll period, usually YYYY-MM.'
}

function describeApi(row: ApiCatalogRow) {
  const parameters = [...pathParameters(row), ...noteParameters(row)]
  const body = bodyDocs(row)
  const returns = returnDocs(row)
  return {
    parameters,
    body,
    returns,
    contentType: row.method === 'GET' ? 'None' : row.notes?.includes('Form data') ? 'multipart/form-data' : 'application/json',
    workflowHint: row.workflowCandidate === 'Yes' ? 'Recommended. Configure this action in Start Rules.' : row.workflowCandidate === 'Possible' ? 'Possible. Use only if this save/delete needs approval.' : 'Usually not required.',
    recordHint: recordHint(row),
    clientHint: clientHint(row)
  }
}

function pathParameters(row: ApiCatalogRow): ParameterDoc[] {
  return Array.from(row.path.matchAll(/\{([^}:]+)(?::[^}]+)?\}/g)).map(match => ({
    name: match[1],
    from: 'Path',
    required: 'Yes',
    definition: definitions[match[1]] || 'Dynamic value in the request path.',
    example: exampleFor(match[1])
  }))
}

function noteParameters(row: ApiCatalogRow): ParameterDoc[] {
  const query = row.notes?.match(/Query:\s*([^.]*)/i)?.[1]
  const formData = row.notes?.includes('Form data')
  const queryParams = query ? query.split(',').map(item => item.trim()).filter(Boolean).map(name => ({
    name,
    from: 'Query string',
    required: ['clientId', 'month'].includes(name) ? 'Usually' : 'Optional',
    definition: definitions[name] || 'Filter or option passed in the URL query string.',
    example: exampleFor(name)
  })) : []
  const formParams = formData ? [
    { name: 'file', from: 'Form data', required: 'Yes', definition: 'Uploaded file.', example: 'employees.xlsx' },
    { name: 'clientId', from: 'Form data', required: 'Usually', definition: definitions.clientId, example: '1' }
  ] : []
  return [...queryParams, ...formParams]
}

function bodyDocs(row: ApiCatalogRow): DetailDoc[] {
  if (row.method === 'GET') return [{ label: 'Body', text: 'No request body. Pass values through path or query parameters.' }]
  if (row.method === 'DELETE') return [{ label: 'Body', text: 'Usually no request body. Identifier is taken from the path and extra filters from query string.' }]
  if (row.notes?.includes('Form data')) return [{ label: 'Body', text: 'Multipart form upload. Include file and required form fields shown in parameters.' }]
  const path = row.path
  if (path === '/api/auth/login') return [{ label: 'JSON', text: '{ email, password }' }]
  if (path === '/api/workflows') return [{ label: 'JSON', text: '{ id, clientId, code, name, resourceType, isActive, stages[] }' }]
  if (path === '/api/workflows/activities') return [{ label: 'JSON', text: '{ id, activityCode, displayName, moduleCode, resourceType, description, isActive }' }]
  if (path === '/api/workflows/action-rules') return [{ label: 'JSON', text: '{ activityCode, httpMethod, pathPattern, resourceType, resourceIdSource, client lookup fields, workflowId, isActive }' }]
  if (path === '/api/clients') return [{ label: 'JSON', text: '{ id, name, code, contactPerson, email, phone, address, isActive }' }]
  if (path === '/api/work-locations') return [{ label: 'JSON', text: '{ id, clientId, name, address, city, state, postalCode, gstin, isPrimary, isActive }' }]
  if (path === '/api/dropdowns') return [{ label: 'JSON', text: '{ id, clientId, type, value, configJson, isActive }' }]
  if (path.includes('/leave-attendance/holidays')) return [{ label: 'JSON', text: '{ id, clientId, name, holidayType, startDate, endDate, description, allLocations, workLocationIds[] }' }]
  if (path.includes('/attendance/daily/batch')) return [{ label: 'JSON', text: '{ clientId, month, workLocationId, entries[] }' }]
  if (path.includes('/attendance/daily')) return [{ label: 'JSON', text: '{ clientId, employeeId, month, entries[] }' }]
  if (path.includes('/groups')) return [{ label: 'JSON', text: '{ id, clientId, name, workLocationId, department, designation, workWeek, cycle days, employeeIds[], isActive }' }]
  if (path === '/api/pay-runs') return [{ label: 'JSON', text: '{ clientId, payPeriod, payRunType, selectedEmployeeIds?, forceRebuild? }' }]
  if (path.includes('/pay-runs/{id}/payments')) return [{ label: 'JSON', text: '{ paidOn, referenceNo, remarks }' }]
  if (path === '/api/payroll-adjustments') return [{ label: 'JSON', text: 'Payroll adjustment record with client, employee, pay period, component, amount, and reason.' }]
  if (path === '/api/employees/actions') return [{ label: 'JSON', text: 'Employee action request with employee, action type, reason, effective date, and infotype changes.' }]
  if (path === '/api/employees') return [{ label: 'JSON', text: 'Employee master payload. Query may include infotypeCode and changeReason.' }]
  return [{ label: 'JSON', text: 'Send the matching request model for this save/action endpoint. Required fields are validated by the API.' }]
}

function returnDocs(row: ApiCatalogRow): DetailDoc[] {
  if (row.method === 'DELETE') return [{ label: 'Success', text: '204 No Content or success response after deletion/deactivation.' }, { label: 'Error', text: '400/403/404/500 with error message.' }]
  if (row.method === 'GET') return [{ label: 'Success', text: '200 OK with requested record/list/report data.' }, { label: 'Error', text: '403 if user has no permission, or 500 if server fails.' }]
  if (row.path.includes('/submit')) return [{ label: 'Success', text: '200 OK with updated transaction status. If configured, workflow task is created after success.' }, { label: 'Error', text: '400 for validation failure, 403 for permission issue, 500 for server error.' }]
  if (row.path.includes('/import') || row.path.includes('/jobs')) return [{ label: 'Success', text: 'Import preview/result/job status object.' }, { label: 'Error', text: 'Validation errors with row-level messages where available.' }]
  return [{ label: 'Success', text: 'Saved/updated record or 204 No Content depending on endpoint.' }, { label: 'Error', text: '400 validation error, 403 permission error, or 500 server error.' }]
}

function recordHint(row: ApiCatalogRow) {
  const pathParam = pathParameters(row)[0]?.name
  if (pathParam) return `URL value / ${pathParam}`
  if (row.method === 'POST') return row.path.includes('/import') ? 'API response value / jobId or created id' : 'Form/request value / id or API response value after creation'
  return 'Not applicable'
}

function clientHint(row: ApiCatalogRow) {
  if (row.path.includes('/pay-runs')) return 'Lookup from table: payruns.Id -> payruns.ClientId'
  if (row.path.includes('/employees')) return 'Use request clientId or lookup from employees.Id -> employees.ClientId'
  if (row.path.includes('/leave-attendance')) return 'Use request/query clientId'
  if (row.notes?.includes('clientId')) return 'Use query/request clientId'
  return 'Logged-in user client or table lookup if record has ClientId'
}

function exampleFor(name: string) {
  if (name === 'month' || name === 'payPeriod') return '2026-04'
  if (name === 'fromDate' || name === 'toDate') return '2026-04-01'
  if (name === 'action') return 'Approved'
  if (name === 'kind') return 'slabs'
  if (name === 'code') return 'client-billing-report'
  if (name === 'jobId') return 'a3f...guid'
  if (name.toLowerCase().includes('id')) return '123'
  return 'value'
}
