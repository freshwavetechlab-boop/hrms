# ESS browser and PWA attendance

The employee Attendance page supports the browser punch flow on phones and computers. It uses the existing employee mapping, attendance permissions, shift resolution, payroll locks and geo-fence rules.

## Employee flow

1. Sign in to ESS and choose **Mark attendance** on the dashboard, or open **Time & leave → Attendance calendar**.
2. Select **Punch In** or **Punch Out**, as offered by the current server attendance state.
3. Allow location and camera access. Capture the camera frame and confirm using the current ESS password.
4. Submit the punch while online. Review the result and the saved location link.

Location is requested for the punch, not continuously in the background. A geo-fence is optional: with no applicable fence, the server records valid coordinates with `NoGeoFenceConfigured`. If a fence exists, its inside/outside and reason rules remain in force. GPS accuracy must meet the existing limit of 100 metres. A coarse computer location is not silently accepted as a precise phone location.

Browser confirmation is explicitly separate from the Android device biometric confirmation. The server verifies the current employee account password; the web client does not claim that a biometric check occurred. Passwords are not saved in browser preferences, punch rows or audit details. The camera frame is a local capture preview, not a retained face-verification record.

## Connectivity and devices

- Production must use HTTPS for browser location and camera access. Localhost is suitable for development.
- The employee must allow the browser/PWA location and camera permissions. Phone location services must be enabled.
- An installed PWA uses the same attendance page and API. Punch submissions require a live connection; API responses and POST requests are not cached for offline replay.
- After an uncertain network result, retry the same pending punch rather than creating another request. The server preserves the first accepted request and its captured coordinates.

## Install the employee app

- Select **Install app** on the ESS sign-in page or in the workspace header. Supported Chrome/Edge browsers open their native installation prompt when available; otherwise the button shows browser-menu instructions.
- On iPhone/iPad, open ESS in Safari, select **Share → Add to Home Screen → Add**. Installation requires the employee's confirmation; the portal does not automatically download an APK or install itself.
- The install control is hidden in the installed app. Dismissing the sign-in suggestion hides that card for the browser session. Sign-in and attendance still require the internet, and use the same account and API.
- PWA updates wait for current app windows to close, so an update does not replace the worker during a punch submission. Reopen the app to pick up an update.

## Deployment and verification

Deploy the API and the **ess-mss** frontend together. The admin payroll frontend is not the employee punch application. No geo-fence setup is required solely to capture the location of a punch.

Repository and isolated database tests use synthetic employees and coordinates. Browser tests using simulated device inputs verify the UI and API flow; they do not establish real iPhone/Android GPS accuracy or physical camera behavior. Those checks require the relevant device and its permission prompts.

The attendance workspace shows the current India time, server punch status, check-in/out times, and saved location links. First-time phone users start in Card view; an existing view preference is preserved. History scrolls independently in ordinary windows. Short windows and expanded location details can scroll the workspace so no controls are clipped. Mobile card filters open in a drawer.

Responsive verification on 13 November 2026 covered approximately 1366×768, 768×1024, 390×844 and 320×569 CSS pixels, including the expanded/collapsed sidebar, 30 attendance rows, the final history action, punch confirmation, login/install instructions, dashboard/calendar, profile, leave and payslip preview. Two synthetic browser punches persisted through the real repository with camera confirmation, saved GPS and no biometric claim. The 23 frontend checks, 36 isolated database checks and production frontend build passed. Actual device installation remains a separate hardware check.
