export type InternalInterviewMode = 'Human' | 'AI' | 'Hybrid'
export type InternalInterviewConfiguration = {
  mode: InternalInterviewMode; language: string; difficulty: string; maxQuestions: number; answerSeconds: number;
  followUpLimit: number; recordingEnabled: boolean; transcriptionEnabled: boolean; questionIds: number[];
}
export type InterviewQuestion = {
  id: number; clientId: number; positionId: number; skill: string; difficulty: string; language: string;
  question: string; evaluationCriteria: string; followUpInstructions: string; isActive: boolean;
}
export type InternalInterviewContext = {
  interviewId: number; applicationId: number; clientId: number; positionId: number; candidateName: string;
  positionTitle: string; roundCode: string; interviewStatus: string; result: string;
  timeZoneId: string; scheduledStart: string; scheduledEnd: string; panelUserIds: number[];
}
export type InternalInterviewView = {
  interviewId: number; applicationId: number; candidateName: string; positionTitle: string; roundCode: string;
  status: string; control: string; revision: number; startedAtUtc?: string | null; endedAtUtc?: string | null;
  consentAtUtc?: string | null; recordingConsent: boolean; transcriptionConsent: boolean;
  mediaState?: string; mediaError?: string; retainUntilUtc?: string; draining?: boolean; speechEnabled?: boolean;
  scheduledStart: string; scheduledEnd: string; timeZoneId: string; noticeVersion: string;
  configuration: InternalInterviewConfiguration; questions?: InterviewQuestion[] | null; canManage: boolean; canResetSchedule?: boolean; isCandidate: boolean;
}
export type InternalInterviewEvent = {
  id: number; interviewId: number; eventKey: string; kind: string; actor: string; source: string;
  text: string; questionEventId?: number | null; offsetMs?: number | null; createdAtUtc: string;
}
export type InterviewMediaGrant = { url: string; token: string; identity: string; expiresInSeconds: number }
