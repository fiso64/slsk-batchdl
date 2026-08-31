export type ThemePreference = 'auto' | 'light' | 'dark';

const STORAGE_KEY = 'sockseek.webui.theme';
const SYSTEM_DARK_QUERY = '(prefers-color-scheme: dark)';

function isThemePreference(value: string | null): value is ThemePreference {
  return value === 'auto' || value === 'light' || value === 'dark';
}

export function getThemePreference(): ThemePreference {
  if (typeof window === 'undefined') return 'auto';

  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    return isThemePreference(stored) ? stored : 'auto';
  } catch {
    return 'auto';
  }
}

function resolveTheme(preference: ThemePreference): 'light' | 'dark' {
  if (preference !== 'auto') return preference;
  if (typeof window === 'undefined') return 'light';
  return window.matchMedia(SYSTEM_DARK_QUERY).matches ? 'dark' : 'light';
}

export function applyThemePreference(preference: ThemePreference): void {
  if (typeof document === 'undefined') return;
  const resolved = resolveTheme(preference);
  document.documentElement.dataset.theme = resolved;
  document.documentElement.style.colorScheme = resolved;
}

export function setThemePreference(preference: ThemePreference): void {
  if (typeof window !== 'undefined') {
    try {
      window.localStorage.setItem(STORAGE_KEY, preference);
    } catch {
      // Browser storage can be unavailable; the in-page preference still applies.
    }
  }
  applyThemePreference(preference);
}

export function initializeTheme(): void {
  if (typeof window === 'undefined') return;

  applyThemePreference(getThemePreference());

  const media = window.matchMedia(SYSTEM_DARK_QUERY);
  const onSystemThemeChange = (): void => {
    if (getThemePreference() === 'auto') applyThemePreference('auto');
  };
  media.addEventListener('change', onSystemThemeChange);

  window.addEventListener('storage', (event) => {
    if (event.key !== STORAGE_KEY) return;
    applyThemePreference(getThemePreference());
  });
}
