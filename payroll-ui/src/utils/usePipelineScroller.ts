import { useCallback, useEffect, useRef, type KeyboardEvent as ReactKeyboardEvent } from 'react'

type Options = {
  rootScrollsVertically?: boolean
}

const canScrollVertically = (element: HTMLElement, delta: number) => {
  if (element.scrollHeight <= element.clientHeight + 1) return false
  if (delta < 0) return element.scrollTop > 0
  return element.scrollTop + element.clientHeight < element.scrollHeight - 1
}

const nearestVerticalScroller = (target: EventTarget | null, root: HTMLElement) => {
  let element = target instanceof HTMLElement ? target : null
  while (element && element !== root) {
    const overflowY = window.getComputedStyle(element).overflowY
    if ((overflowY === 'auto' || overflowY === 'scroll') && element.scrollHeight > element.clientHeight + 1) return element
    element = element.parentElement
  }
  return null
}

/**
 * Keeps nested pipeline lanes usable with a mouse, touchpad and keyboard.
 * Vertical gestures stay with a scrollable lane; horizontal gestures move the
 * board. A mouse wheel over a lane header/gutter moves a wide board sideways.
 */
export function usePipelineScroller<T extends HTMLElement>({ rootScrollsVertically = false }: Options = {}) {
  const ref = useRef<T>(null)

  useEffect(() => {
    const element = ref.current
    if (!element) return

    const onWheel = (event: WheelEvent) => {
      const horizontalDelta = Math.abs(event.deltaX) > 0.5 ? event.deltaX : 0
      if (horizontalDelta) {
        const before = element.scrollLeft
        element.scrollLeft += horizontalDelta
        if (element.scrollLeft !== before) event.preventDefault()
        return
      }

      const verticalDelta = event.deltaY
      if (!verticalDelta) return

      const laneScroller = nearestVerticalScroller(event.target, element)
      if (!event.shiftKey && laneScroller) return
      if (!event.shiftKey && rootScrollsVertically && canScrollVertically(element, verticalDelta)) return
      if (element.scrollWidth <= element.clientWidth + 1) return

      const before = element.scrollLeft
      element.scrollLeft += verticalDelta
      if (element.scrollLeft !== before) event.preventDefault()
    }

    element.addEventListener('wheel', onWheel, { passive: false })
    return () => element.removeEventListener('wheel', onWheel)
  }, [rootScrollsVertically])

  const onKeyDown = useCallback((event: ReactKeyboardEvent<T>) => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return
    const element = ref.current
    if (!element || element.scrollWidth <= element.clientWidth + 1) return
    event.preventDefault()
    element.scrollBy({ left: event.key === 'ArrowLeft' ? -280 : 280, behavior: 'smooth' })
  }, [])

  return { ref, onKeyDown }
}
