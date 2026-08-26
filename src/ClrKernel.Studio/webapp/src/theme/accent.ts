import { createContext, useContext } from 'react';
import { ACCENTS, DEFAULT_ACCENT, isAccentName, type Accent, type AccentName } from './palette';

/**
 * Which accent is in force. Stored under its own key rather than inside the
 * layout prefs: this is a display choice that applies everywhere, and the
 * inline script in `index.html` has to read it before React exists.
 */
export const ACCENT_KEY = 'clrkernel-studio-accent';

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

/**
 * The live accent, for the few places that need its literal value rather than a
 * CSS variable — today just the sandboxed output frame, which is its own
 * document and cannot see the app's tokens.
 *
 * A context rather than a read of `document.documentElement.dataset.accent`:
 * that read happens during render and nothing re-runs it, so a frame rendered
 * before the accent changed would keep painting the old one. Consumers of a
 * context re-render when it changes, which is the property actually needed.
 */
export const AccentContext = createContext<Accent>(
  ACCENTS.find((a) => a.name === DEFAULT_ACCENT) ?? ACCENTS[0],
);

export function useAccent(): Accent {
  return useContext(AccentContext);
}
