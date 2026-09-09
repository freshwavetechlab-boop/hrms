import type { ThemeConfig } from 'antd'

/**
 * The visual contract for every Ant Design primitive in Frevo HRMS.
 * Page components should consume these tokens instead of restyling Ant
 * internals with page-specific selectors.
 */
const hrmsTheme: ThemeConfig = {
  token: {
    colorPrimary: '#0b6fad',
    colorInfo: '#0b6fad',
    colorSuccess: '#16845b',
    colorWarning: '#c77912',
    colorError: '#d14343',
    colorText: '#172136',
    colorTextSecondary: '#667085',
    colorBgLayout: '#f5f8fc',
    colorBgContainer: '#ffffff',
    colorBorder: '#dbe3ec',
    colorBorderSecondary: '#e8edf3',
    controlHeight: 36,
    borderRadius: 8,
    borderRadiusLG: 12,
    fontFamily: "'DM Sans', Inter, 'Segoe UI', system-ui, sans-serif",
    fontSize: 13,
    boxShadowSecondary: '0 18px 45px -22px rgba(25, 47, 76, .34)',
  },
  components: {
    Select: {
      zIndexPopup: 1200,
    },
    Menu: {
      colorItemBgSelected: '#e8f1f8',
      colorItemTextSelected: '#075f96',
      colorItemBgHover: '#f0f5f9',
      colorItemTextHover: '#172136',
      radiusItem: 8,
      radiusSubMenuItem: 8,
    },
  },
}

export default hrmsTheme
