import { acquirePunchLocation, browserDeviceId } from '../utils/attendancePunch'

// Device operations stay behind an ordinary module boundary so the permission,
// failure and camera lifecycle can be exercised independently of API requests.
export const getFreshPunchLocation = () => acquirePunchLocation()

export function getPunchDeviceDetails(scope: string) {
  const connection = (navigator as Navigator & { connection?: { effectiveType?: string; type?: string } }).connection
  return { deviceId: browserDeviceId(localStorage, scope), networkType: connection?.type || connection?.effectiveType || 'Browser', platform: navigator.platform || 'Browser' }
}

export async function openPunchCamera(mediaDevices: Pick<MediaDevices, 'getUserMedia'> | undefined = navigator.mediaDevices) {
  if (!mediaDevices?.getUserMedia) throw new Error('This browser cannot open the camera. Use the secure HTTPS portal in a browser with camera support.')
  return mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'user' }, width: { ideal: 640 }, height: { ideal: 480 } }, audio: false })
}

export function stopPunchCamera(mediaStream: Pick<MediaStream, 'getTracks'> | null) {
  mediaStream?.getTracks().forEach(track => { try { track.stop() } catch { /* Stop the remaining tracks even when a device has already disconnected. */ } })
}

export function capturePunchCameraFrame(video: HTMLVideoElement, createCanvas: () => HTMLCanvasElement = () => document.createElement('canvas')) {
  if (video.readyState < 2 || video.videoWidth < 1 || video.videoHeight < 1) throw new Error('Wait for the live camera picture before capturing a photo.')
  const canvas = createCanvas()
  canvas.width = Math.min(video.videoWidth, 640)
  canvas.height = Math.max(1, Math.round(video.videoHeight * canvas.width / video.videoWidth))
  const context = canvas.getContext('2d')
  if (!context) throw new Error('The camera picture could not be captured. Open the camera and try again.')
  context.drawImage(video, 0, 0, canvas.width, canvas.height)
  const frame = canvas.toDataURL('image/jpeg', 0.8)
  if (!frame.startsWith('data:image/jpeg;base64,') || frame.length < 100) throw new Error('The camera returned an empty picture. Open the camera and try again.')
  return frame
}
