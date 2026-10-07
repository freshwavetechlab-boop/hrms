import { useEffect, useState } from 'react'
import { ExclamationCircleOutlined } from '@ant-design/icons'
import type { Employee } from '../types/payroll'
import { getEffectiveAttachmentConfigurationsResult, getEntityAttachmentsResult } from '../services/attachmentService'
import { employeeMissingInformation, type EmployeeDocumentCheck, type EmployeeInformationIssue } from '../utils/employeeMissingInformation'

// Page-local caches share configuration reads across visible cards, without crossing user sessions.
export function createEmployeeDocumentCheck() {
  const configurations = new Map<number, Promise<EmployeeDocumentCheck['configurations'] | null>>()
  const documents = new Map<string, Promise<EmployeeDocumentCheck | null>>()
  return (employee: Employee) => {
    if (!configurations.has(employee.clientId)) configurations.set(employee.clientId, Promise.all(
      ['EMPLOYEE_CREATE_EDIT', 'EMPLOYEE_PROFILE'].map(form => getEffectiveAttachmentConfigurationsResult(employee.clientId, 'EMPLOYEE', form))
    ).then(results => {
      if (results.some(result => !result.ok)) { configurations.delete(employee.clientId); return null }
      return results.flatMap(result => result.data)
    }))
    const key = `${employee.clientId}:${employee.id}`
    if (!documents.has(key)) documents.set(key, Promise.all([configurations.get(employee.clientId)!, getEntityAttachmentsResult('EMPLOYEE', employee.id)]).then(([configs, files]) => {
      if (!configs || !files.ok) { documents.delete(key); return null }
      return { configurations: configs, attachments: files.data }
    }))
    return documents.get(key)!
  }
}

export default function EmployeeMissingInformation({ employee, checkDocuments, onOpen, additionalIssues = [] }: {
  additionalIssues?: { infotype: string; label: string }[]; employee: Employee; checkDocuments: ReturnType<typeof createEmployeeDocumentCheck>; onOpen: (infotype: EmployeeInformationIssue['infotype']) => void
}) {
  const [documents, setDocuments] = useState<EmployeeDocumentCheck | null>()
  const [retry, setRetry] = useState(0)
  useEffect(() => {
    let active = true
    setDocuments(undefined)
    void checkDocuments(employee).then(result => { if (active) setDocuments(result) })
    return () => { active = false }
  }, [employee.id, employee.clientId, checkDocuments, retry])
  const issues = employeeMissingInformation(employee, documents)
  for (const extra of additionalIssues) {
    const issue = issues.find(issue => issue.infotype === extra.infotype)
    if (issue) { if (!issue.fields.includes(extra.label)) issue.fields.push(extra.label) }
    else issues.push({ infotype: extra.infotype as EmployeeInformationIssue['infotype'], label: 'Additional information', fields: [extra.label] })
  }
  if (!issues.length && documents) return null
  return <div className={`employee-missing-information${issues.length ? '' : ' is-pending'}`} aria-label="Employee information status">
    {issues.length > 0 && <details><summary><ExclamationCircleOutlined /> Missing information <span>{issues.reduce((total, issue) => total + issue.fields.length, 0)}</span></summary>
      <div className="employee-missing-items">{issues.map(issue => <button type="button" key={issue.infotype} onClick={() => onOpen(issue.infotype)}><b>{issue.label}:</b> {issue.fields.join(', ')}</button>)}</div></details>}
    {documents === undefined && <small role="status">Checking documents...</small>}
    {documents === null && <small>Documents could not be checked. <button type="button" onClick={() => setRetry(value => value + 1)}>Retry</button></small>}
  </div>
}
