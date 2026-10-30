import { DestroyableInjector, Injector, runInInjectionContext } from '@angular/core';
import { of, Subject, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { JobQueueEntry, JobQueueResponse } from '../../core/models';
import { AdminJobQueueComponent, parseUtcDate } from './admin-job-queue.component';

const item: JobQueueEntry = {
  kind: 'indexer-run', id: 7, operation: 'process_repository', target: 'orders', status: 'failed',
  requestedByUsername: 'admin', message: null, error: 'Index failed', createdAt: '2026-10-30T10:00:00',
  startedAt: null, nextAttemptAt: null, attemptCount: 1, cancelable: false
};
const response: JobQueueResponse = { items: [item], running: 0, queued: 0, failed: 1, generatedAt: item.createdAt };

describe('AdminJobQueueComponent', () => {
  let injector: DestroyableInjector;
  let destroyed: boolean;
  let component: AdminJobQueueComponent;
  let api: { getJobQueue: ReturnType<typeof vi.fn>; dismissJob: ReturnType<typeof vi.fn>; cancelIndexerRun: ReturnType<typeof vi.fn> };
  beforeEach(() => {
    destroyed = false;
    vi.useFakeTimers();
    api = { getJobQueue: vi.fn(() => of(response)), dismissJob: vi.fn(() => of(undefined)), cancelIndexerRun: vi.fn(() => of({})) };
  });
  function create(): void {
    injector = Injector.create({ providers: [{ provide: ApiService, useValue: api }] });
    component = runInInjectionContext(injector, () => new AdminJobQueueComponent());
  }
  afterEach(() => {
    // DestroyRef resolves to this injector, which must be destroyed only once.
    if (injector && !destroyed) injector.destroy();
    vi.useRealTimers();
  });

  it('loads on creation, polls every five seconds, and stops when destroyed', async () => {
    create();
    await component.refresh();
    expect(component.queue()).toEqual(response);
    await vi.advanceTimersByTimeAsync(5000);
    expect(api.getJobQueue).toHaveBeenCalledTimes(2);
    injector.destroy();
    destroyed = true;
    await vi.advanceTimersByTimeAsync(10000);
    expect(api.getJobQueue).toHaveBeenCalledTimes(2);
  });

  it('does not overlap slow polls and cancels an outstanding request on destruction', async () => {
    const request = new Subject<JobQueueResponse>();
    api.getJobQueue.mockReturnValue(request);
    create();
    expect(component.loading()).toBe(true);
    await vi.advanceTimersByTimeAsync(15000);
    expect(api.getJobQueue).toHaveBeenCalledTimes(1);
    injector.destroy();
    destroyed = true;
    expect(request.observed).toBe(false);
    request.next(response);
    expect(component.queue()).toBeNull();
  });

  it('keeps the last good data on poll failure and clears the error on recovery', async () => {
    create();
    await component.refresh();
    api.getJobQueue.mockReturnValueOnce(throwError(() => new Error('Network down')));
    await component.refresh();
    expect(component.queue()).toEqual(response);
    expect(component.error()).toBe('Network down');
    await component.refresh();
    expect(component.error()).toBe('');
  });

  it('dismisses the failed source and refreshes', async () => {
    create();
    await component.refresh();
    await component.dismiss(item);
    expect(api.dismissJob).toHaveBeenCalledWith('indexer-run', 7);
    expect(api.getJobQueue).toHaveBeenCalledTimes(2);
    expect(component.pending().size).toBe(0);
  });

  it('cancels through the existing indexer API and refreshes', async () => {
    create();
    await component.refresh();
    await component.cancel({ ...item, status: 'running', cancelable: true });
    expect(api.cancelIndexerRun).toHaveBeenCalledWith(7);
    expect(api.getJobQueue).toHaveBeenCalledTimes(2);
  });

  it('refreshes again after an overlapping pre-action poll finishes', async () => {
    create();
    await component.refresh();
    const poll = new Subject<JobQueueResponse>();
    api.getJobQueue.mockReturnValueOnce(poll);
    const refresh = component.refresh();
    const dismiss = component.dismiss(item);
    await Promise.resolve();
    expect(api.getJobQueue).toHaveBeenCalledTimes(2);
    poll.next(response);
    poll.complete();
    await refresh;
    await dismiss;
    expect(api.getJobQueue).toHaveBeenCalledTimes(3);
  });

  it('shows action errors without losing data and allows retry', async () => {
    create();
    await component.refresh();
    api.dismissJob.mockReturnValueOnce(throwError(() => ({ error: { message: 'Cannot dismiss' } })));
    await component.dismiss(item);
    expect(component.error()).toBe('Cannot dismiss');
    expect(component.queue()).toEqual(response);
    expect(component.pending().size).toBe(0);
    await component.dismiss(item);
    expect(api.dismissJob).toHaveBeenCalledTimes(2);
  });

  it('tracks failed and running rows of the same schedule separately and labels all operations', () => {
    create();
    expect(component.identity({ ...item, kind: 'scheduled-job' })).not.toBe(component.identity({ ...item, kind: 'scheduled-job', status: 'running' }));
    for (const operation of ['process_repository', 'process_repositories', 'reindex_all', 'discover', 'link', 'detect_communities', 'link_and_detect', 'process_batch_analysis', 'sync_schema', 'sync_all_schemas', 'branch_overlay', 'reanalyze', 'analysis-batch', 'Discover', 'ReIndexAll', 'ProcessBatchAnalysis', 'LinkAndDetect', 'DetectCommunities', 'RegenerateMcpDocs', 'AssistantRetentionCleanup', 'IngestConventionEmbeddings', 'PruneWorkSessions', 'PruneWorkMessages', 'WorkImport', 'IndexerRunRetention']) {
      expect(component.operationLabel(operation)).not.toBe(operation);
    }
  });
});

describe('queue UTC timestamps', () => {
  it('treats offset-free timestamps as UTC while preserving explicit offsets', () => {
    expect(parseUtcDate('2026-10-30T10:00:00')?.toISOString()).toBe('2026-10-30T10:00:00.000Z');
    expect(parseUtcDate('2026-10-30T10:00:00Z')?.toISOString()).toBe('2026-10-30T10:00:00.000Z');
    expect(parseUtcDate('2026-10-30T10:00:00-04:00')?.toISOString()).toBe('2026-10-30T14:00:00.000Z');
    expect(parseUtcDate('2026-10-30T10:00:00+0530')?.toISOString()).toBe('2026-10-30T04:30:00.000Z');
    expect(parseUtcDate(null)).toBeNull();
    expect(parseUtcDate('invalid')).toBeNull();
  });
});
