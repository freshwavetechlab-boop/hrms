import { useState, useSyncExternalStore } from 'react'
import { Modal } from 'antd'
import { connectInstallEvents, createInstallController, installPlatform } from '../utils/pwaInstall'
import './PwaInstallControl.css'

const controller = createInstallController()
if (typeof window !== 'undefined' && typeof navigator !== 'undefined') connectInstallEvents(controller, window, navigator)
const dismissalKey = 'ess.pwa.install.card-dismissed'
const serverSnapshot = controller.getSnapshot()

function cardDismissed() {
  try { return sessionStorage.getItem(dismissalKey) === '1' } catch { return false }
}

export function PwaInstallControl({ variant = 'compact' }: { variant?: 'compact' | 'card' }) {
  const state = useSyncExternalStore(controller.subscribe, controller.getSnapshot, () => serverSnapshot)
  const [open, setOpen] = useState(false)
  const [dismissed, setDismissed] = useState(cardDismissed)
  const [promptFailed, setPromptFailed] = useState(false)
  const platform = typeof navigator === 'undefined' ? 'desktop' : installPlatform(navigator)

  if (state.installed || (variant === 'card' && dismissed)) return null
  const install = async () => {
    setPromptFailed(false)
    if (state.canPrompt) {
      const result = await controller.requestInstall()
      if (result !== 'failed' && result !== 'unavailable') return
      setPromptFailed(result === 'failed')
    }
    setOpen(true)
  }
  const dismiss = () => {
    try { sessionStorage.setItem(dismissalKey, '1') } catch { /* Private browsing can disable storage. */ }
    setDismissed(true)
  }

  return <>
    {variant === 'card' ? <section className="ess-pwa-card" aria-label="Install employee app">
      <span className="ess-pwa-app-icon" aria-hidden="true"><img src="/favicon.svg" alt="" /></span>
      <div className="ess-pwa-card-copy"><b>Your workspace, one tap away</b><span>Install the app for attendance, leave and payslips.</span></div>
      <button type="button" className="ess-pwa-install" onClick={() => void install()} disabled={state.pending}><InstallIcon /><span>{state.pending ? 'Opening…' : 'Install Android app'}</span></button>
      <button type="button" className="ess-pwa-dismiss" onClick={dismiss} aria-label="Dismiss install suggestion for this session" title="Not now">×</button>
    </section> : <button type="button" className="ess-pwa-install ess-pwa-install-compact" onClick={() => void install()} disabled={state.pending} title="Install employee app" aria-label={state.pending ? 'Opening app installation' : 'Install employee app'}><InstallIcon /><span>{state.pending ? 'Opening…' : 'Install app'}</span></button>}
    <Modal open={open} onCancel={() => setOpen(false)} title="Install Frevo One HR" width={460} footer={<button type="button" className="ess-pwa-close" onClick={() => setOpen(false)}>Got it</button>} className="ess-pwa-modal">
      <div className="ess-pwa-guide-brand"><img src="/favicon.svg" alt="" /><div><b>Frevo One HR</b><span>Your employee workspace</span></div></div>
      {promptFailed && <p className="ess-pwa-guide-note" role="status">The browser could not open installation. You can use its menu instead.</p>}
      {state.accepted && <p className="ess-pwa-guide-note" role="status">Installation was requested. Follow your browser’s confirmation, then open the app from your device.</p>}
      {platform === 'ios-browser' && <p>Open this page in <strong>Safari</strong>, then follow these steps.</p>}
      <ol className="ess-pwa-steps">
        {(platform === 'ios-safari' || platform === 'ios-browser') ? <><li><span>1</span><div>Tap <strong>Share</strong> <ShareIcon /> in Safari.</div></li><li><span>2</span><div>Choose <strong>Add to Home Screen</strong>.<small>Scroll down in the Share menu if needed.</small></div></li><li><span>3</span><div>Tap <strong>Add</strong>, then open Frevo One HR from your Home Screen.</div></li></> : platform === 'mac-safari' ? <><li><span>1</span><div>Open Safari’s <strong>File</strong> menu.</div></li><li><span>2</span><div>Choose <strong>Add to Dock</strong>, then <strong>Add</strong>.<small>If this option is unavailable, open the portal in Chrome or Edge.</small></div></li></> : <><li><span>1</span><div>Open your browser’s <strong>{platform === 'android' ? '⋮ menu' : 'menu'}</strong>.</div></li><li><span>2</span><div>Choose <strong>Install app</strong>{platform === 'android' ? <> or <strong>Add to Home screen</strong></> : <> or <strong>Install this site as an app</strong></>}.</div></li><li><span>3</span><div>Confirm <strong>Install</strong> and open the app from your {platform === 'android' ? 'Home Screen' : 'desktop or app launcher'}.<small>If installation is unavailable, use Chrome or Edge, or continue in your browser.</small></div></li></>}
      </ol>
      <p className="ess-pwa-online-note">An internet connection is needed to sign in and record attendance. Your usual login stays the same.</p>
    </Modal>
  </>
}

function InstallIcon() {
  return <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M12 3v11m-4-4 4 4 4-4" /><path d="M4 15v4a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-4" /></svg>
}

function ShareIcon() {
  return <svg className="ess-pwa-share-icon" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M12 16V3m-4 4 4-4 4 4" /><path d="M7 10H5v11h14V10h-2" /></svg>
}
