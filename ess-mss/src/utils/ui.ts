import type { View } from '../types'

export function initials(name: string) {
  return name.split(/\s+/).map(part => part[0]).join('').slice(0, 2).toUpperCase()
}

export function viewLabel(value: View) {
  return value === 'My Profile' ? 'My profile' : value
}

export function statusClass(status: string) {
  const value = status.toLowerCase()
  if (value.includes('approved')) return 'approved'
  if (value.includes('reject')) return 'rejected'
  if (value.includes('sent back')) return 'sent-back'
  if (value.includes('pending')) return 'pending'
  return 'neutral'
}

export function showToast(text: string, kind: 'success' | 'error') {
  const toast = document.createElement('div')
  toast.className = `ess-toast ${kind}`
  toast.textContent = text
  document.body.appendChild(toast)
  window.setTimeout(() => toast.classList.add('hide'), 2600)
  window.setTimeout(() => toast.remove(), 3200)
}
