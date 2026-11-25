type Reads<T extends object> = { [Key in keyof T]: () => Promise<T[Key]> }
type Publishers<T extends object> = { [Key in keyof T]: (value: T[Key]) => void }

/** Page-local reads only: publish fast sections without waiting for editor settings. */
export function createEmployeePageLoader<Directory extends object, References>(
  reads: Reads<Directory>, publish: Publishers<Directory>,
  references: () => Promise<References>, publishReferences: (value: References) => void,
) {
  let active = true
  const generations = new Map<keyof Directory, number>()
  let referenceRead: Promise<References | undefined> | undefined
  const load = async (keys = Object.keys(reads) as (keyof Directory)[]) => {
    if (!active) return
    await Promise.allSettled(keys.map(async key => {
      const token = (generations.get(key) ?? 0) + 1
      generations.set(key, token)
      const value = await reads[key]()
      if (active && token === generations.get(key)) publish[key](value)
    }))
  }
  const editor = () => {
    if (!active) return Promise.resolve(undefined)
    referenceRead ??= Promise.resolve().then(references).then(value => {
      if (!active) return undefined
      publishReferences(value)
      return value
    }).catch(error => { referenceRead = undefined; if (!active) return undefined; throw error })
    return referenceRead
  }
  return { load, editor, dispose: () => { active = false; generations.clear() } }
}
