import { createContext, useContext } from 'react';
import type { ThemeName } from './palette';

/**
 * Light, dark, or whatever the OS says.
 *
 * Three values rather than a boolean, because "follow the system" is a real
 * answer and not the absence of one: a user who has their machine switch at
 * sunset wants this to switch with it, and a user who picked dark on a light
 * machine wants it to stay dark. Collapsing those to a toggle loses the first.
 */
export type ThemeMode = 'system' | 'light' | 'dark';

/** Its own key, like the accent's — a display choice, not part of a layout. */
export const THEME_KEY = 'clrkernel-studio-theme';

export const DEFAULT_MODE: ThemeMode = 'system';

export function isThemeMode(value: unknown): value is ThemeMode {
  return value === 'system' || value === 'light' || value === 'dark';
}

export function loadThemeMode(): ThemeMode {
  try {
    const stored = localStorage.getItem(THEME_KEY);
    return isThemeMode(stored) ? stored : DEFAULT_MODE;
  } catch {
    // Private browsing. A theme is not worth an error.
    return DEFAULT_MODE;
  }
}

/** What the OS is asking for. Light when nothing can answer — a server-rendered
 *  or headless context has no preference, and light is the design's home. */
export function systemTheme(): ThemeName {
  return typeof window !== 'undefined'
    && window.matchMedia?.('(prefers-color-scheme: dark)').matches
    ? 'dark'
    : 'light';
}

/** The theme a mode actually resolves to right now. */
export function resolveTheme(mode: ThemeMode): ThemeName {
  return mode === 'system' ? systemTheme() : mode;
}

/**
 * Puts the resolved theme on `<html>` and remembers the mode.
 *
 * `data-theme` carries the *resolved* value, never "system": the token layer
 * then needs one selector rather than a selector and a media query that can
 * disagree with each other, and an explicit choice can override the OS at all —
 * which a media query alone cannot do.
 */
export function applyThemeMode(mode: ThemeMode): ThemeName {
  const theme = resolveTheme(mode);
  document.documentElement.dataset.theme = theme;
  try {
    localStorage.setItem(THEME_KEY, mode);
  } catch {
    // Same as the accent: it still applies for this session.
  }
  return theme;
}

/**
 * The live theme, for the two places that need its literal colours rather than
 * a CSS variable: Monaco, whose theme API takes hex, and the sandboxed output
 * frame, which is its own document and cannot see the app's tokens.
 *
 * A context rather than a read of `document.documentElement.dataset.theme` for
 * the same reason the accent is one — that read happens during render and
 * nothing re-runs it, so a frame rendered before the theme changed would keep
 * painting the old one.
 */
export const ThemeContext = createContext<ThemeName>('light');

export function useTheme(): ThemeName {
  return useContext(ThemeContext);
}
