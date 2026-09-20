// Keep the persisted legacy marker so existing schedules need no migration.
export const frevoInterviewDestination = 'Internal HRMS interview'
export const isFrevoInterview = (mode: string, destination: string) => mode === 'Virtual' && destination === frevoInterviewDestination
export const externalInterviewDestination = (destination: string) => destination === frevoInterviewDestination ? '' : destination
