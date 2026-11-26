const eventName = 'frevo:employee-master-saved'

// Each listener owns a channel. A short-lived sender reaches other listeners
// in the same window as well as other same-origin tabs, without retaining data.
function openChannel() {
  try { return new BroadcastChannel(eventName) } catch { return null }
}

export function notifyEmployeeMasterSaved(clientId: number) {
  const channel = openChannel()
  if (!channel) {
    window.dispatchEvent(new CustomEvent(eventName, { detail: clientId }))
    return
  }
  try { channel.postMessage(clientId) }
  catch { window.dispatchEvent(new CustomEvent(eventName, { detail: clientId })) }
  finally { channel.close() }
}

export function onEmployeeMasterSaved(clientId: number, refresh: () => void) {
  const channel = openChannel()
  if (channel) channel.onmessage = event => { if (event.data === clientId) refresh() }
  const onLocal = (event: Event) => { if ((event as CustomEvent).detail === clientId) refresh() }
  window.addEventListener(eventName, onLocal)
  return () => {
    channel?.close()
    window.removeEventListener(eventName, onLocal)
  }
}
