const localDevelopmentApiPort = '5062'

export function resolveRuntimeApiBase(
  configuredBaseUrl?: string,
  runtimeOrigin?: string,
  development = false,
) {
  const configured = configuredBaseUrl?.trim()
  if (configured) {
    const cleanConfigured = configured.replace(/\/+$/, '')
    if (!development && runtimeOrigin && pointsToLoopback(cleanConfigured) && !pointsToLoopback(runtimeOrigin)) {
      try { return new URL(runtimeOrigin).origin } catch { return cleanConfigured }
    }
    return cleanConfigured
  }

  const fallback = development ? `http://localhost:${localDevelopmentApiPort}` : ''
  if (!runtimeOrigin) return fallback

  try {
    const runtime = new URL(runtimeOrigin)
    if (development) runtime.port = localDevelopmentApiPort
    return runtime.origin
  } catch {
    return fallback
  }
}

function pointsToLoopback(value: string) {
  try {
    const host = new URL(value).hostname.toLowerCase().replace(/^\[|\]$/g, '')
    return host === 'localhost' || host === '::1' || host === '0.0.0.0' || host.startsWith('127.')
  } catch {
    return false
  }
}
