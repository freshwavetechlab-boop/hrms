import { useMemo } from 'react'
import SunEditorModule from 'suneditor-react'
import type { SunEditorOptions } from 'suneditor/src/options'
import type { SunEditorReactProps } from 'suneditor-react/dist/types/SunEditorReactProps'
import type { ComponentType } from 'react'
import 'suneditor/dist/css/suneditor.min.css'
import './CommunicationRichEditor.css'

type Props = {
  value: string
  onChange: (value: string) => void
  onFilesPasted?: (files: File[]) => void
  placeholder?: string
  compact?: boolean
}

const fonts = ['Aptos', 'Calibri', 'Arial', 'Tahoma', 'Verdana', 'Georgia', 'Times New Roman', 'Courier New']
const colors = [
  '#111827', '#334155', '#64748b', '#ffffff', '#991b1b', '#dc2626', '#f97316', '#f59e0b',
  '#166534', '#16a34a', '#0f766e', '#0891b2', '#1d4ed8', '#3b82f6', '#5b21b6', '#8b5cf6', '#c026d3',
]

// suneditor-react is published as CommonJS and Vite exposes its component on
// `.default` in development. Normalizing it here keeps dev and production
// builds on the same runtime shape.
const SunEditor = (((SunEditorModule as unknown as { default?: ComponentType<SunEditorReactProps> }).default)
  || SunEditorModule) as ComponentType<SunEditorReactProps>

export default function CommunicationRichEditor({ value, onChange, onFilesPasted, placeholder = 'Write your message…', compact = false }: Props) {
  const options = useMemo<SunEditorOptions>(() => ({
    mode: 'classic' as const,
    resizingBar: false,
    buttonList: compact
      ? [['undo', 'redo'], ['bold', 'underline', 'italic'], ['fontColor', 'hiliteColor'], ['list', 'link']]
      : [
          ['undo', 'redo'],
          ['font', 'fontSize', 'formatBlock'],
          ['bold', 'underline', 'italic', 'strike'],
          ['fontColor', 'hiliteColor', 'removeFormat'],
          ['outdent', 'indent', 'align', 'list', 'lineHeight'],
          ['table', 'link', 'image'],
          ['codeView', 'fullScreen'],
        ],
    font: fonts,
    fontSize: [10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 36],
    colorList: colors,
    formats: ['p', 'div', 'h1', 'h2', 'h3', 'blockquote', 'pre'],
    defaultTag: 'p',
    imageResizing: true,
    imageFileInput: true,
    imageUrlInput: false,
    imageAccept: 'image/*',
    imageMultipleFile: true,
    height: compact ? '150px' : '310px',
    defaultStyle: 'font-family: Aptos, Calibri, Arial, sans-serif; font-size: 14px; color: #172033; line-height: 1.65;',
  }), [compact])

  const captureImages = (files: File[]) => {
    const images = files.filter(file => file.type.startsWith('image/'))
    if (images.length) onFilesPasted?.(images)
  }

  return <div className={`communication-rich-editor${compact ? ' compact' : ''}`} data-testid="communication-message-body">
    <SunEditor
      setContents={value}
      placeholder={placeholder}
      setOptions={options}
      setDefaultStyle="font-family: Aptos, Calibri, Arial, sans-serif; font-size: 14px; color: #172033; line-height: 1.65;"
      onChange={onChange}
      onPaste={event => captureImages(Array.from(event.clipboardData?.files || []))}
      onDrop={event => captureImages(Array.from(event.dataTransfer?.files || []))}
      onImageUploadBefore={(files, _info, uploadHandler) => {
        captureImages(files)
        uploadHandler(files)
        return undefined
      }}
    />
  </div>
}
