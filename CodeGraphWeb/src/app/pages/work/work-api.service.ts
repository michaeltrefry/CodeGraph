import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  ChangesResult,
  FeatureView,
  FindResult,
  GitHubEvidence,
  HoldView,
  ItemView,
  MessageView,
  PlanPreview,
  ProjectView,
  ResourceView,
  SessionView,
  WorkApiError,
  WorkErrorBody
} from './work.models';

type Query = Record<string, string | number | boolean | undefined | null>;

/**
 * The REST routes of the work tools, the same ones agents call (E8). Every write here is attributed
 * to the signed-in person by the server; this service only shapes arguments and reads refusals
 * ({code, message, current}) into a WorkApiError.
 */
@Injectable({ providedIn: 'root' })
export class WorkApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiUrl}/work`;

  async request<T>(method: 'GET' | 'POST' | 'PATCH', path: string, body?: unknown, query?: Query): Promise<T> {
    let params = new HttpParams();
    for (const [name, value] of Object.entries(query ?? {})) {
      if (value !== undefined && value !== null) params = params.set(name, String(value));
    }
    try {
      return await firstValueFrom(this.http.request<T>(method, `${this.base}${path}`, { body: method === 'GET' ? undefined : (body ?? {}), params }));
    } catch (error) {
      throw this.toError(error);
    }
  }

  private toError(error: unknown): WorkApiError {
    if (error instanceof WorkApiError) return error;
    if (error instanceof HttpErrorResponse) {
      const body = error.error as Partial<WorkErrorBody> | null;
      if (body && typeof body === 'object' && typeof body.code === 'string') {
        return new WorkApiError(error.status, body.code, body.message ?? error.message, body.current);
      }
      const detail = body && typeof body === 'object' && 'detail' in body ? String((body as { detail: unknown }).detail) : error.message;
      return new WorkApiError(error.status, error.status === 0 ? 'network' : 'http_error', detail);
    }
    return new WorkApiError(0, 'network', error instanceof Error ? error.message : String(error));
  }

  // ---- reads ---------------------------------------------------------------------------------

  async projects(): Promise<ProjectView[]> {
    const result = await this.request<FindResult<ProjectView>>('POST', '/find', { kind: 'project', detail: 'full', limit: 200 });
    return result.items;
  }

  /** get_work: detail full adds every handoff and attempt of an item. */
  get<T = ProjectView | FeatureView | ItemView>(id: string, detail: 'summary' | 'full' = 'full'): Promise<T> {
    return this.request<T>('GET', `/${encodeURIComponent(id)}`, undefined, { detail });
  }

  async features(project: string): Promise<FeatureView[]> {
    return this.findAll<FeatureView>({ project, kind: 'feature', detail: 'full' });
  }

  async items(project: string, filters: Record<string, unknown> = {}): Promise<ItemView[]> {
    return this.findAll<ItemView>({
      project,
      kind: 'item',
      detail: 'full',
      status: ['backlog', 'ready', 'in_progress', 'in_review', 'complete', 'closed'],
      ...filters
    });
  }

  private async findAll<T>(body: Record<string, unknown>): Promise<T[]> {
    const all: T[] = [];
    let cursor: string | undefined;
    for (let page = 0; page < 20; page++) {
      const result = await this.request<FindResult<T>>('POST', '/find', { ...body, limit: 200, cursor });
      all.push(...result.items);
      cursor = result.next_cursor;
      if (!cursor) break;
    }
    return all;
  }

  changes(project: string, options: { cursor?: string; fromStart?: boolean; waitSeconds?: number; limit?: number }): Promise<ChangesResult> {
    return this.request<ChangesResult>('GET', '/changes', undefined, {
      project,
      cursor: options.cursor,
      from_start: options.fromStart ? true : undefined,
      wait_seconds: options.waitSeconds,
      limit: options.limit
    });
  }

  async sessions(project: string, liveOnly = true): Promise<SessionView[]> {
    return (await this.request<{ items: SessionView[] }>('GET', `/projects/${encodeURIComponent(project)}/sessions`, undefined, { live_only: liveOnly })).items;
  }

  async resources(project: string): Promise<ResourceView[]> {
    return (await this.request<{ items: ResourceView[] }>('GET', `/projects/${encodeURIComponent(project)}/resources`)).items;
  }

  messages(project: string, filters: { channel?: string; item?: string; feature?: string; resource?: string; open?: boolean; toHuman?: boolean; before?: string; limit?: number } = {}):
    Promise<{ items: MessageView[]; next_before?: string }> {
    return this.request('GET', `/projects/${encodeURIComponent(project)}/messages`, undefined, {
      channel: filters.channel,
      item: filters.item,
      feature: filters.feature,
      resource: filters.resource,
      open: filters.open,
      to_human: filters.toHuman,
      before: filters.before,
      limit: filters.limit
    });
  }

  async githubEvidence(project: string, item: string): Promise<GitHubEvidence[]> {
    return (await this.request<{ items: GitHubEvidence[] }>('GET', `/projects/${encodeURIComponent(project)}/items/${encodeURIComponent(item)}/github-evidence`)).items;
  }

  // ---- writes: the tools ---------------------------------------------------------------------

  create(body: Record<string, unknown>): Promise<{ created: boolean; project?: ProjectView; feature?: FeatureView; item?: ItemView }> {
    return this.request('POST', '', body);
  }

  update(id: string, body: Record<string, unknown>): Promise<{ project?: ProjectView; feature?: FeatureView; item?: ItemView }> {
    return this.request('PATCH', `/${encodeURIComponent(id)}`, body);
  }

  next(body: Record<string, unknown>): Promise<{ item?: ItemView; reused: boolean; reason?: string }> {
    return this.request('POST', '/next', body);
  }

  syncPlan(body: Record<string, unknown>): Promise<PlanPreview | { kind: 'plan_applied'; repeated: boolean; feature: FeatureView }> {
    return this.request('POST', '/plans/sync', body);
  }

  post(body: Record<string, unknown>): Promise<{ created: boolean; message: MessageView; recipients: number; resolved?: MessageView; item_versions?: { key: string; version: number }[] }> {
    return this.request('POST', '/messages', body);
  }

  ack(body: Record<string, unknown>): Promise<{ acked: number; item_versions?: { key: string; version: number }[] }> {
    return this.request('POST', '/ack', body);
  }

  inbox(project: string, options: { cursor?: string; includeRead?: boolean; limit?: number; waitSeconds?: number } = {}): Promise<{ items: MessageView[]; next_cursor: string }> {
    return this.request('GET', '/inbox', undefined, {
      project,
      cursor: options.cursor,
      include_read: options.includeRead,
      limit: options.limit,
      wait_seconds: options.waitSeconds
    });
  }

  acquire(body: Record<string, unknown>): Promise<{ outcome: 'granted' | 'held'; resource: ResourceView; hold?: HoldView }> {
    return this.request('POST', '/holds', body);
  }

  renewHold(holdId: string, ttlSeconds?: number): Promise<HoldView> {
    return this.request('POST', `/holds/${encodeURIComponent(holdId)}/renew`, ttlSeconds ? { ttl_seconds: ttlSeconds } : {});
  }

  releaseHold(holdId: string): Promise<{ hold: HoldView; resource: ResourceView }> {
    return this.request('POST', `/holds/${encodeURIComponent(holdId)}/release`, {});
  }

  record(body: Record<string, unknown>): Promise<unknown> {
    return this.request('POST', '/knowledge', body);
  }

  recall(body: Record<string, unknown>): Promise<{ items: import('./work.models').KnowledgeRow[]; next_cursor?: string }> {
    return this.request('POST', '/knowledge/recall', body);
  }
}
