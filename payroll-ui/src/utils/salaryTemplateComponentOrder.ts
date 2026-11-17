import type { StructureLine } from '../types/payroll'

export const SALARY_COMPONENT_DRAG_TYPE = 'application/x-frevo-salary-template-component'
export type SalaryComponentDrag = { version: 1; kind: 'library' | 'line'; id: string }
export type SalaryComponentDropAction = { type: 'add'; id: string } | { type: 'move'; sourceId: string; targetId: string }

export function salaryComponentDragPayload(kind: SalaryComponentDrag['kind'], id: string): string {
  return JSON.stringify({ version: 1, kind, id: String(id) })
}

/** Ignore unrelated/stale drags; a reorder can never be interpreted as an add. */
export function salaryComponentDropAction(payload: string, libraryIds: string[], lineIds: string[], targetId?: string): SalaryComponentDropAction | null {
  let value: unknown
  try { value = JSON.parse(payload) } catch { return null }
  if (!value || typeof value !== 'object') return null
  const drag = value as Partial<SalaryComponentDrag>
  if (drag.version !== 1 || typeof drag.id !== 'string' || !drag.id || drag.id.length > 256) return null
  const id = String(drag.id)
  if (drag.kind === 'library') return libraryIds.includes(id) && !lineIds.includes(id) ? { type: 'add', id } : null
  if (drag.kind !== 'line' || !targetId || id === targetId || !lineIds.includes(id) || !lineIds.includes(targetId)) return null
  return { type: 'move', sourceId: id, targetId }
}

/** Move to the original target index: downward movement lands after the target. */
export function moveSalaryTemplateLine(lines: StructureLine[], sourceId: string, targetId: string): StructureLine[] {
  const source = lines.findIndex(line => String(line.componentId) === sourceId)
  const target = lines.findIndex(line => String(line.componentId) === targetId)
  if (source < 0 || target < 0 || source === target || lines.filter(line => String(line.componentId) === sourceId).length !== 1 || lines.filter(line => String(line.componentId) === targetId).length !== 1) return lines
  const next = [...lines]
  const [line] = next.splice(source, 1)
  next.splice(target, 0, line)
  return next
}
