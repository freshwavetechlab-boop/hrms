export type IconName = 'apps' | 'bell' | 'check' | 'chevron' | 'close' | 'collapse' | 'expand' | 'settings' | 'employees' | 'payruns' | 'reports' | 'security' | 'calendar' | 'dashboard' | 'tasks' | 'adjustments' | 'run' | 'history' | 'tax' | 'building' | 'location' | 'dropdown' | 'holiday' | 'template' | 'component' | 'workflow' | 'notification' | 'job' | 'org' | 'attendance' | 'billing' | 'money' | 'document' | 'shield' | 'user' | 'role'

export default function AppIcon({ name }: { name: IconName }) {
  const paths = {
    apps: 'M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h6v6h-6z',
    bell: 'M18 8a6 6 0 10-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4',
    check: 'M5 12l4 4L19 6',
    chevron: 'M9 18l6-6-6-6',
    close: 'M6 6l12 12M18 6L6 18',
    collapse: 'M14 5l-7 7 7 7M7 12h10',
    expand: 'M10 5l7 7-7 7M17 12H7',
    settings: 'M12 8a4 4 0 100 8 4 4 0 000-8zm0-5v2m0 14v2m9-9h-2M5 12H3m15.36-6.36l-1.42 1.42M6.06 17.94l-1.42 1.42m0-13.72l1.42 1.42m10.88 10.88l1.42 1.42',
    employees: 'M16 21v-2a4 4 0 00-4-4H6a4 4 0 00-4 4v2m17-10a4 4 0 10-4-4 4 4 0 004 4zM10 11a4 4 0 100-8 4 4 0 000 8z',
    payruns: 'M4 4h16v16H4zM8 8h8m-8 4h5m-5 4h8',
    reports: 'M5 3h10l4 4v14H5zM14 3v5h5M9 13h6m-6 4h6',
    security: 'M12 3l7 4v5c0 5-3.5 8-7 9-3.5-1-7-4-7-9V7l7-4zm0 8a2 2 0 100-4 2 2 0 000 4zm-3 6a3 3 0 016 0',
    calendar: 'M7 3v3m10-3v3M4 8h16M5 5h14a1 1 0 011 1v14H4V6a1 1 0 011-1zm3 7h3m3 0h3m-9 4h3m3 0h3',
    dashboard: 'M4 13h6V4H4zm10 7h6V4h-6zM4 20h6v-3H4z',
    tasks: 'M9 6h11M9 12h11M9 18h11M4 6l1 1 2-2M4 12l1 1 2-2M4 18l1 1 2-2',
    adjustments: 'M4 7h10M18 7h2M16 5v4M4 17h2M10 17h10M8 15v4M4 12h5M13 12h7M11 10v4',
    run: 'M5 4l14 8-14 8z',
    history: 'M3 12a9 9 0 109-9M3 4v6h6M12 7v5l3 2',
    tax: 'M5 4h14v16H5zM8 8h8M8 12h8M8 16h5',
    building: 'M4 21V5l8-3 8 3v16M9 21v-6h6v6M8 7h1m3 0h1m3 0h1M8 11h1m3 0h1m3 0h1',
    location: 'M12 21s7-5.5 7-11a7 7 0 10-14 0c0 5.5 7 11 7 11zm0-8a3 3 0 100-6 3 3 0 000 6z',
    dropdown: 'M4 6h16M4 12h10M4 18h16M17 10l3 3 3-3',
    holiday: 'M3 20h18M6 20l3-9 3 9M12 20l3-9 3 9M7 8h10M9 4h6',
    template: 'M5 3h14v18H5zM8 7h8M8 11h8M8 15h4',
    component: 'M8 8h8v8H8zM3 3h6v6H3zm12 0h6v6h-6zM3 15h6v6H3zm12 0h6v6h-6z',
    workflow: 'M6 6h5v5H6zm7 7h5v5h-5zM11 8h3a4 4 0 014 4v1M13 16h-3a4 4 0 01-4-4v-1',
    notification: 'M18 8a6 6 0 10-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4',
    job: 'M9 3h6v4H9zM5 7h14v14H5zM9 12h6M9 16h6',
    org: 'M12 4v4M7 12h10M7 12v4m10-4v4M4 16h6v4H4zm10 0h6v4h-6zM9 4h6v4H9z',
    attendance: 'M12 8v5l3 2M21 12a9 9 0 11-18 0 9 9 0 0118 0z',
    billing: 'M5 3h14v18H5zM8 7h8M8 11h8M8 15h3m3 0h2',
    money: 'M4 7h16v10H4zM8 12h.01M16 12h.01M12 9a3 3 0 100 6 3 3 0 000-6z',
    document: 'M6 3h9l3 3v15H6zM15 3v4h4M9 12h6M9 16h6',
    shield: 'M12 3l7 4v5c0 5-3.5 8-7 9-3.5-1-7-4-7-9V7z',
    user: 'M12 12a4 4 0 100-8 4 4 0 000 8zM4 21a8 8 0 0116 0',
    role: 'M8 7a4 4 0 118 0 4 4 0 01-8 0zM5 21a7 7 0 0114 0M17 14l2 2 4-4'
  }
  return <svg className="ui-icon" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d={paths[name]} /></svg>
}
