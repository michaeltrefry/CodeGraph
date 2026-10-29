import { Component, DestroyRef, Injectable, computed, inject, input, output, signal } from '@angular/core';
import { WorkApiService } from './work-api.service';
import { ProjectWatch, WorkLiveService, withDebounce } from './work-live.service';
import { ProjectView, WorkApiError } from './work.models';

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
  /** Every change of the followed project read so far, oldest first. */
  readonly feed = computed(() => this.watched()?.entries() ?? []);
  readonly feedError = computed(() => this.watched()?.error() ?? '');
  private unsubscribe?: () => void;
  private reloader = withDebounce(() => void this.reload());

  protected constructor() {
    this.destroyRef.onDestroy(() => this.unfollow());
  }

  protected abstract reload(): Promise<void> | void;

  /** Reloads (debounced) whenever the project's change feed has news. */
  protected follow(project: string): ProjectWatch {
    this.unfollow();
    const watch = this.live.watch(project);
    this.watched.set(watch);
    this.unsubscribe = watch.onChange(() => this.reloader.call());
    return watch;
  }

  protected unfollow(): void {
    this.unsubscribe?.();
    this.watched()?.stop();
    this.watched.set(undefined);
    this.reloader.cancel();
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

/** Lines of a textarea as a list, empty lines dropped. */
export function lines(text: string): string[] {
  return text.split('\n').map(line => line.trim()).filter(Boolean);
}

/** A caller-chosen idempotency key for a create (plan). */
export function idempotencyKey(prefix: string): string {
  const random = typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  return `${prefix}-${random}`;
}
