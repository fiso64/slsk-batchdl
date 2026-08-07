export const navigationItems = [
  { id: 'dashboard', label: 'Dashboard', icon: '▦', placement: 'primary' },
  { id: 'search', label: 'Search', icon: '⌕', placement: 'primary' },
  { id: 'downloads', label: 'Downloads', icon: '↓', placement: 'primary' },
  { id: 'uploads', label: 'Uploads', icon: '↑', placement: 'primary' },
  { id: 'chat', label: 'Chat', icon: '◌', placement: 'primary' },
  { id: 'settings', label: 'Settings', icon: '⚙', placement: 'secondary' },
] as const;

export type PageId = (typeof navigationItems)[number]['id'];
