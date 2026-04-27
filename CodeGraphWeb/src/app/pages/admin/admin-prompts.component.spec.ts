import { Injector, runInInjectionContext } from '@angular/core';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { AgentPromptGroupResponse, AgentPromptResponse } from '../../core/models';
import { AdminPromptsComponent } from './admin-prompts.component';

describe('AdminPromptsComponent', () => {
  let api: {
    getAdminPrompts: ReturnType<typeof vi.fn>;
    updateAdminPrompt: ReturnType<typeof vi.fn>;
    resetAdminPrompt: ReturnType<typeof vi.fn>;
  };
  let component: AdminPromptsComponent;

  const prompt: AgentPromptResponse = {
    key: 'assistant.system',
    category: 'assistant',
    categoryDisplayName: 'Assistant',
    displayName: 'System Prompt',
    description: 'Ask prompt',
    defaultText: 'default prompt',
    effectiveText: 'custom prompt',
    hasOverride: true,
    updatedBy: 'michael',
    updatedAt: '2026-04-26T00:00:00Z'
  };

  const groups: AgentPromptGroupResponse[] = [
    { category: 'assistant', categoryDisplayName: 'Assistant', prompts: [prompt] }
  ];

  beforeEach(() => {
    api = {
      getAdminPrompts: vi.fn().mockReturnValue(of(groups)),
      updateAdminPrompt: vi.fn().mockReturnValue(of({ ...prompt, effectiveText: 'new prompt' })),
      resetAdminPrompt: vi.fn().mockReturnValue(of(void 0))
    };

    const injector = Injector.create({
      providers: [{ provide: ApiService, useValue: api }]
    });

    component = runInInjectionContext(injector, () => new AdminPromptsComponent());
  });

  it('loads grouped prompts into editor state', async () => {
    await component.ngOnInit();

    expect(component.promptCount()).toBe(1);
    expect(component.groups()[0].prompts[0].draftText).toBe('custom prompt');
  });

  it('saves prompt edits', async () => {
    await component.ngOnInit();
    component.updateDraft(prompt.key, 'new prompt');

    await component.save(component.groups()[0].prompts[0]);

    expect(api.updateAdminPrompt).toHaveBeenCalledWith(prompt.key, 'new prompt');
    expect(component.groups()[0].prompts[0].message).toBe('Prompt saved.');
  });

  it('resets an override to the code default', async () => {
    vi.stubGlobal('confirm', vi.fn(() => true));
    await component.ngOnInit();

    await component.reset(component.groups()[0].prompts[0]);

    expect(api.resetAdminPrompt).toHaveBeenCalledWith(prompt.key);
    expect(component.groups()[0].prompts[0].effectiveText).toBe('default prompt');
    expect(component.groups()[0].prompts[0].hasOverride).toBe(false);
  });
});
