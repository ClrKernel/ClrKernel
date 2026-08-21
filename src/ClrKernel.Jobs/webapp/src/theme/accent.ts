import { DEFAULT_ACCENT, isAccentName, type AccentName } from './palette';

/**
 * Which accent is in force. Stored under its own key rather than inside the
 * layout prefs: this is a display choice that applies everywhere, and the
 * inline script in `index.html` has to read it before React exists.
 */
export const ACCENT_KEY = 'clrkernel-jobs-accent';

export function loadAccent(): AccentName {
  try {
    const stored = localStorage.getItem(ACCENT_KEY);
    return isAccentName(stored) ? stored : DEFAULT_ACCENT;
  } catch {
    // Private browsing. An accent is not worth an error.
    return DEFAULT_ACCENT;
  }
}

export function applyAccent(accent: AccentName): void {
  document.documentElement.dataset.accent = accent;
  try {
    localStorage.setItem(ACCENT_KEY, accent);
  } catch {
    // Same as above: the accent still applies for this session.
  }
}
