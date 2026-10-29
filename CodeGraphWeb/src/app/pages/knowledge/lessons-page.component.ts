import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { KnowledgeApiService } from './knowledge-api.service';
import { KnowledgeProjectService, actorLabel, actorTitle, errorText, words } from './knowledge-ui';
import { LessonActionsComponent } from './lesson-actions.component';
import {
  CORRECTION_SOURCES,
  CorrectionSourceKind,
  KnowledgeEntry,
  LESSON_ACTIVITIES,
  LessonActivity,
  LessonGroup,
  LessonRow,
  LessonRuleState,
  LessonsFilters,
  LessonsView,
  RULE_STATES,
  RecallFilters,
  UnlearnedReview
} from './knowledge.models';

export type LessonsTab = LessonsView | 'all';

export const LESSON_TABS: { id: LessonsTab; label: string; hint: string }[] = [
  { id: 'recently_learned', label: 'Recently learned', hint: 'Rules, newest first, with how each became a rule. Edit or retract one here; the next briefing reflects it.' },
  { id: 'all', label: 'All lessons', hint: 'Every lesson by source, priority, recurrence and tier.' },
  { id: 'recurrence', label: 'Served vs recurred', hint: 'Recurred while served: text alone is not working, a candidate for a local fix. Recurred while not served: its applies-when is probably wrong.' },
  { id: 'unaddressed', label: 'Not addressed by a skill', hint: 'Rules no skill, instruction, hook, test or CI gate addresses yet, grouped by activity: input for a manual skill update.' },
  { id: 'unmatched', label: 'Not served lately', hint: 'Rules no briefing served for the chosen number of days.' },
  { id: 'unlearned_reviews', label: 'Reviews with no lesson', hint: 'GitHub reviews and review comments on the project\'s items that no lesson names as its source.' }
];

const PAGE_SIZE = 20;
const FULL_PAGE_SIZE = 10;

/**
 * Lessons (§8.7): by source, priority, recurrence and tier; served vs recurred; rules not addressed
 * by a skill; "recently learned"; review comments with no lesson. The lesson actions are those of
 * agents (promote, challenge, record) through the same routes.
 */
@Component({
  selector: 'app-lessons-page',
  imports: [DatePipe, FormsModule, NgTemplateOutlet, LessonActionsComponent],
  templateUrl: './lessons-page.component.html',
  styleUrl: './knowledge.scss'
})
export class LessonsPageComponent implements OnInit {
  private readonly api = inject(KnowledgeApiService);
  readonly project = inject(KnowledgeProjectService);

  readonly tabs = LESSON_TABS;
  readonly activities = LESSON_ACTIVITIES;
  readonly sources = CORRECTION_SOURCES;
  readonly ruleStates = RULE_STATES;
  readonly actorLabel = actorLabel;
  readonly actorTitle = actorTitle;
  readonly words = words;

  readonly tab = signal<LessonsTab>('recently_learned');
  activity: LessonActivity | '' = '';
  source: CorrectionSourceKind | '' = '';
  tier: LessonRuleState | '' = '';
  text = '';
  sinceDays = 30;

  readonly rows = signal<LessonRow[]>([]);
  readonly groups = signal<LessonGroup[]>([]);
  readonly reviews = signal<UnlearnedReview[]>([]);
  readonly lessons = signal<KnowledgeEntry[]>([]);
  readonly nextCursor = signal<string | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly selected = signal<KnowledgeEntry | null>(null);

  // Per tab: the cursor of its next page and the filters its first page was read with.
  private readonly pages = new Map<LessonsTab, { cursor: string; filters: LessonsFilters | RecallFilters }>();
  private loadSequence = 0;
  private selectSequence = 0;

  async ngOnInit(): Promise<void> {
    if (await this.project.load()) await this.load();
  }

  async changeProject(key: string): Promise<void> {
    this.project.select(key);
    this.selected.set(null);
    await this.load();
  }

  async show(tab: LessonsTab): Promise<void> {
    this.tab.set(tab);
    this.nextCursor.set(this.pages.get(tab)?.cursor ?? null);
    await this.load();
  }

  hint(): string {
    return this.tabs.find(tab => tab.id === this.tab())?.hint ?? '';
  }

