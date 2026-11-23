import { useEffect, useRef, useState } from 'react'
import { useLocation } from 'react-router-dom'
import { Alert, Button, Drawer, Select, Space, Tag } from 'antd'
import DataTable from './DataTable'
import BulkMailProgress from './BulkMailProgress'
import { previewOfferMail, sendOfferMail, type OfferDocumentRequestSelection, type OfferMailItem, type OfferMailJob } from '../services/recruitmentOfferMailService'
import { useOfferDocumentRequests } from '../hooks/useOfferDocumentRequests'
import OfferDocumentRequestFields from './OfferDocumentRequestFields'

export function OfferMailProgress({ jobs, loading, error, onRefresh, onRetry }: { jobs: OfferMailJob[]; loading: boolean; error: string; onRefresh: () => void; onRetry: (ids: number[]) => void }) {
  const location = useLocation()
  const [selectedId, setSelectedId] = useState(new URLSearchParams(window.location.search).get('mailJobId') || '')
  const [expanded, setExpanded] = useState(false), [filter, setFilter] = useState('All')
  useEffect(() => {
    const requestedId = new URLSearchParams(location.search).get('mailJobId')
    if (requestedId) { setSelectedId(requestedId); setFilter('All'); setExpanded(true) }
  }, [location.search, location.key])
  const job = jobs.find(item => item.id === selectedId) || jobs[0]
  return <><BulkMailProgress jobs={jobs} selectedId={selectedId} onSelect={setSelectedId} loading={loading} error={error} onRefresh={onRefresh} documentLabel="letters" documentPlural="offer letters" expanded={false} onToggle={() => setExpanded(true)} />
    <Drawer open={expanded && !!job} title="Offer email progress" className="document-roster-mail-history" width="min(1200px, 98vw)" onClose={() => setExpanded(false)} footer={<Button onClick={() => setExpanded(false)}>Close</Button>}>
      <BulkMailProgress jobs={jobs} selectedId={selectedId} onSelect={setSelectedId} loading={loading} error={error} onRefresh={onRefresh} documentLabel="letters" documentPlural="offer letters" />
      {job && <><Space wrap><Select aria-label="Offer email status filter" value={filter} onChange={setFilter} options={['All', 'Sent', 'Queued', 'Failed', 'Missing email', 'Not released', 'Skipped', 'Unknown'].map(value => ({ value, label: value }))} /><Button disabled={!job.items.some(item => item.status === 'Failed')} onClick={() => { setExpanded(false); onRetry(job.items.filter(item => item.status === 'Failed').map(item => item.rosterId)) }}>Retry failed</Button></Space><OfferMailTable rows={job.items.filter(item => filter === 'All' || item.status === filter)} /></>}
    </Drawer>
  </>
}

export default function RecruitmentOfferMail({ clientId, ids, retryFailed, onClose, onQueued }: { clientId: number; ids: number[]; retryFailed: boolean; onClose: () => void; onQueued: (job: OfferMailJob) => void }) {
  const [items, setItems] = useState<OfferMailItem[]>([]), [loading, setLoading] = useState(true), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const documents = useOfferDocumentRequests(clientId)
  const [locked, setLocked] = useState(false)
  const documentSnapshot = useRef<OfferDocumentRequestSelection[] | null>(null)
  // Reuse this id after an uncertain response: retrying cannot create two sends.
  const requestId = useRef(crypto.randomUUID())
  const sending = useRef(false)
  useEffect(() => {
    let active = true
    void previewOfferMail(clientId, ids, retryFailed).then(result => { if (!active) return; if (result.ok) setItems(result.data.items); else setError(result.error || 'Unable to preview offer email recipients.') }).finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [clientId, ids, retryFailed])
  const send = async () => {
    if (sending.current || busy || loading || items.length !== ids.length) return
    if (items.some(item => item.status === 'Ready') && !documents.ready && !documentSnapshot.current) { setError(documents.error || 'Select at least one document before emailing the offer letters.'); return }
    documentSnapshot.current ??= documents.documentRequests.map(item => ({ ...item }))
    sending.current = true
    setLocked(true)
    setBusy(true); setError('')
    try {
      const result = await sendOfferMail(clientId, ids, requestId.current, items, retryFailed, documentSnapshot.current)
      if (!result.ok || !result.data) { setError(result.error || 'Unable to queue offer emails. Retry this same action to check its result.'); return }
      onQueued(result.data)
      window.dispatchEvent(new Event('hrms:actions-changed'))
    } finally { sending.current = false; setBusy(false) }
  }
  return <Drawer open title={`Email selected offer letters (${ids.length})`} className="document-roster-mail-drawer" width="min(1150px, 98vw)" onClose={() => { if (!busy) onClose() }} closable={!busy} maskClosable={!busy} footer={<>{error && <Alert className="offer-mail-footer-feedback" type="error" showIcon message={error} />}<Space wrap><Button disabled={busy} onClick={onClose}>Cancel</Button><Button type="primary" loading={busy} disabled={loading || !!error && !items.length || items.length !== ids.length || items.some(item => item.status === 'Ready') && !documents.ready && !documentSnapshot.current} onClick={() => void send()}>Queue emails ({items.filter(item => item.status === 'Ready').length})</Button></Space></>}>
    <Alert type="info" showIcon message="Email the released PDF and document-upload link" description="Choose the documents to request below. Candidates can upload without a login and submit once. The released PDF is attached; drafts and outdated letters are not emailed." />
    <Space wrap><Tag color="blue">Ready: {items.filter(item => item.status === 'Ready').length}</Tag><Tag color="orange">Need attention: {items.filter(item => !['Ready', 'Skipped'].includes(item.status)).length}</Tag><Tag>Previously handled: {items.filter(item => item.status === 'Skipped').length}</Tag></Space>
    <OfferDocumentRequestFields fields={documents.fields} value={documents.documentRequests} onChange={documents.setDocumentRequests} loading={documents.loading} error={documents.error} disabled={busy || locked} onRetry={documents.refresh} />
    <div className="offer-mail-recipient-region"><OfferMailTable rows={items} loading={loading} /></div>
  </Drawer>
}

function OfferMailTable({ rows, loading = false }: { rows: OfferMailItem[]; loading?: boolean }) {
  return <DataTable fillHeight rows={rows} loading={loading} getRowId={row => row.rosterId} title="Offer email recipients" exportFileName="offer-email-recipients" columns={[
    { key: 'candidateCode', label: 'Profile code', width: 150 }, { key: 'candidateName', label: 'Candidate', width: 210 }, { key: 'email', label: 'Email', width: 230 },
    { key: 'status', label: 'Status', width: 140 }, { key: 'sentCopyStatus', label: 'Sent-folder copy', width: 145 }, { key: 'message', label: 'Details', width: 360 },
  ]} />
}
