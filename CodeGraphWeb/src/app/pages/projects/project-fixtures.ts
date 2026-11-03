// Fixtures of the project page specs.
import { signal } from '@angular/core';
import { RepositoryReviewResponse, RepositoryReviewRunResponse } from '../../core/models';
import { FeatureView, ProjectView } from '../work/work.models';

export const project: ProjectView = {
  kind: 'project', id: 'p', key: 'W', name: 'Workbench', description: 'Coordinates agent work.\nAnd people.', goals: ['Ship the UI', 'Keep it live'],
  repositories: [{ repository: 'codegraph', role: 'owned' }, { repository: 'shared-lib', role: 'dependency' }],
  version: 2, created_by: 'michael', created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z',
  item_counts: { complete: 3, closed: 1, in_progress: 2, in_review: 1, ready: 3 }
};

function run(over: Partial<RepositoryReviewRunResponse>): RepositoryReviewRunResponse {
  return { id: 1, repo: 'codegraph', status: 'completed', reviewMode: 'full', promptVersion: '1', createdAt: '2026-01-02T00:00:00Z', completedAt: '2026-01-02T01:00:00Z', ...over };
}

export function review(over: Partial<RepositoryReviewRunResponse>, severities: string[] = []): RepositoryReviewResponse {
  return {
    run: run(over),
    overview: '',
    findings: severities.map(severity => ({ severity, category: 'c', title: 't', explanation: '', evidence: '', filePath: 'a.cs', suggestedImprovement: '', confidence: 'high' })),
    strengths: [], reviewedAreas: [], skippedAreas: [], followUps: [], projectReviews: []
  };
}

export async function settle(fixture: { detectChanges(): void }): Promise<void> {
  for (let round = 0; round < 5; round++) {
    await new Promise(resolve => setTimeout(resolve, 0));
    fixture.detectChanges();
  }
}

/** A change feed that never has news. */
export const fakeLive = {
  acquire: () => ({ watch: { loading: signal(false), error: signal(''), onChange: () => () => undefined, stop: () => undefined }, release: () => undefined }),
  streamMessages: () => () => undefined
};

export function feature(key: string, over: Partial<FeatureView> = {}): FeatureView {
  return {
    kind: 'feature', id: key, key, project_id: 'p', project_key: 'W', title: `Feature ${key}`, documents: [], version: 1, created_by: 'michael',
    created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z', status: 'open', ...over
  };
}

/** Five features with distinct values on every sort key except where a tie is wanted. */
export function featureSet(): FeatureView[] {
  return [
    feature('W-F2', { title: 'Search box', created_by: 'alice', created_at: '2026-01-02T00:00:00Z', updated_at: '2026-01-05T00:00:00Z',
      item_counts: { complete: 1, ready: 3 }, completion: { items: 4, finished: 1, complete: 1, uncovered_requirements: ['E1', 'E2'] }, blocked_count: 2 }),
    feature('W-F10', { title: 'Billing', created_at: '2026-01-04T00:00:00Z', updated_at: '2026-01-02T00:00:00Z',
      item_counts: { complete: 2, closed: 2 }, status: 'complete' }),
    feature('W-F1', { title: 'Login flow', created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-04T00:00:00Z',
      item_counts: { complete: 1, in_progress: 1, in_review: 1, backlog: 1 }, completion: { items: 4, finished: 1, complete: 1, uncovered_requirements: ['E3'] } }),
    feature('W-F3', { title: 'Empty feature', created_by: 'bob', created_at: '2026-01-03T00:00:00Z', updated_at: '2026-01-03T00:00:00Z' }),
    feature('W-F4', { title: 'Search results', created_by: 'alice', created_at: '2026-01-05T00:00:00Z', updated_at: '2026-01-01T00:00:00Z',
      item_counts: { in_progress: 2, ready: 2 } })
  ];
}
