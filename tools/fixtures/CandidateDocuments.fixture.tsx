import React, { useState } from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { Button, ConfigProvider, Space } from 'antd'
import theme from '/src/theme/hrmsTheme.ts'
import ToastProvider from '/src/components/ToastProvider.tsx'
import PublicCandidateDocumentsPage from '/src/pages/PublicCandidateDocumentsPage.tsx'
import RecruitmentOfferMail from '/src/components/RecruitmentOfferMail.tsx'
import OfferDocumentRequestFields from '/src/components/OfferDocumentRequestFields.tsx'
import { useOfferDocumentRequests } from '/src/hooks/useOfferDocumentRequests.ts'
import '/src/index.css'
import '/src/OrganizationSetup.css'
import '/src/AppShell.css'
import '/src/RecruitmentViewport.css'

function Picker() {
  const documents = useOfferDocumentRequests(55)
  return <section style={{ maxWidth: 780, margin: '24px auto', padding: 16 }}>
    <h1>Offer document selection</h1>
    <OfferDocumentRequestFields fields={documents.fields} value={documents.documentRequests} onChange={documents.setDocumentRequests} loading={documents.loading} error={documents.error} onRetry={documents.refresh} />
    <p><Button type="primary" disabled={!documents.ready}>Continue</Button></p>
    <pre aria-label="Selected document payload" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{JSON.stringify(documents.documentRequests, null, 2)}</pre>
  </section>
}

function App() {
  const [mailOpen, setMailOpen] = useState(new URLSearchParams(window.location.search).get('case') === 'mail')
  const [result, setResult] = useState('')
  const publicPage = window.location.pathname.startsWith('/candidate-documents/')
  return <>
    <style>{`.candidate-documents-fixture-banner{padding:8px 12px;background:#e6f4ff;color:#214663;font-size:12px}.candidate-documents-fixture-nav{padding:12px;display:flex;gap:12px;flex-wrap:wrap}.candidate-documents-fixture-nav a{text-decoration:underline}`}</style>
    <div className="candidate-documents-fixture-banner">Synthetic local test · In-memory API · No database · No emails</div>
    {publicPage ? <PublicCandidateDocumentsPage /> : <>
      <nav className="candidate-documents-fixture-nav"><a href="/candidate-documents/open">Open upload</a><a href="/candidate-documents/retry">Retry upload</a><a href="/candidate-documents/complete">Already submitted</a><a href="/candidate-documents/expired">Expired</a><a href="/candidate-documents/load-error">Load error</a><a href="/document-verification?case=picker">Document picker</a><a href="/document-verification?case=mail">Email drawer</a><a href="/__fixture/sample.pdf" download>Sample PDF</a></nav>
      {new URLSearchParams(window.location.search).get('case') === 'picker' ? <Picker /> : <section style={{ padding: 24 }}><h1>Offer email review</h1><Space wrap><Button type="primary" onClick={() => setMailOpen(true)}>Review offer email</Button><a href="/__fixture/state" target="_blank" rel="noreferrer">Captured synthetic requests</a></Space><p role="status">{result}</p></section>}
      {mailOpen && <RecruitmentOfferMail clientId={55} ids={[1, 2, 3]} retryFailed={false} onClose={() => setMailOpen(false)} onQueued={job => { setResult(`Synthetic job ${job.id} queued. No email was sent.`); setMailOpen(false) }} />}
    </>}
  </>
}

ReactDOM.createRoot(document.getElementById('root')!).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><App /></ToastProvider></BrowserRouter></ConfigProvider>)
