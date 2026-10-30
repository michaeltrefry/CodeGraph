import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { DreamEffects, DreamInstructionSet, DreamRevertResult, DreamRun, DreamSettings } from './dreaming.models';

/** The same Dreaming REST operations used by agents, scoped to the selected project. */
@Injectable({ providedIn: 'root' })
export class DreamingApiService {
  private readonly http = inject(HttpClient);
  private base(project: string): string { return `${environment.apiUrl}/work/dreaming/${encodeURIComponent(project)}`; }
  runs(project: string, limit = 30, before?: string): Observable<DreamRun[]> {
    let params = new HttpParams().set('limit', limit);
    if (before) params = params.set('before', before);
    return this.http.get<DreamRun[]>(`${this.base(project)}/runs`, { params });
  }
  run(project: string, id: string): Observable<DreamRun> { return this.http.get<DreamRun>(`${this.base(project)}/runs/${encodeURIComponent(id)}`); }
  runNow(project: string): Observable<DreamRun> { return this.http.post<DreamRun>(`${this.base(project)}/runs`, { trigger: 'run_now' }); }
  revert(project: string, id: string): Observable<DreamRevertResult> { return this.http.post<DreamRevertResult>(`${this.base(project)}/runs/${encodeURIComponent(id)}/revert`, {}); }
  effects(project: string, id: string): Observable<DreamEffects> { return this.http.get<DreamEffects>(`${this.base(project)}/runs/${encodeURIComponent(id)}/effects`); }
  settings(project: string): Observable<DreamSettings> { return this.http.get<DreamSettings>(`${this.base(project)}/settings`); }
  saveSettings(project: string, settings: DreamSettings): Observable<DreamSettings> { return this.http.put<DreamSettings>(`${this.base(project)}/settings`, settings); }
  instructions(project: string): Observable<DreamInstructionSet> { return this.http.get<DreamInstructionSet>(`${this.base(project)}/instructions`); }
  saveInstructions(project: string, scope: 'base' | 'project', content: string, expected_revision: number): Observable<DreamInstructionSet> {
    return this.http.put<DreamInstructionSet>(`${this.base(project)}/instructions`, { scope, content, expected_revision });
  }
}
