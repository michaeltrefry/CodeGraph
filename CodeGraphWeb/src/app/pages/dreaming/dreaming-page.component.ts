import { CurrencyPipe, DatePipe, JsonPipe } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { KnowledgeProjectService, actorLabel, workError, words } from '../knowledge/knowledge-ui';
import { DreamingApiService } from './dreaming-api.service';
import { DreamEffects, DreamInstructionSet, DreamRevertResult, DreamRun, DreamSettings } from './dreaming.models';

@Component({
  selector: 'app-dreaming-page',
  imports: [CurrencyPipe, DatePipe, JsonPipe, FormsModule, RouterLink],
  templateUrl: './dreaming-page.component.html',
  styleUrls: ['../knowledge/knowledge.scss', './dreaming-page.component.scss']
})
export class DreamingPageComponent implements OnInit {
  private readonly api = inject(DreamingApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  readonly project = inject(KnowledgeProjectService);
  readonly words = words;
  readonly actorLabel = actorLabel;
  readonly runs = signal<DreamRun[]>([]);
  readonly selected = signal<DreamRun | null>(null);
  readonly effects = signal<DreamEffects | null>(null);
  readonly effectsError = signal('');
  readonly instructions = signal<DreamInstructionSet | null>(null);
  readonly revertResult = signal<DreamRevertResult | null>(null);
  readonly tab = signal<'runs' | 'settings' | 'instructions'>('runs');
  readonly loading = signal(false);
  readonly detailLoading = signal(false);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly notice = signal('');
  readonly confirmRevert = signal(false);
  readonly counts = computed(() => this.actionCounts(this.selected()));
  settings: DreamSettings | null = null;
  baseContent = '';
  projectContent = '';
  limit = 30;
  private generation = 0;
  private detailGeneration = 0;

  async ngOnInit(): Promise<void> {
    if (!await this.project.load()) return;
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(params => {
      const key = params.get('key');
      if (key) this.project.select(key);
      void this.changeProject(this.project.selected()!);
    });
  }

  async changeProject(key: string): Promise<void> {
    this.project.select(key);
    ++this.generation;
    ++this.detailGeneration;
    this.selected.set(null);
    this.effects.set(null);
    this.effectsError.set('');
    this.revertResult.set(null);
    this.confirmRevert.set(false);
    this.runs.set([]);
    this.instructions.set(null);
    this.settings = null;
    this.baseContent = this.projectContent = '';
    this.limit = 30;
    this.notice.set('');
    await this.reload();
  }

  async reload(): Promise<void> {
    const key = this.project.selected();
    if (!key) return;
    const generation = ++this.generation;
    this.loading.set(true);
    this.error.set('');
    try {
      const [runs, settings, instructions] = await Promise.all([
        firstValueFrom(this.api.runs(key, this.limit)), firstValueFrom(this.api.settings(key)), firstValueFrom(this.api.instructions(key))
      ]);
      if (generation !== this.generation) return;
      this.runs.set(runs);
      this.settings = { ...settings };
      this.setInstructions(instructions);
    } catch (error) {
      if (generation === this.generation) this.error.set(workError(error).message);
    } finally {
      if (generation === this.generation) this.loading.set(false);
    }
  }

  async more(): Promise<void> { this.limit += 30; await this.reload(); }

  async select(id: string): Promise<void> {
    const key = this.project.selected()!;
    const generation = ++this.detailGeneration;
    this.selected.set(null);
    this.effects.set(null);
    this.effectsError.set('');
    this.revertResult.set(null);
    this.confirmRevert.set(false);
    this.detailLoading.set(true);
    try {
      const run = await firstValueFrom(this.api.run(key, id));
      if (generation !== this.detailGeneration) return;
      this.selected.set(run);
      try {
        const effects = await firstValueFrom(this.api.effects(key, id));
        if (generation === this.detailGeneration) this.effects.set(effects);
      } catch (error) {
        if (generation === this.detailGeneration) this.effectsError.set(workError(error).message);
      }
    } catch (error) {
      if (generation === this.detailGeneration) this.error.set(workError(error).message);
    } finally {
      if (generation === this.detailGeneration) this.detailLoading.set(false);
    }
  }

  async runNow(): Promise<void> {
    const key = this.project.selected()!;
    await this.write(async () => {
      const run = await firstValueFrom(this.api.runNow(key));
      if (key !== this.project.selected()) return;
      await this.reload();
      await this.select(run.id);
      this.notice.set(`Run ${words(run.status)}${run.reason ? ': ' + run.reason : '.'}`);
    });
  }

  async revert(): Promise<void> {
    const key = this.project.selected()!;
    const run = this.selected();
    if (!run || !this.confirmRevert()) return;
    await this.write(async () => {
      const result = await firstValueFrom(this.api.revert(key, run.id));
      if (key !== this.project.selected() || this.selected()?.id !== run.id) return;
      this.revertResult.set(result);
      this.confirmRevert.set(false);
      this.notice.set('Revert finished. Check each result below; conflicts keep the later edit.');
    });
  }

  async saveSettings(): Promise<void> {
    if (!this.settings) return;
    const key = this.project.selected()!;
    const settings = { ...this.settings };
    if (!settings.default_provider.trim() || !settings.default_model.trim() || !settings.schedule.trim() ||
      !Number.isInteger(settings.max_turns) || settings.max_turns < 1 || !Number.isInteger(settings.max_tokens_per_run) || settings.max_tokens_per_run < 1 ||
      !Number.isInteger(settings.minimum_interval_minutes) || settings.minimum_interval_minutes < 0 ||
      !Number.isFinite(settings.input_cost_per_million) || settings.input_cost_per_million < 0 ||
      !Number.isFinite(settings.output_cost_per_million) || settings.output_cost_per_million < 0) {
      this.error.set('Supply a provider, model and schedule, positive whole-number budgets, a non-negative whole-number interval and non-negative token prices.');
      return;
    }
    await this.write(async () => {
      const saved = await firstValueFrom(this.api.saveSettings(key, settings));
      if (key === this.project.selected()) { this.settings = { ...saved }; this.notice.set('Dreaming settings saved.'); }
    });
  }

  async saveInstructions(scope: 'base' | 'project'): Promise<void> {
    const current = this.instructions()?.[scope];
    if (!current) return;
    const key = this.project.selected()!;
    const content = scope === 'base' ? this.baseContent : this.projectContent;
    if (scope === 'base' && !content.trim()) { this.error.set('Base instructions cannot be empty.'); return; }
    await this.write(async () => {
      try {
        await firstValueFrom(this.api.saveInstructions(key, scope, content, current.revision));
      } catch (error) {
        if (workError(error).code === 'conflict' && key === this.project.selected()) {
          this.setInstructions(await firstValueFrom(this.api.instructions(key)));
          throw new Error('Instructions changed since you read them. The current revisions have been reloaded; review them before editing again.');
        }
        throw error;
      }
      if (key !== this.project.selected()) return;
      this.setInstructions(await firstValueFrom(this.api.instructions(key)));
      this.notice.set(`${scope === 'base' ? 'Base instructions' : 'Project addendum'} saved with revision history.`);
    });
  }

  actionCounts(run: DreamRun | null): { action: string; count: number }[] {
    const counts = new Map<string, number>();
    for (const action of run?.actions ?? []) counts.set(action.tool, (counts.get(action.tool) ?? 0) + 1);
    return [...counts].map(([action, count]) => ({ action, count }));
  }

  private setInstructions(instructions: DreamInstructionSet): void {
    this.instructions.set(instructions);
    this.baseContent = instructions.base.content;
    this.projectContent = instructions.project.content;
  }

  private async write(action: () => Promise<void>): Promise<void> {
    if (this.busy()) return;
    const generation = this.generation;
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    try { await action(); }
    catch (error) { if (generation === this.generation) this.error.set(workError(error).message); }
    finally { this.busy.set(false); }
  }
}
