export function safeJsonObject<T extends Record<string, unknown>>(value: string | null | undefined, fallback: T): T {
  if (!value?.trim()) return fallback
  try {
    const parsed: unknown = JSON.parse(value)
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? { ...fallback, ...(parsed as Partial<T>) } : fallback
  } catch {
    return fallback
  }
}

export function safeJsonRecord(value: string | null | undefined) {
  return safeJsonObject<Record<string, string>>(value, {})
}