  lessonsFilters(): LessonsFilters {
    const view = this.tab() as LessonsView;
    const filters: LessonsFilters = { project: this.project.selected()!, view, limit: PAGE_SIZE };
    if (view !== 'unlearned_reviews') {
      if (this.activity) filters.activity = this.activity;
      if (this.source) filters.source = this.source;
    }
    if (view === 'unmatched') filters.since_days = this.sinceDays;
    return filters;
  }

  recallFilters(): RecallFilters {
    const filters: RecallFilters = { project: this.project.selected()!, kind: ['lesson'], detail: 'full', limit: FULL_PAGE_SIZE };
    if (this.activity) filters.activity = this.activity;
    if (this.source) filters.source = this.source;
    if (this.tier) filters.rule_state = [this.tier];
    if (this.text.trim()) filters.text = this.text.trim();
    return filters;
  }

  /**
   * The first page of the shown tab with the current filters, or (more) its next page with the
   * filters its first page was read with. Each tab keeps its own cursor; a response that is no
   * longer the latest request's (another tab, another filter) is dropped.
   */
  async load(more = false): Promise<void> {
    if (!this.project.selected()) return;
    const tab = this.tab();
    const kept = this.pages.get(tab);
    if (more && !kept) return;
    const sequence = ++this.loadSequence;
    this.loading.set(true);
    this.error.set(null);
    try {
      let next: string | undefined;
      let first: LessonsFilters | RecallFilters;
      if (tab === 'all') {
        first = more ? kept!.filters : this.recallFilters();
        const page = await firstValueFrom(this.api.recall<KnowledgeEntry>({ ...(first as RecallFilters), ...(more ? { cursor: kept!.cursor } : {}) }));
        if (sequence !== this.loadSequence) return;
        this.lessons.set(more ? [...this.lessons(), ...page.items] : page.items);
        next = page.next_cursor;
      } else {
        first = more ? kept!.filters : this.lessonsFilters();
        const page = await firstValueFrom(this.api.lessons({ ...(first as LessonsFilters), ...(more ? { cursor: kept!.cursor } : {}) }));
        if (sequence !== this.loadSequence) return;
        this.rows.set(more ? [...this.rows(), ...(page.items ?? [])] : page.items ?? []);
        this.groups.set(more ? mergeGroups(this.groups(), page.groups ?? []) : page.groups ?? []);
        this.reviews.set(more ? [...this.reviews(), ...(page.reviews ?? [])] : page.reviews ?? []);
        next = page.next_cursor;
      }
      if (next) this.pages.set(tab, { cursor: next, filters: first });
      else this.pages.delete(tab);
      this.nextCursor.set(next ?? null);
    } catch (error) {
      if (sequence === this.loadSequence) this.error.set(errorText(error));
    } finally {
      if (sequence === this.loadSequence) this.loading.set(false);
    }
  }

  async select(id: string): Promise<void> {
    const sequence = ++this.selectSequence;
    try {
      const entry = await firstValueFrom(this.api.getEntry(id));
      if (sequence === this.selectSequence) this.selected.set(entry);
    } catch (error) {
      if (sequence === this.selectSequence) this.error.set(errorText(error));
    }
  }

  async changed(entry: KnowledgeEntry): Promise<void> {
    this.selected.set(entry);
    await this.load();
  }

  tierLabel(state: LessonRuleState): string {
    return state === 'candidate' ? 'task-scoped' : state;
  }

  tierClass(state: LessonRuleState): string {
    return state === 'rule' ? 'cg-chip-accent' : state === 'proposed' ? 'cg-chip-warn' : state === 'rejected' ? 'cg-chip-err' : '';
  }
}

/** A next page of grouped rules merged into the groups read so far: one group per activity, no lesson twice. */
export function mergeGroups(existing: LessonGroup[], incoming: LessonGroup[]): LessonGroup[] {
  const merged = existing.map(group => ({ ...group, lessons: [...group.lessons] }));
  for (const group of incoming) {
    const same = merged.find(candidate => candidate.activity === group.activity);
    if (!same) {
      merged.push({ ...group, lessons: [...group.lessons] });
      continue;
    }
    for (const lesson of group.lessons) {
      if (!same.lessons.some(known => known.id === lesson.id)) same.lessons.push(lesson);
    }
  }
  return merged;
}
