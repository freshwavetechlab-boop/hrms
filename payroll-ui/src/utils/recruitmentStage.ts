export function recruitmentStageColor(type = '', name = '') {
  const stageType = type.trim().toLowerCase()
  if (stageType === 'ats') return 'blue'
  if (stageType === 'screening') return 'purple'
  if (stageType === 'externalform') return 'geekblue'
  if (stageType === 'documents') return 'cyan'
  if (stageType === 'interview') return 'gold'
  if (stageType === 'hr') return 'lime'
  if (stageType === 'approval') return 'orange'
  if (stageType === 'offer') return 'magenta'
  if (stageType === 'preonboarding') return '#0f766e'
  if (stageType === 'joining' || stageType === 'completed') return 'green'
  if (stageType === 'rejected') return 'red'
  if (stageType === 'withdrawn' || stageType === 'position') return 'volcano'
  if (/reject|cancel|withdraw/i.test(name)) return 'red'
  if (/join|complete|close/i.test(name)) return 'green'
  return 'default'
}
