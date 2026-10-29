import { Component, DestroyRef, Injectable, computed, inject, input, output, signal } from '@angular/core';
import { WorkApiService } from './work-api.service';
import { ProjectWatch, WatchLease, WorkLiveService, withDebounce } from './work-live.service';
import { ActivatedRoute } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ProjectView, WorkApiError, WorkChange } from './work.models';

const PROJECT_KEY = 'codegraph.work.project';

/** The projects of the work tracker and the one the Now and Messages pages are looking at. */
@Injectable({ providedIn: 'root' })
export class WorkContext {
  private readonly api = inject(WorkApiService);
  readonly projects = signal<ProjectView[]>([]);
  readonly loaded = signal(false);
  readonly error = signal('');
  readonly selected = signal<string>(this.read());

  async load(): Promise<void> {
    try {
      const projects = await this.api.projects();
      this.projects.set(projects);
      this.error.set('');
      if (!projects.some(project => project.key === this.selected())) this.select(projects[0]?.key ?? '');
    } catch (error) {
      this.error.set(error instanceof Error ? error.message : String(error));
    } finally {
      this.loaded.set(true);
    }
  }

  select(key: string): void {
    this.selected.set(key);
    try { localStorage.setItem(PROJECT_KEY, key); } catch { /* storage is a convenience */ }
  }

  private read(): string {
    try { return localStorage.getItem(PROJECT_KEY) ?? ''; } catch { return ''; }
  }
}

/** Shows a refusal of a work tool: the message, and for a conflict what to do about it. */
@Component({
  selector: 'wrk-error',
  standalone: true,
  template: `
    @if (error(); as e) {
      <div [class]="e.isConflict ? 'wrk-conflict' : 'wrk-error'" role="alert">
        <strong>{{ e.isConflict ? 'Conflict' : 'Refused' }} ({{ e.code }}).</strong> {{ e.message }}
        @if (e.isConflict) {
          <div>Someone else changed this since you loaded it. @if (reloadable()) { <button type="button" class="wrk-btn wrk-btn-quiet" (click)="reload.emit()">Reload the current version</button> }</div>
        }
      </div>
    }
  `
})
export class ErrorComponent {
  readonly error = input<WorkApiError | null>(null);
  readonly reloadable = input(true);
  readonly reload = output<void>();
}

/** Common plumbing of the work pages: a guarded action runner and live reload by change feed. */
export abstract class WorkPageBase {
  protected readonly api = inject(WorkApiService);
  protected readonly live = inject(WorkLiveService);
  protected readonly destroyRef = inject(DestroyRef);
  readonly error = signal<WorkApiError | null>(null);
  readonly notice = signal('');
  readonly busy = signal(false);
  /** The change feed being followed, if any (a signal so templates and computeds can read it). */
  readonly watched = signal<ProjectWatch | undefined>(undefined);
  /** The newest writes of the followed project, oldest first, read with loadRecent. */
  readonly feed = signal<WorkChange[]>([]);
  readonly feedError = computed(() => this.watched()?.error() ?? '');
  private unsubscribe?: () => void;
  private lease?: WatchLease;
  private reloader = withDebounce(() => void this.reload());

  protected constructor() {
    this.destroyRef.onDestroy(() => this.unfollow());
  }

  protected abstract reload(): Promise<void> | void;

  /** Reloads (debounced) whenever the project's change feed has news. */
  protected follow(project: string): ProjectWatch {
    this.unfollow();
    this.lease = this.live.acquire(project);
    const watch = this.lease.watch;
    this.watched.set(watch);
    this.unsubscribe = watch.onChange(() => this.reloader.call());
    return watch;
  }

  protected unfollow(): void {
    this.unsubscribe?.();
    this.lease?.release();
    this.lease = undefined;
    this.watched.set(undefined);
    this.reloader.cancel();
  }

  /** Reads the project's newest writes into feed (a targeted read, not the whole history). */
  protected async loadRecent(project: string, limit = 40): Promise<void> {
    this.feed.set(await this.api.recentChanges(project, limit));
  }

  /**
   * Runs handler for the route's key now and whenever it changes (the router reuses a component
   * when only the parameter differs: an item link from an item page, a created project's page).
   */
  protected onKey(route: ActivatedRoute, handler: (key: string) => void): void {
    route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(params => handler(params.get('key') ?? ''));
  }

  /** After a create: a retry that finds the earlier attempt's result changed nothing, and says so. */
  protected noteReplay(result: { created?: boolean } | undefined): void {
    if (result && result.created === false) this.notice.set("Already created by an earlier attempt; your later edits weren't applied.");
  }

  /** Runs a write; a refusal is shown, not thrown. Returns undefined when it was refused. */
  async run<T>(action: () => Promise<T>, success = ''): Promise<T | undefined> {
    this.busy.set(true);
    this.error.set(null);
    this.notice.set('');
    try {
      const result = await action();
      if (success) this.notice.set(success);
      return result;
    } catch (error) {
      this.error.set(error instanceof WorkApiError ? error : new WorkApiError(0, 'error', error instanceof Error ? error.message : String(error)));
      return undefined;
    } finally {
      this.busy.set(false);
    }
  }
}

/**
 * One idempotency key per attempt at a create: kept while the form stays open and is retried (a
 * retry after a lost response repeats the key and creates nothing twice), dropped once it succeeded.
 */
export class KeyHolder {
  private current?: string;

  constructor(private readonly prefix: string) {}

  get key(): string {
    return (this.current ??= idempotencyKey(this.prefix));
  }

  reset(): void {
    this.current = undefined;
  }
}

/** Lines of a textarea as a list, empty lines dropped. */
export function lines(text: string): string[] {
  return text.split('\n').map(line => line.trim()).filter(Boolean);
}

/** A caller-chosen idempotency key for a create (plan). */
export function idempotencyKey(prefix: string): string {
  const random = typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  return `${prefix}-${random}`;
}
