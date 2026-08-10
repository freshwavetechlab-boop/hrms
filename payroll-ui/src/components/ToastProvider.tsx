import { createContext, useCallback, useContext, useEffect, useRef } from 'react'
import { notification } from 'antd'
export type ToastType = 'success' | 'error' | 'warning' | 'info'
export type ToastAction = { label: string; href: string }
export type ToastOptions = { actions?: ToastAction[]; duration?: number }
type ToastNotifier = (message: string, type?: ToastType, options?: ToastOptions) => void
let notify: ToastNotifier = () => undefined
export const toast = {
  success: (message: string, options?: ToastOptions) => notify(message, 'success', options),
  error: (message: string, options?: ToastOptions) => notify(message, 'error', options),
  warning: (message: string, options?: ToastOptions) => notify(message, 'warning', options),
  info: (message: string, options?: ToastOptions) => notify(message, 'info', options),
}
const ToastContext = createContext<ToastNotifier>((message, type, options) => notify(message, type, options))
export const useToast = () => useContext(ToastContext)
export default function ToastProvider({ children }: { children: React.ReactNode }) {
  const [api, holder] = notification.useNotification()
  const recent = useRef(new Map<string, number>())
 const show = useCallback((message: string, type: ToastType = 'success', options: ToastOptions = {}) => {

    const text = message.trim()
    if (!text) return
    const key = `${type}:${text}`
    const now = Date.now()
    if (now - (recent.current.get(key) ?? 0) < 1200) return
    recent.current.set(key, now)
    api[type]({
      key,
      message: titleFor(type),
      description: <div className="app-toast-description">
        <span>{text}</span>
        {!!options.actions?.length && <div className="app-toast-actions">
          {options.actions.map(action => <a key={`${action.href}:${action.label}`} href={action.href}>{action.label}<b aria-hidden="true">&#8594;</b></a>)}
        </div>}
      </div>,
      placement: 'topRight',
      duration: options.duration ?? (type === 'error' ? (options.actions?.length ? 12 : 5) : 3),
      className: `app-toast-notification ${type}`
    })
    }, [api])
  useEffect(() => { notify = show; return () => { notify = () => undefined } }, [show])
  return <ToastContext.Provider value={show}>{holder}{children}</ToastContext.Provider>
}

function titleFor(type: ToastType) {
  if (type === 'error') return 'Action needed'
  if (type === 'warning') return 'Please review'
  if (type === 'info') return 'Notice'
  return 'Success'
}
