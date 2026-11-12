import type { User } from '../types'

export type PunchAction = 'CheckIn' | 'CheckOut'
export type PunchLocation = { latitude: number; longitude: number; accuracyMeters: number; capturedAt: string }
export type AttendanceToday = { attendanceDate: string; status: string; checkInTime?: string | null; checkOutTime?: string | null; nextExpectedAction: PunchAction | 'Completed' | 'Unavailable' | 'WaitForApproval'; approvalPending: boolean }
export type SavedAttendancePunch = { id: number; action: PunchAction; capturedAt: string; latitude: number; longitude: number; accuracyMeters: number; validationStatus: string; decision: string }
export type BrowserPunchRequest = PunchLocation & { clientRequestId: string; action: PunchAction; deviceId: string; networkType: string; appVersion: string; cameraCaptureConfirmed: boolean; biometricConfirmed: false; reason: string; confirmationPassword: string; deviceModel: string; osVersion: string }
export type BrowserPunchResult = { allowed: boolean; punchRecorded: boolean; status: string; message: string; nextExpectedAction?: string; requiresReason: boolean; punchId?: number | null; attendanceDate?: string | null; idempotentReplay?: boolean; retryAfterSeconds?: number; confirmationMethod?: string }

export const freshGpsOptions: PositionOptions = { enableHighAccuracy: true, maximumAge: 0, timeout: 20000 }

export function canMarkAttendance(user: Pick<User, 'employeeId' | 'permissions' | 'mustChangePassword'>) {
  const permissions = user.permissions.map(permission => permission.toLowerCase())
  return Number(user.employeeId) > 0 && !user.mustChangePassword && permissions.includes('ess.self') && permissions.includes('ess.attendance.mark')
}

export function locationFromPosition(position: Pick<GeolocationPosition, 'coords' | 'timestamp'>, now = Date.now()): PunchLocation {
  const { latitude, longitude, accuracy } = position.coords
  if (![latitude, longitude, accuracy, position.timestamp].every(Number.isFinite) || latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180 || (latitude === 0 && longitude === 0))
    throw new Error('The device did not return a valid GPS location. Turn on location services and try again.')
  if (accuracy < 0 || accuracy > 100) throw new Error(`GPS accuracy is ${Math.ceil(accuracy)} m. Enable precise location, move near a window or outdoors, then retry. A reading within 100 m is required.`)
  if (now - position.timestamp > 60000 || position.timestamp - now > 60000) throw new Error('The device returned an old GPS reading. Refresh location and try again.')
  return { latitude, longitude, accuracyMeters: Math.ceil(accuracy), capturedAt: new Date(now).toISOString() }
}

export function gpsErrorMessage(error: Pick<GeolocationPositionError, 'code'>) {
  if (error.code === 1) return 'Location permission was denied. Allow precise location for this site in browser or phone settings, then retry.'
  if (error.code === 3) return 'GPS timed out. Turn on location services, move near a window or outdoors, then retry. Laptops may not provide a precise reading.'
  return 'The device could not obtain GPS. Turn on location services and precise location, then retry. Laptops may not provide a precise reading.'
}

export function acquirePunchLocation(geolocation: Pick<Geolocation, 'getCurrentPosition'> | undefined = navigator.geolocation): Promise<PunchLocation> {
  if (!geolocation) return Promise.reject(new Error('This browser cannot access location. Open the secure HTTPS portal in a supported browser and enable location services.'))
  return new Promise((resolve, reject) => geolocation.getCurrentPosition(position => {
    try { resolve(locationFromPosition(position)) } catch (error) { reject(error) }
  }, error => reject(new Error(gpsErrorMessage(error))), freshGpsOptions))
}

export function cameraErrorMessage(error: unknown) {
  const name = error instanceof Error ? error.name : ''
  if (name === 'NotAllowedError' || name === 'PermissionDeniedError') return 'Camera permission was denied. Allow camera access for this site in browser or phone settings, then open the camera again.'
  if (name === 'NotFoundError' || name === 'DevicesNotFoundError') return 'No camera is available on this device. Use a phone or computer with a working camera.'
  return 'The camera could not start. Close other apps using it, check camera permission, and try again. Use the secure HTTPS portal.'
}

export function buildBrowserPunch(input: { requestId: string; action: PunchAction; location: PunchLocation; deviceId: string; cameraFrameCaptured: boolean; password: string; reason: string; networkType?: string; platform?: string }): BrowserPunchRequest {
  if (!input.requestId || !input.deviceId) throw new Error('A device and request identifier are required. Close the punch window and retry.')
  if (!input.cameraFrameCaptured) throw new Error('Capture a photo with the camera before confirming attendance.')
  if (!input.password) throw new Error('Enter your ESS password to confirm attendance.')
  if (!validMapLocation(input.location) || !Number.isFinite(input.location.accuracyMeters) || input.location.accuracyMeters < 0 || input.location.accuracyMeters > 100 || !Number.isFinite(new Date(input.location.capturedAt).getTime())) throw new Error('Refresh the precise GPS reading before confirming attendance.')
  return { ...input.location, clientRequestId: input.requestId, action: input.action, deviceId: input.deviceId, networkType: input.networkType?.slice(0, 40) || 'Browser', appVersion: 'ess-web/1.0', cameraCaptureConfirmed: true, biometricConfirmed: false, reason: input.reason.trim(), confirmationPassword: input.password, deviceModel: 'Browser / PWA', osVersion: input.platform?.slice(0, 80) || 'Browser' }
}

export function browserDeviceId(storage: Pick<Storage, 'getItem' | 'setItem'>, scope: string, randomId: () => string = () => crypto.randomUUID()) {
  const key = `ess.attendance.device:${scope}`
  const saved = storage.getItem(key)
  if (saved && /^web-[a-zA-Z0-9-]{8,100}$/.test(saved)) return saved
  const id = `web-${randomId()}`
  storage.setItem(key, id)
  return id
}

export const mapsLocationUrl = (location: Pick<PunchLocation, 'latitude' | 'longitude'>) => `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(`${location.latitude},${location.longitude}`)}`
export const validMapLocation = (location: Pick<PunchLocation, 'latitude' | 'longitude'>) => Number.isFinite(location.latitude) && Number.isFinite(location.longitude) && location.latitude >= -90 && location.latitude <= 90 && location.longitude >= -180 && location.longitude <= 180 && !(location.latitude === 0 && location.longitude === 0)

export function resultNeedsReason(result: BrowserPunchResult) { return result.requiresReason || result.status === 'ReasonRequired' || result.status === 'OutsideFenceReasonRequired' }

export function attendanceClock(value: string) {
  const withZone = /(?:Z|[+-]\d{2}:\d{2})$/i.test(value) ? value : `${value}+05:30`
  const date = new Date(withZone)
  return Number.isFinite(date.getTime()) ? new Intl.DateTimeFormat('en-IN', { timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit' }).format(date) : value
}
