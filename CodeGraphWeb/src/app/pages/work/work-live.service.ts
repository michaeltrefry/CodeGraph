import { Injectable, inject, signal } from '@angular/core';
import { environment } from '../../../environments/environment';
import { AuthService } from '../../core/auth.service';
import { WorkApiService } from './work-api.service';
import { MessageEvent, WorkChange } from './work.models';

export interface SseFrame { id?: string; event?: string; data: string }

/** Splits a buffer of server-sent events into complete frames and the unfinished rest. */
export function parseSse(buffer: string): { frames: SseFrame[]; rest: string } {
  const frames: SseFrame[] = [];
  const normalized = buffer.replace(/\r\n/g, '\n');
  const blocks = normalized.split('\n\n');
  const rest = blocks.pop() ?? '';
  for (const block of blocks) {
    const frame: SseFrame = { data: '' };
    const data: string[] = [];
    for (const line of block.split('\n')) {
      if (!line || line.startsWith(':')) continue;
      const colon = line.indexOf(':');
      const field = colon < 0 ? line : line.slice(0, colon);
      const value = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '');
      if (field === 'id') frame.id = value;
      else if (field === 'event') frame.event = value;
      else if (field === 'data') data.push(value);
    }
    if (data.length > 0) {
      frame.data = data.join('\n');
      frames.push(frame);
    }
  }
  return { frames, rest };
}

const MAX_ENTRIES = 5000;
const PAGE = 200;
const LONG_POLL_SECONDS = 25;

/** The change feed of one project, kept in memory and following new entries by long-poll. */
export class ProjectWatch {
  /** Every entry read so far, oldest first (the newest 5000 at most). */
  readonly entries = signal<WorkChange[]>([]);
  /** true while the history is being read for the first time. */
  readonly loading = signal(true);
  readonly error = signal('');
  private readonly listeners = new Set<() => void>();
  private stopped = false;
  private cursor: string | undefined;

  constructor(private readonly api: WorkApiService, readonly project: string, private readonly pause = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms))) {
    void this.run();
  }

  /** Calls back after entries newer than the history arrive. Returns the unsubscribe. */
  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  stop(): void {
    this.stopped = true;
    this.listeners.clear();
  }

  private append(items: WorkChange[]): void {
    if (items.length === 0) return;
    this.entries.update(current => {
      const merged = current.concat(items);
      return merged.length > MAX_ENTRIES ? merged.slice(merged.length - MAX_ENTRIES) : merged;
    });
  }

  private async run(): Promise<void> {
    let initial = true;
    let failures = 0;
    while (!this.stopped) {
      try {
        const result = await this.api.changes(this.project, {
          cursor: this.cursor,
          fromStart: this.cursor === undefined,
          limit: PAGE,
          waitSeconds: initial ? 0 : LONG_POLL_SECONDS
        });
        if (this.stopped) return;
        this.cursor = result.next_cursor;
        failures = 0;
        this.error.set('');
        this.append(result.items);
        if (initial) {
          if (result.items.length < PAGE) {
            initial = false;
            this.loading.set(false);
          }
        } else if (result.items.length > 0) {
          this.listeners.forEach(listener => listener());
        }
      } catch (error) {
        if (this.stopped) return;
        failures++;
        this.error.set(error instanceof Error ? error.message : String(error));
        this.loading.set(false);
        initial = false;
        await this.pause(Math.min(30_000, 2000 * failures));
      }
    }
  }
}

/** Live updates for the work pages: change-feed watches by long-poll and the message stream over fetch. */
@Injectable({ providedIn: 'root' })
export class WorkLiveService {
  private readonly api = inject(WorkApiService);
  private readonly auth = inject(AuthService);

  watch(project: string): ProjectWatch {
    return new ProjectWatch(this.api, project);
  }

  /**
   * Follows the message stream of a project (SSE). The browser's EventSource cannot send the
   * Authorization header the stream needs, so this reads the response body of a fetch. Reconnects
   * with Last-Event-ID after a drop. Returns the function that stops it.
   */
  streamMessages(project: string, onEvent: (event: MessageEvent) => void, onState?: (connected: boolean) => void): () => void {
    const controller = new AbortController();
    let lastId: string | undefined;

    const run = async (): Promise<void> => {
      let failures = 0;
      while (!controller.signal.aborted) {
        try {
          const token = await this.auth.getValidAccessToken();
          const headers: Record<string, string> = { Accept: 'text/event-stream' };
          if (token) headers['Authorization'] = `Bearer ${token}`;
          if (lastId) headers['Last-Event-ID'] = lastId;
          const response = await fetch(`${environment.apiUrl}/work/messages/stream?project=${encodeURIComponent(project)}`, { headers, signal: controller.signal });
          if (!response.ok || !response.body) throw new Error(`The message stream answered ${response.status}.`);
          onState?.(true);
          failures = 0;
          const reader = response.body.getReader();
          const decoder = new TextDecoder();
          let buffer = '';
          for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            buffer += decoder.decode(value, { stream: true });
            const parsed = parseSse(buffer);
            buffer = parsed.rest;
            for (const frame of parsed.frames) {
              if (frame.id) lastId = frame.id;
              try {
                onEvent(JSON.parse(frame.data) as MessageEvent);
              } catch {
                // A frame that is not an event is skipped.
              }
            }
          }
        } catch {
          if (controller.signal.aborted) return;
        }
        onState?.(false);
        failures++;
        await new Promise(resolve => setTimeout(resolve, Math.min(30_000, 1000 * failures)));
      }
    };

    void run();
    return () => controller.abort();
  }
}

export function withDebounce(fn: () => void, ms = 400): { call: () => void; cancel: () => void } {
  let timer: ReturnType<typeof setTimeout> | undefined;
  return {
    call: () => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(() => {
        timer = undefined;
        fn();
      }, ms);
    },
    cancel: () => {
      if (timer) clearTimeout(timer);
      timer = undefined;
    }
  };
}
