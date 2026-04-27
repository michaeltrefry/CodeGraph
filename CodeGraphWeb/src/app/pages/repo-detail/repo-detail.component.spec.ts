import { DestroyRef, Injector, runInInjectionContext } from '@angular/core';
import { DomSanitizer } from '@angular/platform-browser';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { ChatContextService } from '../../core/chat-context.service';
import {
  AnalysisBatchStatus,
  ProjectDetailResponse,
  ProjectHealthResponse,
  ProjectSecurityResponse
} from '../../core/models';
import { RepoDetailComponent } from './repo-detail.component';

function createDetail(name: string): ProjectDetailResponse {
  return {
    project: {
      name,
      isFoundational: false,
      properties: {}
    },
    analyses: [],
    nodeCounts: {},
    dotnetProjects: {},
    inboundEdgeCount: 0,
    outboundEdgeCount: 0,
    inboundProjects: [],
    outboundProjects: []
  };
}

function createHealth(name: string): ProjectHealthResponse {
  return {
    repoHealth: {
      project: name,
      overallHealth: 8.5,
      totalFiles: 1,
      hotspotCount: 0,
      alertCount: 0,
      computedAt: '2026-04-26T00:00:00Z',
      baseOverallHealth: 8.5,
      scorePenalty: 0
    },
    projectHealths: [],
    topHotspots: [],
    analyses: []
  };
}

function createSecurity(project: string): ProjectSecurityResponse {
  return {
    project,
    securityScore: 9,
    criticalCount: 0,
    highCount: 0,
    mediumCount: 0,
    lowCount: 0,
    findings: [],
    computedAt: '2026-04-26T00:00:00Z'
  };
}

function createBatchStatus(repo: string): AnalysisBatchStatus {
  return {
    id: 1,
    repo,
    providerBatchId: `${repo}-batch`,
    providerName: 'local',
    executionMode: 'direct',
    includeAllSource: false,
    status: 'completed',
    submittedAt: '2026-04-26T00:00:00Z',
    completedAt: '2026-04-26T00:01:00Z',
    requestCount: 0,
    completedCount: 0
  };
}

function createComponent() {
  const paramMap$ = new Subject<ReturnType<typeof convertToParamMap>>();
  const detailSubjects = new Map<string, Subject<ProjectDetailResponse>>();
  const healthSubjects = new Map<string, Subject<ProjectHealthResponse>>();
  const securitySubjects = new Map<string, Subject<ProjectSecurityResponse>>();
  const readmeSubjects = new Map<string, Subject<{ content: string }>>();
  const batchSubjects = new Map<string, Subject<AnalysisBatchStatus>>();

  const getSubject = <T>(subjects: Map<string, Subject<T>>, name: string) => {
    const subject = subjects.get(name) ?? new Subject<T>();
    subjects.set(name, subject);
    return subject;
  };

  const api = {
    getProject: vi.fn((name: string) => getSubject(detailSubjects, name).asObservable()),
    getProjectHealth: vi.fn((name: string) => getSubject(healthSubjects, name).asObservable()),
    getProjectSecurity: vi.fn((name: string) => getSubject(securitySubjects, name).asObservable()),
    getProjectReadme: vi.fn((name: string) => getSubject(readmeSubjects, name).asObservable()),
    getBatchStatus: vi.fn((name: string) => getSubject(batchSubjects, name).asObservable()),
    getLatestRepositoryReview: vi.fn().mockReturnValue(throwError(() => ({ status: 404 }))),
    getRepositoryReview: vi.fn(),
    getProjectDiagnostics: vi.fn().mockReturnValue(of({
      project: 'repo',
      errorCount: 0,
      warningCount: 0,
      infoCount: 0,
      diagnostics: []
    })),
    reAnalyze: vi.fn(),
    deleteProject: vi.fn(),
    streamRepositoryReview: vi.fn()
  };

  const sanitizer = {
    bypassSecurityTrustHtml: vi.fn((html: string) => html)
  };

  const injector = Injector.create({
    providers: [
      { provide: ApiService, useValue: api },
      { provide: ChatContextService, useValue: { setRepo: vi.fn() } },
      { provide: Router, useValue: { navigate: vi.fn() } },
      { provide: ActivatedRoute, useValue: { paramMap: paramMap$.asObservable() } },
      { provide: DomSanitizer, useValue: sanitizer },
      { provide: DestroyRef, useValue: { onDestroy: vi.fn() } }
    ]
  });

  const component = runInInjectionContext(injector, () => new RepoDetailComponent());

  return {
    component,
    paramMap$,
    detailSubjects,
    healthSubjects,
    securitySubjects,
    readmeSubjects,
    batchSubjects
  };
}

describe('RepoDetailComponent', () => {
  it('ignores stale repository detail responses after a faster route change', () => {
    const {
      component,
      paramMap$,
      detailSubjects,
      healthSubjects,
      securitySubjects,
      readmeSubjects,
      batchSubjects
    } = createComponent();

    component.ngOnInit();
    paramMap$.next(convertToParamMap({ name: 'repo-a' }));
    paramMap$.next(convertToParamMap({ name: 'repo-b' }));

    detailSubjects.get('repo-b')?.next(createDetail('repo-b'));
    healthSubjects.get('repo-b')?.next(createHealth('repo-b'));
    securitySubjects.get('repo-b')?.next(createSecurity('repo-b'));
    readmeSubjects.get('repo-b')?.next({ content: 'repo-b readme' });
    batchSubjects.get('repo-b')?.next(createBatchStatus('repo-b'));

    detailSubjects.get('repo-a')?.next(createDetail('repo-a'));
    healthSubjects.get('repo-a')?.next(createHealth('repo-a'));
    securitySubjects.get('repo-a')?.next(createSecurity('repo-a'));
    readmeSubjects.get('repo-a')?.next({ content: 'repo-a readme' });
    batchSubjects.get('repo-a')?.next(createBatchStatus('repo-a'));

    expect(component.name()).toBe('repo-b');
    expect(component.detail()?.project.name).toBe('repo-b');
    expect(component.health()?.repoHealth?.project).toBe('repo-b');
    expect(component.security()?.project).toBe('repo-b');
    expect(component.readme()).toBe('repo-b readme');
    expect(component.batchStatus()?.repo).toBe('repo-b');
    expect(component.loading()).toBe(false);
  });
});
