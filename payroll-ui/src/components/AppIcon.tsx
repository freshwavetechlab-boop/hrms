export type IconName = 'apps' | 'bell' | 'check' | 'chevron' | 'close' | 'collapse' | 'expand' | 'settings' | 'employees' | 'payruns' | 'reports' | 'security' | 'calendar'

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
    calendar: 'M7 3v3m10-3v3M4 8h16M5 5h14a1 1 0 011 1v14H4V6a1 1 0 011-1zm3 7h3m3 0h3m-9 4h3m3 0h3'
  }
  return <svg className="ui-icon" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d={paths[name]} /></svg>
}
