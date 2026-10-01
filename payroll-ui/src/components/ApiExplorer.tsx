import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Input, Modal, Space, Spin } from 'antd'
import { FilePdfOutlined } from '@ant-design/icons'
import type { ApiCatalogRow } from '../data/apiCatalog'
import { apiUrl } from '../services/apiClient'
import { loadSwaggerAssets, type ApiDocument, type SwaggerRequest } from '../services/apiDocumentationService'
import './ApiExplorer.css'

export default function ApiExplorer({ row, document, onClose, onManageKeys }: { row: ApiCatalogRow; document: ApiDocument; onClose: () => void; onManageKeys?: () => void }) {
  const root = useRef<HTMLDivElement>(null)
  const token = useRef('')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)
  const [exporting, setExporting] = useState(false)
  const machine = row.path === '/api/integrations/attendance/punches'
  const url = apiUrl(row.path)
  const exportDocument = async () => {
    setExporting(true)
    try {
      const { downloadApiDocumentationPdf } = await import('../utils/apiDocumentationPdf')
      downloadApiDocumentationPdf(row, document, url)
    } catch { setError('Unable to export this API document. Please try again.') }
    finally { setExporting(false) }
  }
  useEffect(() => {
    let active = true
    void loadSwaggerAssets().then(() => {
      if (!active || !root.current) return
      if (!window.SwaggerUIBundle) throw new Error('Unable to initialize the API explorer.')
      const operation = document.paths[row.path]?.[row.method.toLowerCase()]
      window.SwaggerUIBundle({
        spec: { ...document, servers: [{ url: apiUrl('') || window.location.origin }], paths: { [row.path]: { [row.method.toLowerCase()]: { ...operation, summary: row.purpose, security: [] } } } },
        domNode: root.current, docExpansion: 'full', deepLinking: false,
        defaultModelsExpandDepth: -1, displayRequestDuration: true, persistAuthorization: false,
        requestInterceptor: (request: SwaggerRequest) => {
          const target = new URL(request.url, window.location.origin)
          if (target.origin !== new URL(apiUrl('/'), window.location.origin).origin || !target.pathname.startsWith('/api/')) throw new Error('Use the configured HRMS API server.')
          request.headers ||= {}
          if (machine) {
            if (!token.current.trim()) { setError('Enter the attendance API key before executing this request.'); throw new Error('Attendance API key is required.') }
            request.headers.Authorization = 'Bearer ' + token.current.trim().replace(/^Bearer\s+/i, '')
            request.credentials = 'omit'
          } else {
            const session = sessionStorage.getItem('payroll.auth.token')
            if (session && !request.headers.Authorization) request.headers.Authorization = 'Bearer ' + session
            request.credentials = 'include'
          }
          setError(''); return request
        },
      })
      setLoading(false)
    }).catch(err => { if (active) { setError(err instanceof Error ? err.message : 'Unable to load API explorer.'); setLoading(false) } })
    return () => { active = false; token.current = ''; if (root.current) root.current.replaceChildren() }
  }, [row.id, document])
  return <Modal title={row.method + ' ' + row.path} open onCancel={onClose} footer={null} width="min(1160px, 96vw)" className="api-explorer-modal">
    <Space wrap className="api-explorer-address"><Input aria-label="API request URL" readOnly value={url} /><Button onClick={() => void navigator.clipboard.writeText(url).catch(() => setError('Select and copy the URL manually.'))}>Copy URL</Button><Button icon={<FilePdfOutlined aria-hidden="true" />} loading={exporting} onClick={() => void exportDocument()}>Export document (PDF)</Button></Space>
    {machine ? <div className="api-explorer-auth"><label htmlFor="attendance-test-key">API key (Bearer)</label><Space.Compact block><Input.Password id="attendance-test-key" autoComplete="off" placeholder="Paste the API key from API keys" onChange={event => { token.current = event.target.value }} />{onManageKeys && <Button onClick={onManageKeys}>Generate API key</Button>}</Space.Compact><p>The key and Machine ID must match. IN and OUT use this same endpoint.</p></div> : <p>Requests use your current login and permissions.</p>}
    {error && <Alert type="error" showIcon message={error} />}
    {loading && <Spin />}
    <div ref={root} className="api-explorer-document" onClickCapture={event => {
      if (machine && !token.current.trim() && event.target instanceof Element && event.target.closest('.execute')) {
        event.preventDefault(); event.stopPropagation(); setError('Enter the attendance API key before executing this request.')
        window.document.getElementById('attendance-test-key')?.focus()
      }
    }} />
  </Modal>
}
