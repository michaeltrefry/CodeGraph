import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import { ApiService } from '../../core/api.service';
import { JobQueueEntry, JobQueueResponse } from '../../core/models';
import { AdminJobQueueComponent } from './admin-job-queue.component';

const failure: JobQueueEntry = {
  kind: 'scheduled-job', id: 12, operation: 'IndexerRunRetention', target: 'weekly-retention', status: 'failed',
  requestedByUsername: null, message: null, error: 'Retention failed', createdAt: '2026-10-30T10:00:00',
  startedAt: null, nextAttemptAt: null, attemptCount: 1, cancelable: false
};

describe('admin job queue browser rendering', () => {
  afterEach(() => TestBed.resetTestingModule());
  it('renders loading, schedule retry rows, failed details, cap counts, retained data on errors, and empty state', async () => {
    const snapshots: Subject<JobQueueResponse>[] = [];
    await TestBed.configureTestingModule({
      imports: [AdminJobQueueComponent],
      providers: [{ provide: ApiService, useValue: { getJobQueue: () => {
        const snapshot = new Subject<JobQueueResponse>();
        snapshots.push(snapshot);
        return snapshot;
      } } }]
    }).compileComponents();
    const fixture = TestBed.createComponent(AdminJobQueueComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('Loading job queue');
    snapshots[0].next({ items: [
      failure,
      { ...failure, status: 'running', startedAt: '2026-10-30T10:15:00Z', error: null },
      { ...failure, kind: 'indexer-run', id: 20, operation: 'process_repository', status: 'queued', nextAttemptAt: '2026-10-30T11:00:00-04:00', cancelable: true }
    ], running: 1, queued: 500, failed: 1, generatedAt: failure.createdAt });
    snapshots[0].complete();
    await fixture.componentInstance.refresh();
    fixture.detectChanges();
    expect(element.querySelectorAll('tbody tr')).toHaveLength(3);
    expect(element.textContent).toContain('Indexer run retention');
    expect(element.textContent).toContain('Index repository');
    expect(element.querySelector('.failure')?.textContent).toContain('Retention failed');
    expect(element.textContent).toContain('Retrying at');
    expect(element.textContent).toContain('Showing 3 of 502');
    expect(element.querySelectorAll('button')).toHaveLength(2);
    const refresh = fixture.componentInstance.refresh();
    snapshots[1].error(new Error('Queue unavailable'));
    await refresh;
    fixture.detectChanges();
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Queue unavailable');
    expect(element.querySelectorAll('tbody tr')).toHaveLength(3);
    const empty = fixture.componentInstance.refresh();
    snapshots[2].next({ items: [], running: 0, queued: 0, failed: 0, generatedAt: failure.createdAt });
    snapshots[2].complete();
    await empty;
    fixture.detectChanges();
    expect(element.textContent).toContain('Nothing is running, queued or failed.');
    expect(element.textContent).not.toContain('Showing');
    fixture.destroy();
  });
});
