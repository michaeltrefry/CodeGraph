import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { KnowledgeApiService } from './knowledge-api.service';
import { ProjectRow, WorkActor, WorkError } from './knowledge.models';

/** Who wrote a row (E3): a person by name, a session as `harness / model @ host`. */
export function actorLabel(actor: WorkActor | null | undefined): string {
  if (!actor) return 'unknown';
  if (actor.kind !== 'session') return actor.name;
  const tool = [actor.harness, actor.model].filter(Boolean).join(' / ') || actor.name;
  return actor.host ? `${tool} @ ${actor.host}` : tool;
}

/** The title (tooltip) of an actor: the identity and, for a session, its id. */
export function actorTitle(actor: WorkActor | null | undefined): string {
  if (!actor) return '';
  return actor.session_id ? `${actor.name} (session ${actor.session_id})` : actor.name;
}

/** A snake_case value as words: tracker_updates → tracker updates. */
export function words(value: string | null | undefined): string {
  return (value ?? '').replace(/_/g, ' ');
}

/**
 * The idempotency key of one create form (E1): made once, kept across retries of the same
 * submission so a lost response cannot create twice, and replaced only after a success.
 */
export class IdempotencyKey {
  private current = IdempotencyKey.make();

  get value(): string {
    return this.current;
  }

  reset(): void {
    this.current = IdempotencyKey.make();
  }

  private static make(): string {
    const random = globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
    return `ui-${random}`;
  }
}

/** The work error of a refused call, or a generic one. */
export function workError(error: unknown): WorkError {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as Partial<WorkError> | null;
    if (body && typeof body === 'object' && typeof body.code === 'string' && typeof body.message === 'string') {
      return body as WorkError;
    }
    if (body && typeof body === 'object' && typeof (body as { detail?: unknown }).detail === 'string') {
      return { code: 'unavailable', message: (body as { detail: string }).detail };
    }
    return { code: 'http_' + error.status, message: error.message };
  }
  return { code: 'error', message: error instanceof Error ? error.message : String(error) };
}

/** A refusal as one line for a banner; a conflict says the row was reloaded. */
export function errorText(error: unknown): string {
  const refused = workError(error);
  return refused.code === 'conflict'
    ? `${refused.message} It changed since you read it, so it was reloaded; check it and try again.`
    : refused.message;
}

const PROJECT_KEY = 'codegraph.knowledge.project';

/** The project the knowledge pages show: shared by the three pages and remembered per browser. */
@Injectable({ providedIn: 'root' })
export class KnowledgeProjectService {
  private readonly api = inject(KnowledgeApiService);

  readonly projects = signal<ProjectRow[]>([]);
  readonly selected = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  async load(): Promise<string | null> {
    try {
      const projects = await firstValueFrom(this.api.listProjects());
      this.projects.set(projects);
      const remembered = this.read();
      const current = this.selected() ?? remembered;
      const choice = projects.find(project => project.key === current)?.key ?? projects[0]?.key ?? null;
      this.selected.set(choice);
      this.error.set(null);
      return choice;
    } catch (error) {
      this.error.set(errorText(error));
      return null;
    }
  }

  select(key: string): void {
    this.selected.set(key);
    try {
      localStorage.setItem(PROJECT_KEY, key);
    } catch {
      // Storage may be unavailable; the choice still holds for this visit.
    }
  }

  private read(): string | null {
    try {
      return localStorage.getItem(PROJECT_KEY);
    } catch {
      return null;
    }
  }
}
