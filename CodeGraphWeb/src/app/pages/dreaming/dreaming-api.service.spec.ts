import { HttpClient, HttpParams } from '@angular/common/http';
import { Injector, runInInjectionContext } from '@angular/core';
import { firstValueFrom, of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { environment } from '../../../environments/environment';
import { DreamingApiService } from './dreaming-api.service';
import { DreamSettings } from './dreaming.models';

describe('DreamingApiService', () => {
  it('uses the shared project-scoped routes and URL-encodes IDs', async () => {
    const http = { get: vi.fn().mockReturnValue(of([])), post: vi.fn().mockReturnValue(of({})), put: vi.fn().mockReturnValue(of({})) };
    const injector = Injector.create({ providers: [{ provide: HttpClient, useValue: http }] });
    const api = runInInjectionContext(injector, () => new DreamingApiService());
    const base = `${environment.apiUrl}/work/dreaming/ONE%2FTWO`;
    await firstValueFrom(api.runs('ONE/TWO', 60));
    const [url, options] = http.get.mock.calls.at(-1)! as [string, { params: HttpParams }];
    expect(url).toBe(`${base}/runs`); expect(options.params.get('limit')).toBe('60');
    await firstValueFrom(api.run('ONE/TWO', 'run/one')); expect(http.get).toHaveBeenLastCalledWith(`${base}/runs/run%2Fone`);
    await firstValueFrom(api.runNow('ONE/TWO')); expect(http.post).toHaveBeenLastCalledWith(`${base}/runs`, { trigger: 'run_now' });
    await firstValueFrom(api.revert('ONE/TWO', 'run/one')); expect(http.post).toHaveBeenLastCalledWith(`${base}/runs/run%2Fone/revert`, {});
    await firstValueFrom(api.effects('ONE/TWO', 'run/one')); expect(http.get).toHaveBeenLastCalledWith(`${base}/runs/run%2Fone/effects`);
    await firstValueFrom(api.instructions('ONE/TWO')); expect(http.get).toHaveBeenLastCalledWith(`${base}/instructions`);
    await firstValueFrom(api.saveInstructions('ONE/TWO', 'project', '', 0)); expect(http.put).toHaveBeenLastCalledWith(`${base}/instructions`, { scope: 'project', content: '', expected_revision: 0 });
    const settings = { enabled: false } as DreamSettings;
    await firstValueFrom(api.saveSettings('ONE/TWO', settings)); expect(http.put).toHaveBeenLastCalledWith(`${base}/settings`, settings);
  });
});
