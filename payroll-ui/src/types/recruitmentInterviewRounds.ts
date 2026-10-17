export type InterviewRoundDefinition = { name: string; interviewType: string }
export type InterviewRoundState = InterviewRoundDefinition & { interviewId?: number | null; status: string; result: string; passed: boolean; canSchedule: boolean }
export type InterviewRoundPlan = { applicationId: number; positionId: number; stageName: string; revision: number; isConfigured: boolean; canConfigure: boolean; complete: boolean; completedCount: number; rounds: InterviewRoundState[] }
