export type InstallPromptEvent = Event & {
  prompt: () => Promise<void>
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>
}

export type InstallSnapshot = { installed: boolean; canPrompt: boolean; pending: boolean; accepted: boolean }
export type InstallOutcome = 'accepted' | 'dismissed' | 'unavailable' | 'failed'
export type InstallPlatform = 'ios-safari' | 'ios-browser' | 'android' | 'mac-safari' | 'desktop'

export function installPlatform(navigator: { userAgent: string; platform?: string; maxTouchPoints?: number }): InstallPlatform {
  const agent = navigator.userAgent
  const ios = /iPhone|iPad|iPod/i.test(agent) || (navigator.platform === 'MacIntel' && (navigator.maxTouchPoints ?? 0) > 1)
  const safari = /Safari/i.test(agent) && !/Chrome|Chromium|CriOS|Edg|FxiOS|OPiOS|Android/i.test(agent)
  if (ios) return safari ? 'ios-safari' : 'ios-browser'
  if (/Android/i.test(agent)) return 'android'
  if (/Macintosh|Mac OS X/i.test(agent) && safari) return 'mac-safari'
  return 'desktop'
}

// The browser event is single-use. Keep it in memory across the login -> workspace
// transition, without storing an event, employee details, or authentication data.
export function createInstallController(initiallyInstalled = false) {
  let state: InstallSnapshot = { installed: initiallyInstalled, canPrompt: false, pending: false, accepted: false }
  let deferred: InstallPromptEvent | null = null
  const listeners = new Set<() => void>()
  const update = (change: Partial<InstallSnapshot>) => {
    state = { ...state, ...change }
    listeners.forEach(listener => listener())
  }
  return {
    getSnapshot: () => state,
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener) } },
    capture: (event: InstallPromptEvent) => {
      if (state.installed) return
      event.preventDefault()
      deferred = event
      update({ canPrompt: true, accepted: false })
    },
    setInstalled: (installed: boolean) => {
      if (installed) deferred = null
      update({ installed, ...(installed ? { canPrompt: false, pending: false, accepted: false } : {}) })
    },
    requestInstall: async (): Promise<InstallOutcome> => {
      if (!deferred || state.installed || state.pending) return 'unavailable'
      const event = deferred
      deferred = null
      update({ canPrompt: false, pending: true, accepted: false })
      try {
        await event.prompt()
        const { outcome } = await event.userChoice
        // Accepting the prompt is not proof that installation completed.
        update({ pending: false, accepted: outcome === 'accepted' && !state.installed })
        return outcome
      } catch {
        update({ pending: false })
        return 'failed'
      }
    },
  }
}

export type InstallController = ReturnType<typeof createInstallController>

export function connectInstallEvents(controller: InstallController, browser: Window, navigator: Navigator & { standalone?: boolean }) {
  const media = browser.matchMedia('(display-mode: standalone)')
  const refresh = () => { if (media.matches || navigator.standalone === true) controller.setInstalled(true) }
  const capture = (event: Event) => controller.capture(event as InstallPromptEvent)
  const installed = () => controller.setInstalled(true)
  refresh()
  browser.addEventListener('beforeinstallprompt', capture)
  browser.addEventListener('appinstalled', installed)
  browser.addEventListener('pageshow', refresh)
  media.addEventListener?.('change', refresh)
  return () => {
    browser.removeEventListener('beforeinstallprompt', capture)
    browser.removeEventListener('appinstalled', installed)
    browser.removeEventListener('pageshow', refresh)
    media.removeEventListener?.('change', refresh)
  }
}
