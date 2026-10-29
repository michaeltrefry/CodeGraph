import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../../core/auth.service';
import { ProjectWatch, WorkLiveService, parseSse, withDebounce } from './work-live.service';
import { WorkApiService } from './work-api.service';
import { WorkChange } from './work.models';

const change = (sequence: number): WorkChange => ({
  sequence,
  project_id: 'p',
  project_key: 'W',
  entity: 'item',
  entity_id: 'i',
  entity_key: 'W-1',
  kind: 'item_updated',
  actor: { name: 'michael', kind: 'user' },
  changes: [],
  created_at: '2026-01-01T00:00:00Z'
});

describe('parseSse', () => {
  it('reads complete frames and keeps the unfinished rest', () => {
    const { frames, rest } = parseSse('id: 4\nevent: message\ndata: {"a":1}\n\n: keepalive\n\nid: 5\nevent: mess');
    expect(frames).toEqual([{ id: '4', event: 'message', data: '{"a":1}' }]);
    expect(rest).toBe('id: 5\nevent: mess');
  });

  it('joins multi-line data and tolerates CRLF', () => {
    const { frames } = parseSse('data: one\r\ndata: two\r\n\r\n');
    expect(frames[0].data).toBe('one\ntwo');
  });
});

describe('ProjectWatch', () => {
  const idle = async () => undefined;

  it('starts at the head of the feed (no history read), tells listeners once, then on each batch of entries', async () => {
    const calls: unknown[] = [];
    const pages = [
      { items: [], next_cursor: 'head' },
      { items: [change(3)], next_cursor: 'c3' }
    ];
    let watch!: ProjectWatch;
    const api = {
      changes: vi.fn(async (_project: string, options: unknown) => {
        calls.push(options);
        const page = pages.shift();
        if (page) return page;
        watch.stop();
        return { items: [], next_cursor: 'c3' };
      })
    } as unknown as WorkApiService;
    watch = new ProjectWatch(api, 'W', idle);
    const heard = vi.fn();
    watch.onChange(heard);

    await vi.waitFor(() => expect(heard).toHaveBeenCalledTimes(2));
    expect(watch.loading()).toBe(false);
    expect(calls[0]).toMatchObject({ cursor: undefined, waitSeconds: 0 });
    expect(calls[0]).not.toHaveProperty('fromStart');
    expect(calls[1]).toMatchObject({ cursor: 'head', waitSeconds: 25 });
    watch.stop();
  });

  it('reports a failure and keeps trying', async () => {
    let watch!: ProjectWatch;
    let attempts = 0;
    const api = {
      changes: vi.fn(async () => {
        attempts++;
        if (attempts === 1) throw new Error('offline');
        watch.stop();
        return { items: [], next_cursor: 'c' };
      })
    } as unknown as WorkApiService;
    watch = new ProjectWatch(api, 'W', idle);
    await vi.waitFor(() => expect(attempts).toBe(2));
  });

  it('starts again at the head when the cursor is ahead of a restored feed, instead of looping on the refusal', async () => {
    const cursors: (string | undefined)[] = [];
    let watch!: ProjectWatch;
    const api = {
      changes: vi.fn(async (_project: string, options: { cursor?: string }) => {
        cursors.push(options.cursor);
        if (cursors.length === 1) return { items: [], next_cursor: 'far' };
        if (cursors.length === 2) throw new Error('cursor is ahead of the newest entry of the feed (the database was restored to an earlier state). Start again without cursor.');
        watch.stop();
        return { items: [], next_cursor: 'head' };
      })
    } as unknown as WorkApiService;
    const pause = vi.fn(idle);
    watch = new ProjectWatch(api, 'W', pause);
    const heard = vi.fn();
    watch.onChange(heard);
    await vi.waitFor(() => expect(cursors).toHaveLength(3));
    expect(cursors).toEqual([undefined, 'far', undefined]);
    expect(pause).not.toHaveBeenCalled();
  });
});

describe('WorkLiveService.acquire', () => {
  it('shares one watch per project and stops it a little after the last page lets go', async () => {
    vi.useFakeTimers();
    const api = { changes: vi.fn(() => new Promise(() => undefined)) };
    TestBed.configureTestingModule({ providers: [{ provide: WorkApiService, useValue: api }, { provide: AuthService, useValue: {} }] });
    const service = TestBed.inject(WorkLiveService);
    const first = service.acquire('W');
    const second = service.acquire('W');
    expect(second.watch).toBe(first.watch);
    expect(api.changes).toHaveBeenCalledTimes(1);
    first.release();
    second.release();
    const again = service.acquire('W');
    expect(again.watch).toBe(first.watch);
    again.release();
    vi.advanceTimersByTime(6000);
    expect(service.acquire('W').watch).not.toBe(first.watch);
    vi.useRealTimers();
  });
});

describe('WorkLiveService.streamMessages', () => {
  it('resumes with Last-Event-ID and tells the page to reload once on every reconnect', async () => {
    const headers: Record<string, string>[] = [];
    const frame = 'id: 5\nevent: message\ndata: {"sequence":5,"message_id":"m"}\n\n';
    const body = () => {
      let sent = false;
      return { getReader: () => ({ read: async () => (sent ? { done: true, value: undefined } : ((sent = true), { done: false, value: new TextEncoder().encode(frame) })) }) };
    };
    vi.stubGlobal('fetch', vi.fn(async (_url: string, init: { headers: Record<string, string> }) => {
      headers.push(init.headers);
      return { ok: true, body: body() };
    }));
    TestBed.configureTestingModule({ providers: [{ provide: WorkApiService, useValue: {} }, { provide: AuthService, useValue: { getValidAccessToken: async () => 'tok' } }] });
    const service = TestBed.inject(WorkLiveService);
    const events: unknown[] = [];
    const reconnected = vi.fn();
    const stop = service.streamMessages('W', event => events.push(event), undefined, reconnected);
    await vi.waitFor(() => expect(headers).toHaveLength(2), { timeout: 4000 });
    stop();
    vi.unstubAllGlobals();
    expect(headers[0]['Authorization']).toBe('Bearer tok');
    expect(headers[0]['Last-Event-ID']).toBeUndefined();
    expect(headers[1]['Last-Event-ID']).toBe('5');
    expect(events).toHaveLength(2);
    expect(reconnected).toHaveBeenCalledTimes(1);
  });
});

describe('withDebounce', () => {
  it('runs once after the last call', () => {
    vi.useFakeTimers();
    const fn = vi.fn();
    const debounced = withDebounce(fn, 100);
    debounced.call();
    debounced.call();
    vi.advanceTimersByTime(99);
    expect(fn).not.toHaveBeenCalled();
    vi.advanceTimersByTime(2);
    expect(fn).toHaveBeenCalledTimes(1);
    vi.useRealTimers();
  });
});
