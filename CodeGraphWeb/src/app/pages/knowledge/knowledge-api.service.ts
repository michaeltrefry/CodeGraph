import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  ChallengeBody,
  ChallengeResult,
  FindWorkResult,
  KnowledgeEntry,
  LessonsFilters,
  LessonsResult,
  ProjectRow,
  PromoteBody,
  PromoteResult,
  RecallFilters,
  RecallResult,
  RecordBody,
  RecordResult,
  ReviewQueueResult
} from './knowledge.models';

/**
 * The knowledge, lesson and review-queue routes of WorkController, the same ones agents call
 * (E8): the pages have no write path of their own. Every write is one of record, promote or
 * challenge, with expected_version on changes and an idempotency key on creates (E1).
 */
@Injectable({ providedIn: 'root' })
export class KnowledgeApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiUrl}/work`;

  /** find_work for projects. */
  listProjects(): Observable<ProjectRow[]> {
    return this.http.post<FindWorkResult<ProjectRow>>(`${this.base}/find`, { kind: 'project', limit: 200 }).pipe(map(result => result.items));
  }

  /** recall: the filters as the body. */
  recall<T>(filters: RecallFilters): Observable<RecallResult<T>> {
    return this.http.post<RecallResult<T>>(`${this.base}/knowledge/recall`, filters);
  }

  /** recall of one entry, whole, with its revisions. */
  getEntry(id: string): Observable<KnowledgeEntry> {
    const params = new HttpParams().set('detail', 'full');
    return this.http.get<RecallResult<KnowledgeEntry>>(`${this.base}/knowledge/${encodeURIComponent(id)}`, { params })
      .pipe(map(result => result.items[0]));
  }

  /** record: entry (with supersedes), revise, retract, verify, relate, unmerge or acknowledge. */
  record(body: RecordBody): Observable<RecordResult> {
    return this.http.post<RecordResult>(`${this.base}/knowledge`, body);
  }

  /** promote: propose, approve, reject (with reason) or addressed_by. */
  promote(id: string, body: PromoteBody): Observable<PromoteResult> {
    return this.http.post<PromoteResult>(`${this.base}/knowledge/${encodeURIComponent(id)}/promote`, body);
  }

  /** challenge: open (with an idempotency key) or resolve. */
  challenge(body: ChallengeBody): Observable<ChallengeResult> {
    return this.http.post<ChallengeResult>(`${this.base}/knowledge/challenges`, body);
  }

  /** lessons: a view and its filters. */
  lessons(filters: LessonsFilters): Observable<LessonsResult> {
    return this.http.post<LessonsResult>(`${this.base}/knowledge/lessons`, filters);
  }

  reviewDreamProposal(id: string, project: string, expectedVersion: number, action: 'accept' | 'reject', reason?: string): Observable<unknown> {
    return this.http.post(`${this.base}/knowledge/dream-proposals/${encodeURIComponent(id)}/review`, {
      project, expected_version: expectedVersion, action, ...(reason ? { reason } : {})
    });
  }

  /** review_queue: project, limit and cursor as query parameters. */
  reviewQueue(project: string, limit: number, cursor?: string): Observable<ReviewQueueResult> {
    let params = new HttpParams().set('project', project).set('limit', limit);
    if (cursor) params = params.set('cursor', cursor);
    return this.http.get<ReviewQueueResult>(`${this.base}/knowledge/review-queue`, { params });
  }
}
