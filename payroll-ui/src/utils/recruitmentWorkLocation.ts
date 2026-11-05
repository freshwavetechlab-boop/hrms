export type WorkLocationIdentity = { id: number; name: string; city?: string }

export function recruitmentWorkLocationLabel(location: WorkLocationIdentity) {
  const city = location.city?.trim()
  return city && !location.name.toLowerCase().includes(city.toLowerCase()) ? `${location.name} · ${city}` : location.name
}

// Posting identity is the existing Work Location ID; matching city/name text
// never makes one institute equivalent to another location in that city.
export function matchesRecruitmentWorkLocation(workLocationId: number | null | undefined, selectedId: number) {
  return selectedId <= 0 || Number(workLocationId || 0) === selectedId
}
