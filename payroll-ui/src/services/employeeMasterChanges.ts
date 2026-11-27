const eventName = 'frevo:employee-master-saved'

// Each listener owns a channel. A short-lived sender reaches other listeners
// in the same window as well as other same-origin tabs, without retaining data.
function openChannel() {
  try { return new BroadcastChannel(eventName) } catch { return null }
}

export function notifyEmployeeMasterSaved(clientId: number, testPayrollRemoved = false) {
  const change = testPayrollRemoved ? { clientId, testPayrollRemoved: true } : clientId
  const channel = openChannel()
  if (!channel) {
    window.dispatchEvent(new CustomEvent(eventName, { detail: change }))
    return
  }
  try { channel.postMessage(change) }
  catch { window.dispatchEvent(new CustomEvent(eventName, { detail: change })) }
  finally { channel.close() }
}

export function onEmployeeMasterSaved(clientId: number, refresh: () => void, filter?: 'testPayrollRemoved') {
  const channel = openChannel()
  const matches = (changed: unknown) => {
    const cleanup = changed !== null && typeof changed === 'object' && 'testPayrollRemoved' in changed && changed.testPayrollRemoved === true && 'clientId' in changed
    const id = cleanup ? changed.clientId : changed
    return (!filter || cleanup) && typeof id === 'number' && id > 0 && (!clientId || id === clientId)
  }
  if (channel) channel.onmessage = event => { if (matches(event.data)) refresh() }
  const onLocal = (event: Event) => { if (matches((event as CustomEvent).detail)) refresh() }
  window.addEventListener(eventName, onLocal)
  return () => {
    channel?.close()
    window.removeEventListener(eventName, onLocal)
  }
}
