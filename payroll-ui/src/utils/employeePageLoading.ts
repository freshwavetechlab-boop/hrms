type Reads<T extends object> = { [Key in keyof T]: () => Promise<T[Key]> }
type Publishers<T extends object> = { [Key in keyof T]: (value: T[Key]) => void }

/** Page-local reads only: publish fast sections without waiting for editor settings. */
export function createEmployeePageLoader<Directory extends object, References>(
  reads: Reads<Directory>, publish: Publishers<Directory>,
  references: () => Promise<References>, publishReferences: (value: References) => void,
) {
  let active = true, generation = 0
  let referenceRead: Promise<References | undefined> | undefined
  const load = async () => {
    if (!active) return
    const token = ++generation
    await Promise.allSettled((Object.keys(reads) as (keyof Directory)[]).map(async key => {
      const value = await reads[key]()
      if (active && token === generation) publish[key](value)
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
  return { load, editor, dispose: () => { active = false; generation++ } }
}
