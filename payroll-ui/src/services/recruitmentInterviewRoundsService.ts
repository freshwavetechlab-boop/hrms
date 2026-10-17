import { getJsonResult, postJson } from './apiClient'
import type { InterviewRoundDefinition, InterviewRoundPlan } from '../types/recruitmentInterviewRounds'

const route = '/api/recruitment/interviews/rounds'
export const getInterviewRounds = (applicationId: number) => getJsonResult<InterviewRoundPlan | null>(`${route}/${applicationId}`, null, { toast: false, loader: false })
export const saveInterviewRounds = (applicationId: number, expectedRevision: number, rounds: InterviewRoundDefinition[]) => postJson(`${route}/${applicationId}`, { expectedRevision, rounds }, null as InterviewRoundPlan | null, { toast: false })
