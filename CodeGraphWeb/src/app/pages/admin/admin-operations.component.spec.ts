import { HttpClient } from '@angular/common/http';
import { DOCUMENT } from '@angular/common';
import { Injector, runInInjectionContext } from '@angular/core';
import { of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AdminOperationsComponent } from './admin-operations.component';

describe('AdminOperationsComponent', () => {
  let http: { post: ReturnType<typeof vi.fn> };
  let component: AdminOperationsComponent;

  function createComponent(documentValue: Document = document): AdminOperationsComponent {
    const injector = Injector.create({
      providers: [
        { provide: HttpClient, useValue: http },
        { provide: DOCUMENT, useValue: documentValue }
      ]
    });
    return runInInjectionContext(injector, () => new AdminOperationsComponent());
  }

  beforeEach(() => {
    localStorage.clear();
    http = { post: vi.fn() };
    component = createComponent();
  });

  it('reuses a logical submission key across component reload after response loss and rotates after acknowledgement', async () => {
    http.post
      .mockReturnValueOnce(throwError(() => new Error('response lost')))
      .mockReturnValueOnce(of({ status: 'queued', runId: 42 }))
      .mockReturnValueOnce(of({ status: 'queued', runId: 43 }));

    await component.runOp('indexer/repositories/reindex-all');
    component = createComponent();
    await component.runOp('indexer/repositories/reindex-all');
    component = createComponent();
    await component.runOp('indexer/repositories/reindex-all');

    const firstKey = http.post.mock.calls[0][2].headers['Idempotency-Key'];
    const retryKey = http.post.mock.calls[1][2].headers['Idempotency-Key'];
    const nextOperationKey = http.post.mock.calls[2][2].headers['Idempotency-Key'];
    expect(retryKey).toBe(firstKey);
    expect(nextOperationKey).not.toBe(firstKey);
  });

  it('fails closed without sending when durable submission storage is unavailable', async () => {
    const unavailableDocument = {
      defaultView: {
        get localStorage(): Storage {
          throw new Error('storage disabled');
        }
      }
    } as Document;
    component = createComponent(unavailableDocument);

    await component.runOp('indexer/repositories/reindex-all');

    expect(http.post).not.toHaveBeenCalled();
    expect(component.result()?.success).toBe(false);
    expect(component.result()?.message).toContain('request was not sent');
  });
});
