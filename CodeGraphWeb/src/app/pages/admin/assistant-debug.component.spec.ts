import { Injector, runInInjectionContext } from '@angular/core';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { AssistantDebugExchangeListResponse } from '../../core/models';
import { AssistantDebugComponent } from './assistant-debug.component';

describe('AssistantDebugComponent', () => {
  let api: { getAssistantDebugExchanges: ReturnType<typeof vi.fn> };
  let component: AssistantDebugComponent;

  const debug: AssistantDebugExchangeListResponse = {
    run: {
      id: 42,
      chatId: 'chat',
      username: 'michael',
      status: 'completed',
      question: 'What changed?',
      warnings: [],
      lastSequence: 3,
      createdAt: '2026-04-26T00:00:00Z'
    },
    exchanges: [
      {
        exchangeIndex: 1,
        turnIndex: 1,
        provider: 'anthropic',
        model: 'claude',
        requestBody: { messages: [] },
        responseBody: { content: [] },
        requestText: 'request',
        responseText: 'response',
        inputTokens: 10,
        outputTokens: 5,
        totalTokens: 15,
        createdAt: '2026-04-26T00:00:00Z'
      }
    ]
  };

  beforeEach(() => {
    api = {
      getAssistantDebugExchanges: vi.fn().mockReturnValue(of(debug))
    };

    const injector = Injector.create({
      providers: [{ provide: ApiService, useValue: api }]
    });

    component = runInInjectionContext(injector, () => new AssistantDebugComponent());
  });

  it('requires a positive run id', async () => {
    component.runId = 0;

    await component.load();

    expect(component.error()).toBe('Run id is required.');
    expect(api.getAssistantDebugExchanges).not.toHaveBeenCalled();
  });

  it('loads debug exchanges for a run', async () => {
    component.runId = 42;

    await component.load();

    expect(api.getAssistantDebugExchanges).toHaveBeenCalledWith(42);
    expect(component.debug()).toBe(debug);
  });
});
