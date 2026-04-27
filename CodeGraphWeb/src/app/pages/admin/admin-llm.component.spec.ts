import { Injector, runInInjectionContext } from '@angular/core';
import { of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import {
  LlmAnalysisResponse,
  LlmAssistantResponse,
  LlmProviderResponse,
  LlmReviewResponse
} from '../../core/models';
import { AdminLlmComponent } from './admin-llm.component';

describe('AdminLlmComponent', () => {
  let api: {
    listLlmProviders: ReturnType<typeof vi.fn>;
    updateLlmProvider: ReturnType<typeof vi.fn>;
    listLlmProviderModels: ReturnType<typeof vi.fn>;
    getLlmAnalysis: ReturnType<typeof vi.fn>;
    updateLlmAnalysis: ReturnType<typeof vi.fn>;
    getLlmReview: ReturnType<typeof vi.fn>;
    updateLlmReview: ReturnType<typeof vi.fn>;
    getLlmAssistant: ReturnType<typeof vi.fn>;
    updateLlmAssistant: ReturnType<typeof vi.fn>;
  };
  let component: AdminLlmComponent;

  const providers: LlmProviderResponse[] = [
    {
      provider: 'anthropic',
      hasToken: false,
      endpointUrl: undefined,
      apiVersion: '2023-06-01',
      models: ['claude-sonnet-4-6']
    },
    {
      provider: 'openai',
      hasToken: true,
      endpointUrl: 'https://api.openai.com/v1',
      models: ['gpt-5.2']
    },
    {
      provider: 'lmstudio',
      hasToken: false,
      models: []
    }
  ];

  const analysis: LlmAnalysisResponse = {
    defaultProvider: 'anthropic',
    defaultModel: 'claude-sonnet-4-6',
    maxTokensPerAnalysis: 1000,
    maxTokensPerSynthesis: 2000,
    maxFileSizeKb: 300,
    maxParallelAnalyses: 4,
    maxSourceChars: 5000
  };

  const review: LlmReviewResponse = {
    defaultProvider: 'anthropic',
    defaultModel: 'claude-sonnet-4-6',
    maxFilesToInspect: 20,
    maxSourceCharsPerFile: 4000,
    maxInspectionPasses: 2,
    maxFindings: 10
  };

  const assistant: LlmAssistantResponse = {
    defaultProvider: 'anthropic',
    defaultModel: 'claude-sonnet-4-6',
    maxTokens: 1200,
    maxTurns: 8
  };

  beforeEach(() => {
    api = {
      listLlmProviders: vi.fn().mockReturnValue(of(providers)),
      updateLlmProvider: vi.fn().mockReturnValue(of({ ...providers[0], hasToken: true })),
      listLlmProviderModels: vi.fn().mockReturnValue(of([
        { provider: 'anthropic', model: 'claude-sonnet-4-6' },
        { provider: 'openai', model: 'gpt-5.2' }
      ])),
      getLlmAnalysis: vi.fn().mockReturnValue(of(analysis)),
      updateLlmAnalysis: vi.fn().mockReturnValue(of(analysis)),
      getLlmReview: vi.fn().mockReturnValue(of(review)),
      updateLlmReview: vi.fn().mockReturnValue(of(review)),
      getLlmAssistant: vi.fn().mockReturnValue(of(assistant)),
      updateLlmAssistant: vi.fn().mockReturnValue(of(assistant))
    };

    const injector = Injector.create({
      providers: [{ provide: ApiService, useValue: api }]
    });

    component = runInInjectionContext(injector, () => new AdminLlmComponent());
  });

  it('loads providers, catalog, and runtime defaults', async () => {
    await component.ngOnInit();

    expect(component.providers.map(provider => provider.displayName)).toEqual([
      'Anthropic',
      'OpenAI compatible',
      'LM Studio'
    ]);
    expect(component.providerModelCount()).toBe(2);
    expect(component.analysis?.defaultModel).toBe('claude-sonnet-4-6');
    expect(component.review?.maxFindings).toBe(10);
    expect(component.assistant?.maxTurns).toBe(8);
  });

  it('saves provider token replacement and model list changes', async () => {
    await component.ngOnInit();
    const provider = component.providers[0];
    component.setTokenMode(provider, 'Replace');
    provider.tokenValue = 'new-token';
    provider.endpointUrl = ' https://api.anthropic.com ';
    provider.newModel = 'claude-opus-4-6';
    component.addProviderModel(provider);

    await component.saveProvider(provider);

    expect(api.updateLlmProvider).toHaveBeenCalledWith('anthropic', {
      endpointUrl: 'https://api.anthropic.com',
      apiVersion: '2023-06-01',
      models: ['claude-sonnet-4-6', 'claude-opus-4-6'],
      token: { action: 'Replace', value: 'new-token' }
    });
    expect(component.providers[0].hasToken).toBe(true);
    expect(component.providers[0].message).toBe('Provider saved.');
  });

  it('maps field-keyed validation errors onto default model controls', async () => {
    await component.ngOnInit();
    api.updateLlmAnalysis.mockReturnValue(throwError(() => ({
      error: {
        errors: {
          default_model: ['model missing is not in the configured model list for anthropic']
        }
      }
    })));

    await component.saveAnalysis();

    expect(component.fieldError('analysis', 'defaultModel')).toContain('model missing');
    expect(component.sectionError.analysis).toBe('Provider and model selection could not be saved.');
  });
});
