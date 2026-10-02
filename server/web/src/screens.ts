export interface Screen {
  path: string;
  title: string;
  /** Hidden from the nav (and shown as forbidden) for Viewer sessions. */
  adminOnly?: boolean;
}

/** The admin screens from server-design 5.1, in nav order. */
export const screens: readonly Screen[] = [
  { path: '/', title: 'Dashboard' },
  { path: '/players', title: 'Players' },
  { path: '/map', title: 'Map' },
  { path: '/sessions', title: 'Sessions & Saves' },
  { path: '/teams', title: 'Teams & Factions' },
  { path: '/economy', title: 'Economy' },
  { path: '/chat', title: 'Chat' },
  { path: '/logs', title: 'Logs' },
  { path: '/settings', title: 'Settings' },
  { path: '/diagnostics', title: 'Diagnostics' },
];
