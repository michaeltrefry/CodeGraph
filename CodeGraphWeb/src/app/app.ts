import { Component, DestroyRef, HostListener, ViewChild, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, NavigationStart, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { catchError, filter, map, of, startWith } from 'rxjs';
import { ApiService } from './core/api.service';
import { AuthService } from './core/auth.service';
import { ChatContextService } from './core/chat-context.service';
import { AccentName, ThemeService } from './core/theme.service';
import { WikiSection, WikiTreeNode } from './core/models';
import { ChatSidebarComponent } from './components/chat-sidebar/chat-sidebar.component';
import { NavSearchComponent } from './components/nav-search/nav-search.component';
import { WikiTreeNodeComponent } from './pages/wiki/wiki-tree-node.component';

interface Crumb {
  label: string;
  url: string;
}

const SIDEBAR_COLLAPSED_KEY = 'codegraph.sidebar.collapsed';

const SEGMENT_LABELS: Record<string, string> = {
  repos: 'Repositories',
  schemas: 'Schemas',
  graph: 'Graph',
  memory: 'Memory',
  clusters: 'Clusters',
  impact: 'Impact',
  ask: 'Ask',
  wiki: 'Wiki',
  settings: 'Admin',
  search: 'Search',
  nodes: 'Nodes',
  operations: 'Operations',
  schedules: 'Schedules',
  'db-health': 'Database health',
  sections: 'Sections',
  exclusions: 'Exclusions',
  admins: 'Admins',
  prompts: 'Prompts',
  llm: 'LLM config',
  'mcp-hub': 'MCP Hub',
  'database-sources': 'Database sources',
  reports: 'Reports',
  'assistant-debug': 'Assistant debug',
  'access-tokens': 'Access tokens'
};

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ChatSidebarComponent, NavSearchComponent, WikiTreeNodeComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly themeService = inject(ThemeService);
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);

  currentUrl = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map(event => event.urlAfterRedirects),
      startWith(this.router.url)
    ),
    { initialValue: this.router.url }
  );

  sidebarCollapsed = signal(this.readCollapsedPref());
  wikiSections = signal<WikiSection[]>([]);
  expandedWikiSection = signal<string | null>(null);
  wikiTree = signal<WikiTreeNode[] | null>(null);
  loadingWikiTree = signal(false);
  theme = this.themeService.theme;
  accent = this.themeService.accent;
  accentOptions = this.themeService.accents;
  themeToggleLabel = computed(() => this.theme() === 'dark' ? 'Use light theme' : 'Use dark theme');
  wikiNavOpen = computed(() => this.currentUrl().split('?')[0].startsWith('/wiki'));
  authEnabled = this.auth.enabled;
  currentUser = this.auth.currentUser;
  isAdmin = computed(() => !this.authEnabled() || this.currentUser()?.isAdmin === true);

  @ViewChild(NavSearchComponent) private navSearch?: NavSearchComponent;

  crumbs = computed<Crumb[]>(() => {
    const url = this.currentUrl().split('?')[0].split('#')[0];
    const parts = url.split('/').filter(Boolean);
    const out: Crumb[] = [];
    let href = '';

    for (const part of parts) {
      href += '/' + part;
      out.push({ label: SEGMENT_LABELS[part] ?? decodeURIComponent(part), url: href });
    }

    return out;
  });

  constructor() {
    const chatContext = inject(ChatContextService);

    this.router.events.pipe(
      filter((e): e is NavigationStart => e instanceof NavigationStart),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe(() => chatContext.clear());

    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      startWith(null),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe(() => this.syncWikiNav());
  }

  toggleSidebar(): void {
    const next = !this.sidebarCollapsed();
    this.sidebarCollapsed.set(next);
    try { localStorage.setItem(SIDEBAR_COLLAPSED_KEY, next ? '1' : '0'); } catch {}
  }

  toggleTheme(): void {
    this.themeService.toggleTheme();
  }

  setAccent(accent: AccentName): void {
    this.themeService.setAccent(accent);
  }

  signOut(): void {
    this.auth.signOut();
  }

  accentLabel(accent: AccentName): string {
    return accent[0].toUpperCase() + accent.slice(1);
  }

  openWikiSection(section: WikiSection): void {
    if (this.expandedWikiSection() !== section.slug) {
      this.expandedWikiSection.set(section.slug);
      this.loadWikiTree(section.slug);
    }
  }

  toggleWikiSection(section: WikiSection): void {
    if (this.expandedWikiSection() === section.slug) {
      this.expandedWikiSection.set(null);
      this.wikiTree.set(null);
      this.loadingWikiTree.set(false);
      return;
    }

    this.expandedWikiSection.set(section.slug);
    this.loadWikiTree(section.slug);
  }

  @HostListener('document:keydown', ['$event'])
  onDocumentKeydown(event: KeyboardEvent): void {
    if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 'k') return;
    if (this.isEditableTarget(event.target)) return;

    event.preventDefault();
    this.navSearch?.focus();
  }

  private isEditableTarget(target: EventTarget | null): boolean {
    if (!(target instanceof HTMLElement)) return false;
    const tagName = target.tagName.toLowerCase();
    return target.isContentEditable || tagName === 'input' || tagName === 'textarea' || tagName === 'select';
  }

  private readCollapsedPref(): boolean {
    try { return localStorage.getItem(SIDEBAR_COLLAPSED_KEY) === '1'; } catch { return false; }
  }

  private syncWikiNav(): void {
    const url = this.router.url.split('?')[0];
    if (!url.startsWith('/wiki')) return;

    this.ensureWikiSectionsLoaded();

    const sectionSlug = url.slice('/wiki/'.length).split('/')[0];
    if (sectionSlug && sectionSlug !== this.expandedWikiSection()) {
      this.expandedWikiSection.set(sectionSlug);
      this.loadWikiTree(sectionSlug);
    }
  }

  private ensureWikiSectionsLoaded(): void {
    if (this.wikiSections().length > 0) return;

    this.api.listSections().pipe(
      catchError(() => of([] as WikiSection[])),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe(sections => this.wikiSections.set(sections));
  }

  private loadWikiTree(sectionSlug: string): void {
    this.loadingWikiTree.set(true);
    this.wikiTree.set(null);

    this.api.getSectionTree(sectionSlug).pipe(
      map(tree => tree.filter(node => node.slug !== '_root')),
      catchError(() => of([] as WikiTreeNode[])),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe(tree => {
      if (this.expandedWikiSection() !== sectionSlug) return;
      this.wikiTree.set(tree);
      this.loadingWikiTree.set(false);
    });
  }
}
