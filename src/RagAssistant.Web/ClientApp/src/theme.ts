export type Theme = 'light' | 'dark' | 'system';

const KEY = 'cortex-theme';

export function getStoredTheme(): Theme {
  const t = localStorage.getItem(KEY);
  return t === 'light' || t === 'dark' ? t : 'system';
}

export function applyTheme(theme: Theme) {
  if (theme === 'system') localStorage.removeItem(KEY);
  else localStorage.setItem(KEY, theme);

  const dark =
    theme === 'dark' ||
    (theme === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.classList.toggle('dark', dark);
}

// Re-applies the theme when the OS preference changes while in 'system' mode.
// Returns a cleanup function for use in a useEffect.
export function watchSystemTheme(getTheme: () => Theme) {
  const mq = window.matchMedia('(prefers-color-scheme: dark)');
  const onChange = () => {
    if (getTheme() === 'system') applyTheme('system');
  };
  mq.addEventListener('change', onChange);
  return () => mq.removeEventListener('change', onChange);
}
