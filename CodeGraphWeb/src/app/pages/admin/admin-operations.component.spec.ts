import { HttpClient } from '@angular/common/http';
import { Injector, runInInjectionContext } from '@angular/core';
import { of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AdminOperationsComponent } from './admin-operations.component';

describe('AdminOperationsComponent', () => {
  let http: { post: ReturnType<typeof vi.fn> };
  let component: AdminOperationsComponent;

  beforeEach(() => {
    http = { post: vi.fn() };
    const injector = Injector.create({
      providers: [{ provide: HttpClient, useValue: http }]
    });
    component = runInInjectionContext(injector, () => new AdminOperationsComponent());
  });

  it('reuses a logical submission key after response loss and rotates it after acknowledgement', async () => {
    http.post
      .mockReturnValueOnce(throwError(() => new Error('response lost')))
      .mockReturnValueOnce(of({ status: 'queued', runId: 42 }))
      .mockReturnValueOnce(of({ status: 'queued', runId: 43 }));

    await component.runOp('indexer/repositories/reindex-all');
    await component.runOp('indexer/repositories/reindex-all');
    await component.runOp('indexer/repositories/reindex-all');

    const firstKey = http.post.mock.calls[0][2].headers['Idempotency-Key'];
    const retryKey = http.post.mock.calls[1][2].headers['Idempotency-Key'];
    const nextOperationKey = http.post.mock.calls[2][2].headers['Idempotency-Key'];
    expect(retryKey).toBe(firstKey);
    expect(nextOperationKey).not.toBe(firstKey);
  });
});
