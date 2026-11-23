type SourceFileIdentity = Pick<File, 'name' | 'size' | 'lastModified'>

// Loading a generated, not-yet-persisted JD is not a user edit. An uncertain
// write is attempted automatically only once for the same content/file.
export function embeddedJdAutosaveKey(snapshot: string, baseline: string, sourceFile: SourceFileIdentity | null, attempted: string) {
  if (!sourceFile && snapshot === baseline) return ''
  const key = JSON.stringify([snapshot, sourceFile ? [sourceFile.name, sourceFile.size, sourceFile.lastModified] : null])
  return key === attempted ? '' : key
}
