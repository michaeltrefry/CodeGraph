import { Injector, runInInjectionContext } from '@angular/core';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import {
  MemoryClaimBundle,
  MemoryDiagnostics,
  MemoryEntityBundle,
  MemoryGraphResponse,
  MemorySearchResult
} from '../../core/models';
import { MemoryPageComponent } from './memory-page.component';

const overviewGraph: MemoryGraphResponse = {
  nodes: [
    {
      id: 'project_codegraph',
      label: 'CodeGraph',
      type: 'project',
      summary: 'Code analysis platform',
      createdAt: '2026-04-01T00:00:00Z',
      updatedAt: '2026-04-27T00:00:00Z'
    },
    {
      id: 'memory_system',
      label: 'Memory system',
      type: 'concept',
      summary: 'Claim-centric memory',
      createdAt: '2026-04-01T00:00:00Z',
      updatedAt: '2026-04-26T00:00:00Z'
    }
  ],
  links: [
    {
      source: 'project_codegraph',
      target: 'memory_system',
      relationship: 'has_subsystem',
      timestamp: '2026-04-27T00:00:00Z'
    }
  ],
  totalNodeCount: 2
};

const entityBundle: MemoryEntityBundle = {
  entity: {
    id: 'project_codegraph',
    label: 'CodeGraph',
    type: 'project',
    aliases: [],
    summary: 'Code analysis platform',
    source: 'test',
    createdAt: '2026-04-01T00:00:00Z',
    updatedAt: '2026-04-27T00:00:00Z'
  },
  activeClaims: [
    {
      id: 'claim_1',
      claimKey: 'claim_1',
      factGroupKey: 'fg_1',
      subjectEntityId: 'project_codegraph',
      predicate: 'has_subsystem',
      objectEntityId: 'memory_system',
      normalizedText: 'CodeGraph has subsystem Memory system',
      status: 'Active',
      recordedAt: '2026-04-27T00:00:00Z',
      source: 'test'
    }
  ],
  conflictingClaims: [],
  supersededClaims: [],
  neighborEdges: [
    {
      fromEntityId: 'project_codegraph',
      toEntityId: 'memory_system',
      edgeType: 'has_subsystem',
      createdAt: '2026-04-27T00:00:00Z',
      updatedAt: '2026-04-27T00:00:00Z'
    }
  ],
  observations: []
};

const claimBundle: MemoryClaimBundle = {
  claim: entityBundle.activeClaims[0],
  factGroupPeers: [],
  supersessionChain: [],
  conflicts: [],
  evidence: [],
  observations: []
};

const diagnostics: MemoryDiagnostics = {
  username: 'michael',
  generatedAtUtc: '2026-04-27T00:00:00Z',
  embeddingAvailable: true,
  retrievalDegraded: false,
  writeDegraded: false,
  entityCount: 2,
  claimCount: 1,
  activeClaimCount: 1,
  conflictedClaimCount: 0,
  supersededClaimCount: 0,
  deprecatedClaimCount: 0,
  seedAliasCount: 2,
  observationCount: 0,
  evidenceCount: 0,
  orphanObservationCount: 0,
  orphanEvidenceCount: 0,
  healthSignals: [],
  writeDiagnostics: {
    username: 'michael',
    generatedAtUtc: '2026-04-27T00:00:00Z',
    queuedCount: 0,
    staleQueuedCount: 0,
    processingCount: 0,
    retryingCount: 0,
    completedCount: 1,
    failedCount: 0,
    submissionFailureCount: 0,
    processingFailureCount: 0,
    staleProcessingCount: 0,
    staleAfterMinutes: 15,
    staleQueuedReceipts: [],
    staleProcessingReceipts: [],
    retryingReceipts: [],
    recentFailedReceipts: []
  }
};

const searchResult: MemorySearchResult = {
  query: 'codegraph',
  entities: [
    {
      entityId: 'project_codegraph',
      label: 'CodeGraph',
      type: 'project',
      score: 99,
      matchKind: 'exact',
      diagnostics: {
        retrievalStage: 'entity_search',
        scoreBreakdown: { exact: 99 },
        matchedFields: ['label'],
        matchedEntityIds: ['project_codegraph'],
        matchedClaimIds: []
      }
    }
  ],
  claims: [
    {
      claimId: 'claim_1',
      normalizedText: 'CodeGraph has subsystem Memory system',
      predicate: 'has_subsystem',
      status: 'Active',
      score: 77,
      matchKind: 'lexical',
      diagnostics: {
        retrievalStage: 'claim_text_search',
        scoreBreakdown: { lexical: 77 },
        matchedFields: ['normalizedText'],
        matchedEntityIds: ['project_codegraph'],
        matchedClaimIds: ['claim_1']
      }
    }
  ]
};

describe('MemoryPageComponent', () => {
  let api: {
    getMemoryDiagnostics: ReturnType<typeof vi.fn>;
    getMemoryGraph: ReturnType<typeof vi.fn>;
    getMemoryEntityGraph: ReturnType<typeof vi.fn>;
    getMemoryEntityBundle: ReturnType<typeof vi.fn>;
    getMemoryClaimBundle: ReturnType<typeof vi.fn>;
    searchMemory: ReturnType<typeof vi.fn>;
  };
  let component: MemoryPageComponent;

  beforeEach(() => {
    api = {
      getMemoryDiagnostics: vi.fn().mockReturnValue(of(diagnostics)),
      getMemoryGraph: vi.fn().mockReturnValue(of(overviewGraph)),
      getMemoryEntityGraph: vi.fn().mockReturnValue(of(overviewGraph)),
      getMemoryEntityBundle: vi.fn().mockReturnValue(of(entityBundle)),
      getMemoryClaimBundle: vi.fn().mockReturnValue(of(claimBundle)),
      searchMemory: vi.fn().mockReturnValue(of(searchResult))
    };

    const injector = Injector.create({
      providers: [{ provide: ApiService, useValue: api }]
    });

    component = runInInjectionContext(injector, () => new MemoryPageComponent());
  });

  it('loads diagnostics and overview graph on init', () => {
    component.ngOnInit();

    expect(api.getMemoryDiagnostics).toHaveBeenCalledWith(15, 8);
    expect(api.getMemoryGraph).toHaveBeenCalledWith(250, 0);
    expect(component.diagnostics()?.entityCount).toBe(2);
    expect(component.nodes().map(node => node.id)).toEqual(['project_codegraph', 'memory_system']);
  });

  it('searches and focuses an entity result', () => {
    component.ngOnInit();
    component.updateSearchText('codegraph');
    component.runSearch();
    component.focusSearchEntity(component.searchEntityResults()[0]);

    expect(api.searchMemory).toHaveBeenCalledWith('codegraph', 8, 8);
    expect(api.getMemoryEntityGraph).toHaveBeenCalledWith('project_codegraph', 500);
    expect(component.focusMode()).toBe(true);
    expect(component.selectedBundle()?.entity.id).toBe('project_codegraph');
  });

  it('loads claim detail from claim search results', () => {
    component.ngOnInit();
    component.updateSearchText('claim');
    component.runSearch();
    component.inspectSearchClaim(component.searchClaimResults()[0]);

    expect(api.getMemoryClaimBundle).toHaveBeenCalledWith('claim_1', {
      includeSupersessionChain: true,
      includeConflicts: true,
      includeEvidence: true
    });
    expect(component.selectedClaimBundle()?.claim.id).toBe('claim_1');
  });
});
