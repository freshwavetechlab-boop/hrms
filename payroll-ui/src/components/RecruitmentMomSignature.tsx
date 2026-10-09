import { useEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import { Alert, Button, Form, Input, Modal, Select, Tag, message } from 'antd'
import { useAuthSession } from './AuthGate'
import { getRecruitmentProcessDocumentSignatures, saveRecruitmentProcessDocumentSignature } from '../services/recruitmentCaseService'
import type { RecruitmentProcessDocument, RecruitmentProcessDocumentSignature } from '../types/recruitmentCases'
import './RecruitmentWorkOrderWorkspace.css'

export default function RecruitmentMomSignature({ document, onClose, onSaved, readOnly = false }: { document: RecruitmentProcessDocument; onClose: () => void; onSaved: () => Promise<void>; readOnly?: boolean }) {
  const session = useAuthSession()
  const [documentSignatures, setDocumentSignatures] = useState<RecruitmentProcessDocumentSignature[]>([])
  const [signatureMethod, setSignatureMethod] = useState<'Typed' | 'Drawn' | 'Image'>('Typed')
  const [signerName, setSignerName] = useState(session?.user.displayName || '')
  const [signatureDataUrl, setSignatureDataUrl] = useState('')
  const [signatureSaving, setSignatureSaving] = useState(false)
  useEffect(() => { void getRecruitmentProcessDocumentSignatures(document.id).then(setDocumentSignatures) }, [document.id])
  const captureSignature = async () => {
    if (!document || readOnly) return
    if (signerName.trim().length < 2) return void message.error("Enter the signer's full name.")
    if (signatureMethod !== 'Typed' && !signatureDataUrl) return void message.error('Draw or upload the signature first.')
    setSignatureSaving(true)
    const response = await saveRecruitmentProcessDocumentSignature(document.id, {
      signatureMethod,
      signerName: signerName.trim(),
      signatureDataUrl: signatureMethod === 'Typed' ? signerName.trim() : signatureDataUrl,
    })
    setSignatureSaving(false)
    if (!response.ok || !response.data) return
    setDocumentSignatures(await getRecruitmentProcessDocumentSignatures(document.id))
    await onSaved()
  }

  const uploadSignatureImage = (file?: File | null) => {
    if (!file) return
    if (!/^image\/(png|jpeg)$/i.test(file.type) || file.size > 750 * 1024) return void message.error('Use a PNG/JPG signature image up to 750 KB.')
    const reader = new FileReader()
    reader.onload = () => setSignatureDataUrl(String(reader.result || ''))
    reader.readAsDataURL(file)
  }

  return (<Modal
      width={720}
      centered
      bodyStyle={{ maxHeight: '65dvh', overflowY: 'auto' }}
      open
      title={readOnly ? 'Job MoM & panel signatures' : 'Sign job MoM'}
      okText="Capture signature"
      confirmLoading={signatureSaving}
      okButtonProps={{ style: readOnly ? { display: 'none' } : undefined, disabled: readOnly || document.status === 'Signed' || signerName.trim().length < 2 || (signatureMethod !== 'Typed' && !signatureDataUrl) }}
      onOk={() => void captureSignature()}
      onCancel={() => { if (!signatureSaving) onClose() }}
      cancelButtonProps={{ disabled: signatureSaving }}
      destroyOnClose
    >
      <Alert showIcon type="info" message="Audited electronic signature" description="Your signed-in user, method and timestamp are stored with this MoM. This combined MoM belongs to the job. Every assigned panel member signs using their own login. The final required signature sends agreed terms to HR Division for approval." />
      <Tag>{document.signatureCount}/{document.requiredSignatureCount} panel signatures</Tag>
      {document.bodySnapshot && <pre style={{ whiteSpace: 'pre-wrap', maxHeight: 300, overflow: 'auto' }}>{document.bodySnapshot}</pre>}
      {!readOnly && <Form component="div" layout="vertical" className="mom-signature-form">
        <Form.Item label="Signer name" required><Input value={signerName} onChange={event => setSignerName(event.target.value)} /></Form.Item>
        <Form.Item label="Signature method" required><Select value={signatureMethod} options={['Typed', 'Drawn', 'Image'].map(value => ({ value, label: value === 'Image' ? 'Upload PNG/JPG' : value }))} onChange={value => { setSignatureMethod(value); setSignatureDataUrl('') }} /></Form.Item>
        {signatureMethod === 'Typed' && <div className="mom-typed-signature" aria-label="Typed signature preview">{signerName || 'Your signature'}</div>}
        {signatureMethod === 'Drawn' && <SignaturePad onChange={setSignatureDataUrl} />}
        {signatureMethod === 'Image' && <div className="mom-signature-upload"><input type="file" accept="image/png,image/jpeg" onChange={event => uploadSignatureImage(event.target.files?.[0])} />{signatureDataUrl && <img src={signatureDataUrl} alt="Uploaded signature preview" />}</div>}
      </Form>}
      {!!documentSignatures.length && <div className="mom-signature-list"><b>Captured signatures</b>{documentSignatures.map(row => <div key={row.id}><span>{row.signerName} · {row.signerRole}</span><Tag color="green">{row.signatureMethod} · {new Date(row.signedAtUtc).toLocaleString()}</Tag></div>)}</div>}
    </Modal>)
}

function SignaturePad({ onChange }: { onChange: (value: string) => void }) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const drawing = useRef(false)
  const point = (event: ReactPointerEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current!
    const bounds = canvas.getBoundingClientRect()
    return { x: (event.clientX - bounds.left) * (canvas.width / bounds.width), y: (event.clientY - bounds.top) * (canvas.height / bounds.height) }
  }
  const start = (event: ReactPointerEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current!
    const current = point(event)
    drawing.current = true
    canvas.setPointerCapture(event.pointerId)
    const context = canvas.getContext('2d')!
    context.beginPath(); context.moveTo(current.x, current.y)
  }
  const move = (event: ReactPointerEvent<HTMLCanvasElement>) => {
    if (!drawing.current) return
    const canvas = canvasRef.current!
    const current = point(event)
    const context = canvas.getContext('2d')!
    context.strokeStyle = '#17223b'; context.lineWidth = 2.4; context.lineCap = 'round'; context.lineJoin = 'round'
    context.lineTo(current.x, current.y); context.stroke()
  }
  const finish = () => {
    if (!drawing.current || !canvasRef.current) return
    drawing.current = false
    onChange(canvasRef.current.toDataURL('image/png'))
  }
  const clear = () => {
    const canvas = canvasRef.current
    if (!canvas) return
    canvas.getContext('2d')?.clearRect(0, 0, canvas.width, canvas.height)
    onChange('')
  }
  return <div className="mom-signature-pad"><canvas ref={canvasRef} width={640} height={180} onPointerDown={start} onPointerMove={move} onPointerUp={finish} onPointerCancel={finish} onPointerLeave={finish} /><Button size="small" onClick={clear}>Clear drawing</Button></div>
}
