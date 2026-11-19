import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const code = ts.transpileModule(readFileSync(new URL('../src/hooks/useRecruitmentPreferences.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText

// A small hook scheduler exercises state/effect identity without browser or new
// dependencies. Bail-outs and dependency comparisons follow Object.is.
function harness(initialStorage = {}) {
  const storage = new Map(Object.entries(initialStorage))
  const slots = []
  let cursor = 0, dirty = false, pendingEffects = [], renders = 0
  const changed = (old, next) => !old || old.length !== next.length || next.some((value, index) => !Object.is(value, old[index]))
  const react = {
    createContext: value => ({ value }), useContext: context => context.value,
    useState: initial => {
      const index = cursor++
      if (!slots[index]) {
        const slot = { value: typeof initial === 'function' ? initial() : initial }
        slot.set = next => {
          const value = typeof next === 'function' ? next(slot.value) : next
          if (!Object.is(slot.value, value)) { slot.value = value; dirty = true }
        }
        slots[index] = slot
      }
      return [slots[index].value, slots[index].set]
    },
    useRef: initial => {
      const index = cursor++
      return slots[index] ??= { current: initial }
    },
    useCallback: (callback, deps) => {
      const index = cursor++
      if (!slots[index] || changed(slots[index].deps, deps)) slots[index] = { callback, deps }
      return slots[index].callback
    },
    useEffect: (effect, deps) => {
      const index = cursor++
      if (!slots[index] || changed(slots[index].deps, deps)) {
        slots[index] = { deps }
        pendingEffects.push(effect)
      }
    },
  }
  const sessionStorage = { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) }
  const hooks = {}
  new Function('exports', 'require', 'sessionStorage', code)(hooks, path => {
    assert.equal(path, 'react'); return react
  }, sessionStorage)
  return { hooks, react, storage, get renders() { return renders }, render(component) {
    let result, iterations = 0
    do {
      assert.ok(++iterations < 30, 'hook consumer must settle instead of exceeding update depth')
      dirty = false; cursor = 0; pendingEffects = []; renders++
      result = component(hooks, react)
      for (const effect of pendingEffects) effect()
    } while (dirty)
    return result
  } }
}

test('client persistence effect settles and preference setter stays stable across renders', () => {
  const h = harness()
  const consumer = (hooks, react) => {
    const [client, setClient] = hooks.useSessionPreference('client', 0)
    react.useEffect(() => { setClient(38) }, [setClient])
    return { client, setClient }
  }
  const first = h.render(consumer)
  assert.equal(first.client, 38)
  assert.equal(h.storage.get('client'), '38')
  const before = h.renders
  const next = h.render(consumer)
  assert.equal(first.setClient, next.setClient)
  assert.equal(h.renders, before + 1)
  next.setClient(38)
  const after = h.renders
  h.render(consumer)
  assert.equal(h.renders, after + 1, 'saving an unchanged value adds no update render')
})

test('array fallbacks do not recreate setter and functional updates retain no-op identity', () => {
  const h = harness()
  const consumer = hooks => hooks.useSessionPreference('selected', [])
  const first = h.render(consumer), next = h.render(consumer)
  assert.equal(first[1], next[1])
  assert.equal(first[0], next[0])
  next[1](current => [...current, 7])
  assert.deepEqual(h.render(consumer)[0], [7])
  const before = h.renders
  next[1](current => current)
  h.render(consumer)
  assert.equal(h.renders, before + 1)
})

test('changing preference keys restores each saved value and missing keys use their current fallback', () => {
  const h = harness({ first: '55', second: '20', invalid: '"wrong type"' })
  let key = 'first', fallback = 0
  const consumer = hooks => hooks.useSessionPreference(key, fallback)
  assert.equal(h.render(consumer)[0], 55)
  key = 'second'
  const second = h.render(consumer)
  assert.equal(second[0], 20)
  second[1](value => value + 1)
  assert.equal(h.render(consumer)[0], 21)
  key = 'first'
  assert.equal(h.render(consumer)[0], 55)
  h.storage.delete('second')
  key = 'second'; fallback = 9
  assert.equal(h.render(consumer)[0], 9)
  key = 'invalid'
  assert.equal(h.render(consumer)[0], 9)
  assert.equal(h.storage.get('first'), '55')
})
