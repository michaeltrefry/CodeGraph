import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { ProjectHealthResponse } from '../../core/models';
import { RepoDetailHealthComponent } from './repo-detail-health.component';

function createHealth(overrides?: Partial<ProjectHealthResponse>): ProjectHealthResponse {
  return {
    repoHealth: {
      project: 'TC.CodeGraphApi',
      overallHealth: 8.7,
      totalFiles: 42,
      hotspotCount: 3,
      alertCount: 1,
      computedAt: '2026-04-12T00:00:00Z',
      baseOverallHealth: 8.7,
      scorePenalty: 0
    },
    projectHealths: [],
    topHotspots: [],
    analyses: [],
    repositoryVitality: {
      historyMaturity: 'Mature',
      hasSufficientHistoryForTrends: true,
      activityStatus: 'sustained',
      firefightingStatus: 'elevated',
      monthlyCommits: [
        { month: '2026-01-01T00:00:00Z', commitCount: 2 },
        { month: '2026-02-01T00:00:00Z', commitCount: 0 },
        { month: '2026-03-01T00:00:00Z', commitCount: 5 }
      ],
      velocityLast6Months: 24,
      velocityPrior6Months: 16,
      velocityChangePercent: 50,
      dormantMonths12m: 1,
      maxInactiveStreakMonths: 1,
      firefightingCommits90d: 4,
      firefightingCommits365d: 11,
      firefightingRate90d: 0.25,
      firefightingRate365d: 0.18
    },
    ...overrides
  };
}

async function createComponent(health: ProjectHealthResponse) {
  await TestBed.configureTestingModule({
    imports: [RepoDetailHealthComponent],
    providers: [
      {
        provide: ApiService,
        useValue: {
          findNodeByFile: vi.fn()
        }
      },
      {
        provide: Router,
        useValue: {
          navigate: vi.fn()
        }
      }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(RepoDetailHealthComponent);
  fixture.componentInstance.projectName = 'TC.CodeGraphApi';
  fixture.componentInstance.health = health;
  fixture.detectChanges();
  return fixture;
}

describe('RepoDetailHealthComponent', () => {
  it('keeps summary stats in the header and renders details inline', async () => {
    const fixture = await createComponent(createHealth());
    const text = fixture.nativeElement.textContent as string;

    expect(text).toContain('Codebase Health');
    expect(text).toContain('files');
    expect(text).toContain('hotspots');
    expect(text).toContain('alerts');
    expect(text).not.toContain('Show details');
    expect(text).toContain('Project vitality');
  });

  it('renders repository vitality metrics and monthly activity bars', async () => {
    const fixture = await createComponent(createHealth());
    const text = fixture.nativeElement.textContent as string;

    expect(text).toContain('Project vitality');
    expect(text).toContain('Activity');
    expect(text).toContain('Sustained');
    expect(text).toContain('Firefighting');
    expect(text).toContain('Elevated');
    expect(text).toContain('Monthly activity');
    expect(fixture.nativeElement.querySelectorAll('.rdh-activity-col').length).toBe(3);
  });

  it('shows explicit immature-history messaging when trends are low confidence', async () => {
    const fixture = await createComponent(createHealth({
      repositoryVitality: {
        ...createHealth().repositoryVitality!,
        historyMaturity: 'Young',
        hasSufficientHistoryForTrends: false,
        velocityChangePercent: -12.5
      }
    }));

    const text = fixture.nativeElement.textContent as string;

    expect(text).toContain('Trend confidence is still immature for this repository');
    expect(text).toContain('History maturity');
    expect(text).toContain('Young');
  });

  it('shows an empty state when no monthly activity points are available', async () => {
    const fixture = await createComponent(createHealth({
      repositoryVitality: {
        ...createHealth().repositoryVitality!,
        monthlyCommits: []
      }
    }));

    const text = fixture.nativeElement.textContent as string;

    expect(text).toContain('No monthly activity captured yet.');
    expect(fixture.nativeElement.querySelectorAll('.rdh-activity-col').length).toBe(0);
  });

  it('shows project-level AI analysis inline', async () => {
    const fixture = await createComponent(createHealth({
      projectHealths: [
        {
          project: 'TC.CodeGraphApi',
          dotnetProject: 'TC.CodeGraphApi.Service',
          overallHealth: 7.4,
          totalFiles: 12,
          hotspotCount: 2,
          alertCount: 0,
          computedAt: '2026-04-12T00:00:00Z',
          baseOverallHealth: 7.4,
          scorePenalty: 0
        }
      ],
      analyses: [
        {
          project: 'TC.CodeGraphApi',
          dotnetProject: 'TC.CodeGraphApi.Service',
          analysis: 'Service project analysis body',
          confidence: 'high',
          modelUsed: 'claude-test',
          createdAt: '2026-04-12T00:00:00Z',
          updatedAt: '2026-04-12T00:00:00Z'
        }
      ]
    }));

    const text = fixture.nativeElement.textContent as string;

    expect(text).toContain('Per-project health');
    expect(text).toContain('TC.CodeGraphApi.Service');
    expect(text).toContain('Service project analysis body');
    expect(fixture.nativeElement.querySelectorAll('.rdh-project-row').length).toBe(1);
  });
});
