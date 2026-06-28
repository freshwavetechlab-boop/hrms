import { type ReactNode, useId, useState } from 'react'

type Props = {
  accept?: string
  fileName?: string
  title: string
  hint: string
  preview?: ReactNode
  onFile: (file: File) => void
}

export default function FileDropZone({ accept, fileName, title, hint, preview, onFile }: Props) {
  const id = useId()
  const [dragging, setDragging] = useState(false)
  const pick = (files: FileList | null) => { const file = files?.[0]; if (file) onFile(file) }
  const open = () => document.getElementById(id)?.click()
  return <div className={`file-drop-zone ${dragging ? 'dragging' : ''}`} role="button" tabIndex={0} onClick={open} onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') open() }} onDragOver={event => { event.preventDefault(); setDragging(true) }} onDragLeave={() => setDragging(false)} onDrop={event => { event.preventDefault(); setDragging(false); pick(event.dataTransfer.files) }}>
    <input id={id} type="file" accept={accept} onClick={event => event.stopPropagation()} onChange={event => pick(event.target.files)} />
    {preview && <span className="file-drop-preview">{preview}</span>}
    <span className="file-drop-icon">↥</span>
    <span className="file-drop-copy"><strong>{fileName || title}</strong><small>{fileName ? 'Selected file' : hint}</small></span>
    <span className="file-drop-action">Browse</span>
  </div>
}
