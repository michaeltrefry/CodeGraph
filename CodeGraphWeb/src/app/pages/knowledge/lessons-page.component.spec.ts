import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { KnowledgeApiService } from './knowledge-api.service';
import { LessonsPageComponent, mergeGroups } from './lessons-page.component';
import { AGENT, lessonRow, rule } from './knowledge-fixtures';
import { LessonsFilters, LessonsResult } from './knowledge.models';

describe('LessonsPageComponent', () => {
  let api: { [K in 'listProjects' | 'recall' | 'getEntry' | 'record' | 'lessons' | 'promote' | 'challenge']: ReturnType<typeof vi.fn> };

  async function create() {
    await TestBed.configureTestingModule({
      imports: [LessonsPageComponent],
      providers: [{ provide: KnowledgeApiService, useValue: api }]
    }).compileComponents();
    const fixture = TestBed.createComponent(LessonsPageComponent);
    fixture.detectChanges();
    for (let i = 0; i < 5; i++) await new Promise(resolve => setTimeout(resolve));
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    api = {
      listProjects: vi.fn().mockReturnValue(of([{ id: 'p', key: 'CG', kind: 'project', title: 'CodeGraph' }])),
      lessons: vi.fn((filters: { view: string }) => of(
        filters.view === 'unaddressed' ? { view: filters.view, groups: [{ activity: 'merging', lessons: [lessonRow()] }] }
          : filters.view === 'unlearned_reviews'
            ? { view: filters.view, reviews: [{ evidence_id: 'g-1', item: 'CG-4', kind: 'review_comment', author_login: 'octocat', source: 'review', priority: 'high', created_at: '2026-10-28T09:00:00Z', body: 'Rename this' }] }
            : { view: filters.view, items: [lessonRow()] })),
      recall: vi.fn().mockReturnValue(of({ items: [rule({ author: AGENT })] })),
      getEntry: vi.fn().mockReturnValue(of(rule())),
      record: vi.fn().mockReturnValue(of({ created: false, entry: rule({ status: 'retracted' }) })),
      promote: vi.fn(),
      challenge: vi.fn()
    };
  });

  it('opens on recently learned rules with recurrence, last served and the actor', async () => {
    const fixture = await create();

    expect(api.lessons).toHaveBeenCalledWith({ project: 'CG', view: 'recently_learned', limit: 20 });
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Never push to main');
    expect(text).toContain('from human prompt');
    expect(text).toContain('2 recurred while served');
    expect(text).toContain('1 while not served');
    expect(text).toContain('last served never');
    expect(text).toContain('michael');
  });

  it('reads each view with its filters', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;

    page.activity = 'merging';
    page.source = 'review';
    await page.show('recurrence');
    expect(api.lessons).toHaveBeenLastCalledWith({ project: 'CG', view: 'recurrence', limit: 20, activity: 'merging', source: 'review' });

    page.sinceDays = 90;
    await page.show('unmatched');
    expect(api.lessons).toHaveBeenLastCalledWith({ project: 'CG', view: 'unmatched', limit: 20, activity: 'merging', source: 'review', since_days: 90 });

    await page.show('unaddressed');
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('merging');
    expect(page.groups()[0].lessons.length).toBe(1);

    await page.show('unlearned_reviews');
    expect(api.lessons).toHaveBeenLastCalledWith({ project: 'CG', view: 'unlearned_reviews', limit: 20 });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('octocat');
  });

  it('lists every lesson by source, tier and activity through recall', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    page.tier = 'proposed';
    page.source = 'prompt';
    page.text = 'push';
    await page.show('all');
    fixture.detectChanges();

    expect(api.recall).toHaveBeenLastCalledWith({ project: 'CG', kind: ['lesson'], detail: 'full', limit: 10, source: 'prompt', rule_state: ['proposed'], text: 'push' });
    expect(fixture.nativeElement.textContent).toContain('claude-code / opus @ dev-mac');
  });

  it('retracts a recently learned rule from its detail and reloads the list', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    await page.select('r-1');
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Release branches need it');

    await page.changed(rule({ status: 'retracted' }));
    expect(page.selected()?.status).toBe('retracted');
    expect(api.lessons).toHaveBeenCalledTimes(2);
  });

  it('drops the answer of a tab left before it arrived', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    const slow = new Subject<LessonsResult>();
    api.lessons.mockImplementationOnce(() => slow);

    const left = page.show('recurrence');
    await page.show('unmatched');
    slow.next({ view: 'recurrence', items: [lessonRow({ id: 'stale', summary: 'From the tab left' })] });
    slow.complete();
    await left;

    expect(page.tab()).toBe('unmatched');
    expect(page.rows().map(row => row.id)).toEqual(['r-1']);
  });

  it('reads the next page of a tab with its own cursor and the filters of its first page', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    api.lessons.mockImplementation((filters: LessonsFilters) => of({ view: filters.view, items: [lessonRow()], next_cursor: `${filters.view}-2` }));
    await page.show('recurrence');
    await page.show('unmatched');
    await page.show('recurrence');

    page.activity = 'testing';
    await page.load(true);
    expect(api.lessons).toHaveBeenLastCalledWith({ project: 'CG', view: 'recurrence', limit: 20, cursor: 'recurrence-2' });
  });

  it('merges grouped pages by activity', () => {
    const merged = mergeGroups(
      [{ activity: 'merging', lessons: [lessonRow({ id: 'a' })] }],
      [{ activity: 'merging', lessons: [lessonRow({ id: 'a' }), lessonRow({ id: 'b' })] }, { activity: 'testing', lessons: [lessonRow({ id: 'c' })] }]);
    expect(merged.map(group => [group.activity, group.lessons.map(lesson => lesson.id)])).toEqual([['merging', ['a', 'b']], ['testing', ['c']]]);
  });
});
