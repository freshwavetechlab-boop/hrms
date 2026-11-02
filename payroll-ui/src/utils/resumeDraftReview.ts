import type { RecruitmentResumeReviewedDraft } from '../services/recruitmentTalentService'

export function retainResumeDrafts(drafts: RecruitmentResumeReviewedDraft[], files: File[]) {
  const byFile = new Map(drafts.map(draft => [draft.file, draft]))
  return files.flatMap(file => byFile.has(file) ? [byFile.get(file)!] : [])
}

export function fillMissingResumeNumbers(drafts: RecruitmentResumeReviewedDraft[]) {
  const fields = ['currentCtc', 'expectedCtc', 'noticePeriodDays'] as const
  let changed = 0
  const rows = drafts.map(draft => {
    const next = { ...draft }
    for (const field of fields) if (next[field] == null) { next[field] = 0; changed++ }
    return next
  })
  return { rows, changed }
}
