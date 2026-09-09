import type { NotificationAutomationCatalog, NotificationRule, NotificationSetup, NotificationSmtpSetting, NotificationStakeholderPreview, NotificationTemplate } from '../types/payroll'
import { getJson, postEmpty, postJson } from './apiClient'

export const getNotificationSetup = () => getJson<NotificationSetup>('/api/notifications/setup', { smtp: { id: 1, isEnabled: false, deliveryPaused: false, host: '', port: 587, userName: '', password: '', enableSsl: true, fromEmail: '', fromName: '' }, templates: [], rules: [], queue: [], logs: [] })
export const saveNotificationSmtp = (smtp: NotificationSmtpSetting) => postJson('/api/notifications/smtp', smtp, smtp, { successMessage: 'SMTP settings saved.' })
export const saveNotificationTemplate = (template: NotificationTemplate) => postJson('/api/notifications/templates', template, template, { successMessage: 'Email template saved.' })
export const saveNotificationRule = (rule: NotificationRule) => postJson('/api/notifications/rules', rule, rule, { successMessage: 'Notification rule saved.' })
export const retryNotification = (id: number) => postEmpty(`/api/notifications/queue/${id}/retry`, null, { successMessage: 'Notification queued for retry.' })
export const sendNotificationTest = (ruleId: number, toEmail: string) => postJson('/api/notifications/test', { ruleId, toEmail }, null, { successMessage: 'Test notification queued.' })
export const getNotificationAutomationCatalog = () => getJson<NotificationAutomationCatalog>('/api/notifications/automation/catalog', { events: [] })
export const previewNotificationStakeholders = (request: { eventCode: string; resourceType: string; resourceId: string; clientId?: number | null }) => postJson<typeof request, NotificationStakeholderPreview>('/api/notifications/automation/stakeholders', request, { eventCode: request.eventCode, resourceType: request.resourceType, resourceId: request.resourceId, clientId: request.clientId, stakeholders: [] }, { loader: false, toast: 'error-only' })
