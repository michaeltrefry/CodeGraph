import { Injector, runInInjectionContext } from '@angular/core';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { ChatContextService } from '../../core/chat-context.service';
import { GraphNode, ProjectDetailResponse } from '../../core/models';
import { NodeListComponent } from './node-list.component';

function createDetail(name: string): ProjectDetailResponse {
  return {
    project: {
      name,
      isFoundational: false,
      properties: {}
    },
    analyses: [],
    nodeCounts: {
      Class: 120,
      Method: 3460
    },
    dotnetProjects: {},
    inboundEdgeCount: 0,
    outboundEdgeCount: 0,
    inboundProjects: [],
    outboundProjects: []
  };
}

function createNode(overrides: Partial<GraphNode>): GraphNode {
  return {
    id: 1,
    project: 'AgentHarness',
    label: 'Class',
    name: 'AgentHarness',
    qualifiedName: 'AgentHarness',
    filePath: 'AgentHarness.cs',
    startLine: 1,
    endLine: 10,
    properties: {},
    doNotTrust: false,
    ...overrides
  };
}

function createComponent(apiOverrides: Partial<ApiService>) {
  const api = {
    getProjectNodes: vi.fn().mockReturnValue(of({ items: [], total: 0, page: 1, pageSize: 10000 })),
    getProject: vi.fn().mockReturnValue(of(createDetail('AgentHarness'))),
    ...apiOverrides
  };

  const injector = Injector.create({
    providers: [
      { provide: ApiService, useValue: api },
      { provide: ChatContextService, useValue: { setNodeList: vi.fn() } },
      {
        provide: ActivatedRoute,
        useValue: {
          snapshot: {
            paramMap: convertToParamMap({ name: 'AgentHarness' }),
            queryParamMap: convertToParamMap({})
          }
        }
      }
    ]
  });

  const component = runInInjectionContext(injector, () => new NodeListComponent());
  return { component, api };
}

describe('NodeListComponent', () => {
  it('does not report no nodes when the project detail says indexed nodes exist', () => {
    const { component } = createComponent({
      getProjectNodes: vi.fn().mockReturnValue(of({ items: [], total: 3580, page: 1, pageSize: 10000 }))
    } as Partial<ApiService>);

    component.ngOnInit();

    expect(component.displayTotal()).toBe(3580);
    expect(component.emptyStateMessage()).toBe('3,580 nodes are indexed, but this response did not include any nodes.');
  });

  it('shows a load failure instead of a false empty state when the nodes request fails', () => {
    const { component } = createComponent({
      getProjectNodes: vi.fn().mockReturnValue(throwError(() => new Error('request failed')))
    } as Partial<ApiService>);

    component.ngOnInit();

    expect(component.displayTotal()).toBe(3580);
    expect(component.emptyStateMessage()).toBe('3,580 nodes are indexed, but the node list could not be loaded.');
  });

  it('counts nested members in section totals', () => {
    const classNode = createNode({
      id: 1,
      label: 'Class',
      name: 'HarnessRunner',
      qualifiedName: 'AgentHarness.HarnessRunner'
    });
    const methodNode = createNode({
      id: 2,
      label: 'Method',
      name: 'Run',
      qualifiedName: 'AgentHarness.HarnessRunner.Run'
    });
    const { component } = createComponent({
      getProjectNodes: vi.fn().mockReturnValue(of({ items: [classNode, methodNode], total: 2, page: 1, pageSize: 10000 }))
    } as Partial<ApiService>);

    component.ngOnInit();

    expect(component.sections()[0].label).toBe('Class');
    expect(component.sections()[0].containers).toHaveLength(1);
    expect(component.sections()[0].totalCount).toBe(2);
  });
});
