import { useEffect, useState } from 'react'
import { Alert, Button, Drawer, Space, Spin } from 'antd'
import { ExportOutlined, FilePdfOutlined } from '@ant-design/icons'
import { apiRequest, apiUrl } from '../services/apiClient'

type Props = {
  publicId?: string
  documentUrl?: string
  title?: string
  label?: string
}

// Both the admin portal and candidate action link use the existing secured
// attachment reads. No public asset URL, new permission, or stored copy.
export default function OfferLetterPreview({ publicId, documentUrl, title = 'Offer letter', label = 'View offer' }: Props) {
  const [open, setOpen] = useState(false)
  const [preview, setPreview] = useState<{ url: string; blob: Blob } | null>(null)
  const [error, setError] = useState('')
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    setPreview(null)
    setError('')
    if (!open) return
    let active = true
    let objectUrl: string | undefined
    const controller = new AbortController()
    const timeout = window.setTimeout(() => controller.abort(), 30000)
    void (async () => {
      try {
        let response: Response
        if (publicId) {
          response = await apiRequest(`/api/attachments/${encodeURIComponent(publicId)}/content?download=false`, { signal: controller.signal, loader: false })
        } else if (documentUrl) {
          // The candidate already holds a narrowly scoped, expiring access ticket.
          // Never send administrator credentials to a document URL.
          response = await fetch(apiUrl(documentUrl), { signal: controller.signal, credentials: 'omit', referrerPolicy: 'no-referrer' })
        } else throw new Error('No offer letter is linked.')
        if (!response.ok) throw new Error('The offer letter is unavailable or access has expired. Refresh the page or contact HR.')
        const blob = await response.blob()
        if (await blob.slice(0, 5).text() !== '%PDF-') throw new Error('This attachment is not a PDF. Ask HR to generate or upload a PDF offer letter.')
        if (!active) return
        const pdf = new Blob([blob], { type: 'application/pdf' })
        objectUrl = URL.createObjectURL(pdf)
        setPreview({ url: objectUrl, blob: pdf })
      } catch (reason) {
        if (active) setError(controller.signal.aborted ? 'Opening the PDF timed out. Please try again.' : reason instanceof Error ? reason.message : 'Unable to open the offer letter.')
      } finally { window.clearTimeout(timeout) }
    })()
    return () => {
      active = false
      controller.abort()
      window.clearTimeout(timeout)
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [open, publicId, documentUrl, attempt])

  const exportPdf = () => {
    if (!preview) return
    // A separate URL lets the normal browser PDF tab finish loading even if
    // the user immediately closes the portal drawer.
    const url = URL.createObjectURL(preview.blob)
    const tab = window.open(url, '_blank')
    if (tab) tab.opener = null
    else { URL.revokeObjectURL(url); setError('Allow pop-ups for this portal, then select Export PDF again.'); return }
    window.setTimeout(() => URL.revokeObjectURL(url), 60000)
  }

  return <>
    <Button size="small" icon={<FilePdfOutlined />} onClick={() => setOpen(true)}>{label}</Button>
    <Drawer title={title} open={open} width="min(1050px, 98vw)" onClose={() => setOpen(false)}
      bodyStyle={{ padding: 12, display: 'flex', flexDirection: 'column', gap: 12 }}
      extra={<Button type="primary" icon={<ExportOutlined />} disabled={!preview} onClick={exportPdf}>Export PDF</Button>}>
      {error && <Alert type="error" showIcon message={error} action={<Button size="small" onClick={() => setAttempt(value => value + 1)}>Retry</Button>} />}
      {!preview && !error && <Space role="status" style={{ justifyContent: 'center', minHeight: 180 }}><Spin />Opening secured offer letter…</Space>}
      {preview && <iframe title="Offer letter PDF preview" src={preview.url} style={{ width: '100%', flex: 1, minHeight: '70vh', border: 0 }} />}
    </Drawer>
  </>
}
