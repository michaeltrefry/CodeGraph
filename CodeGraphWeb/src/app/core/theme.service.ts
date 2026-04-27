import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';

export type ThemeName = 'light' | 'dark';
export type AccentName = 'indigo' | 'cyan' | 'green' | 'amber';

const THEME_KEY = 'codegraph.theme';
const ACCENT_KEY = 'codegraph.accent';
const THEMES: readonly ThemeName[] = ['light', 'dark'];
const ACCENTS: readonly AccentName[] = ['indigo', 'cyan', 'green', 'amber'];

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);

  readonly accents = ACCENTS;
  readonly theme = signal<ThemeName>(this.readInitialTheme());
  readonly accent = signal<AccentName>(this.readInitialAccent());

  constructor() {
    this.applyTheme(this.theme());
    this.applyAccent(this.accent());
    this.followSystemThemeUntilChosen();
  }

  toggleTheme(): void {
    this.setTheme(this.theme() === 'dark' ? 'light' : 'dark');
  }

  setTheme(theme: ThemeName): void {
    if (!this.isTheme(theme)) return;
    this.theme.set(theme);
    this.writeStorage(THEME_KEY, theme);
    this.applyTheme(theme);
  }

  setAccent(accent: AccentName): void {
    if (!this.isAccent(accent)) return;
    this.accent.set(accent);
    this.writeStorage(ACCENT_KEY, accent);
    this.applyAccent(accent);
  }

  private readInitialTheme(): ThemeName {
    const stored = this.readStorage(THEME_KEY);
    if (this.isTheme(stored)) return stored;
    return this.prefersDark() ? 'dark' : 'light';
  }

  private readInitialAccent(): AccentName {
    const stored = this.readStorage(ACCENT_KEY);
    return this.isAccent(stored) ? stored : 'indigo';
  }

  private followSystemThemeUntilChosen(): void {
    const media = this.window()?.matchMedia('(prefers-color-scheme: dark)');
    if (!media) return;

    media.addEventListener('change', event => {
      if (this.readStorage(THEME_KEY)) return;
      const theme = event.matches ? 'dark' : 'light';
      this.theme.set(theme);
      this.applyTheme(theme);
    });
  }

  private prefersDark(): boolean {
    return this.window()?.matchMedia('(prefers-color-scheme: dark)').matches ?? false;
  }

  private applyTheme(theme: ThemeName): void {
    this.document.documentElement.dataset['theme'] = theme;
  }

  private applyAccent(accent: AccentName): void {
    this.document.documentElement.dataset['accent'] = accent;
  }

  private readStorage(key: string): string | null {
    try {
      return this.window()?.localStorage.getItem(key) ?? null;
    } catch {
      return null;
    }
  }

  private writeStorage(key: string, value: string): void {
    try {
      this.window()?.localStorage.setItem(key, value);
    } catch {}
  }

  private window(): Window | null {
    return this.document.defaultView;
  }

  private isTheme(value: string | null): value is ThemeName {
    return THEMES.includes(value as ThemeName);
  }

  private isAccent(value: string | null): value is AccentName {
    return ACCENTS.includes(value as AccentName);
  }
}
