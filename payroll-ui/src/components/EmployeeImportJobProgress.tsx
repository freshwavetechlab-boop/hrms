import { useEffect, useRef, useState } from 'react'
import type { BulkImportStatus } from '../services/settingsService'
import { employeeJobIsRunning, employeeJobTitle, readEmployeeImportJob, type EmployeeImportJob } from '../services/employeeImportJobs'
import BulkUploadProgressModal from './BulkUploadProgressModal'

export type TrackedEmployeeJob = {
  id: string
  clientId: number
  scope: string
  initial?: BulkImportStatus
  assignment?: { alreadyAssigned: number; blocked: { reason: string }[]; rosterTemplateId?: string; removeTestPayroll?: boolean }
  operation?: EmployeeImportJob['operation']
}

export default function EmployeeImportJobProgress({ tracked, open, onClose, onFinished }: {
  tracked: TrackedEmployeeJob; open: boolean; onClose: () => void; onFinished: (job: BulkImportStatus, cleanup: boolean) => void
}) {
  const [job, setJob] = useState<BulkImportStatus | EmployeeImportJob | undefined>(tracked.initial)
  const [message, setMessage] = useState('')
  const [unavailable, setUnavailable] = useState(false)
  const finished = useRef(onFinished)
  finished.current = onFinished

  useEffect(() => {
    const controller = new AbortController()
    let timer: number | undefined
    const complete = (current: BulkImportStatus) => finished.current(current, tracked.assignment?.removeTestPayroll === true ||
      ('kind' in current && current.kind === 'salary-template-assignment-cleanup'))
    const poll = async () => {
      const result = await readEmployeeImportJob(tracked.id, tracked.clientId, controller.signal)
      if (controller.signal.aborted) return
      if (result.ok && result.data?.jobId === tracked.id && (!result.data.clientId || result.data.clientId === tracked.clientId)) {
        const current = result.data
        setJob(current)
        setMessage('')
        if (!employeeJobIsRunning(current)) { complete(current); return }
      } else if ([401, 403, 404].includes(result.status) || result.ok) {
        setUnavailable(true)
        setMessage('Job details are unavailable for this account and client.')
        return
      } else {
        setMessage('Reconnecting to job progress…')
      }
      timer = window.setTimeout(poll, result.ok ? 1200 : 5000)
    }
    if (tracked.initial && !employeeJobIsRunning(tracked.initial)) complete(tracked.initial)
    else void poll()
    return () => { controller.abort(); window.clearTimeout(timer) }
  }, [tracked])

  const state = unavailable ? 'error' : !job || employeeJobIsRunning(job) ? 'uploading' : job.state === 'Completed' ? 'success' : 'error'
  const percent = state === 'success' ? 100 : job?.totalRows ? Math.min(state === 'uploading' ? 99 : 100, Math.round(100 * job.completedRows / job.totalRows)) : 0
  const kind = tracked.assignment?.removeTestPayroll ? 'salary-template-assignment-cleanup' :
    job && 'kind' in job ? job.kind : tracked.assignment ? 'salary-template-assignment' : 'employee-import'
  const title = employeeJobTitle({ kind,
    operation: job && 'operation' in job ? job.operation : tracked.operation ?? 'upsert' })
  const assignment = tracked.assignment
  return <BulkUploadProgressModal open={open} background title={title} state={state} percent={percent} onClose={onClose}
    summary={unavailable ? { totalRows: 0, errors: [message] } : job ?? { totalRows: 0 }}
    stage={state === 'uploading' ? message || 'Track this job in Notifications.' : undefined}
    errorMessage={unavailable ? 'Could not open job details' : state === 'error' ? 'Employee job needs attention' : undefined}
    successMessage={`${title} completed`} successDescription={kind === 'salary-template-assignment-cleanup' ? `${job?.updated ?? 0} employee${job?.updated === 1 ? '' : 's'} updated` : assignment
      ? `${job?.updated ?? 0} assigned · ${assignment.alreadyAssigned} already assigned · ${assignment.blocked.length} blocked`
      : job && 'kind' in job && job.kind === 'salary-template-assignment' ? `${job.updated} assigned` : undefined} />
}
