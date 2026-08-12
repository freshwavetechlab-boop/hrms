export type RecruitmentPipelineDisplayMode = 'pipeline' | 'table' | 'both' | 'flow'

export const recruitmentPipelineDisplayOptions: Array<{ value: RecruitmentPipelineDisplayMode; label: string }> = [
  { value: 'pipeline', label: 'Pipeline view' },
  { value: 'table', label: 'Table view' },
  { value: 'flow', label: 'Flow view' },
  { value: 'both', label: 'Both views' },
]

export const recruitmentPipelineDisplayStorageKey = 'recruitment.pipeline.view'
