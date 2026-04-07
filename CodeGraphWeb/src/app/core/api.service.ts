import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  ProjectListResponse,
  ProjectDetailResponse,
  ProjectHealthResponse,
  AnalysisBatchStatus,
  NodeListResponse,
  NodeDetailResponse,
  GraphOverviewResponse,
  AssistantEvent,
  WikiSection,
  WikiTreeNode,
  WikiPage,
  WikiPageListItem,
  WikiPageRequest,
  WikiRevisionListItem,
  WikiRevision,
  WikiAttachment,
  NodeSourceResponse,
  UnifiedSearchResponse,
  ProjectSecurityResponse,
  ClusterOverviewResponse,
  ClusterDetailResponse,
  ClusterGraphResponse,
  ImpactReport
} from './models';

const API = environment.apiUrl;

@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);

  // Projects
  listProjects(search?: string, group?: string, page = 1, pageSize = 25): Observable<ProjectListResponse> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (search) params = params.set('search', search);
    if (group) params = params.set('group', group);
    return this.http.get<ProjectListResponse>(`${API}/projects`, { params });
  }

  getProject(name: string): Observable<ProjectDetailResponse> {
    return this.http.get<ProjectDetailResponse>(`${API}/projects/${encodeURIComponent(name)}`);
  }

  getProjectHealth(name: string): Observable<ProjectHealthResponse> {
    return this.http.get<ProjectHealthResponse>(
      `${API}/projects/${encodeURIComponent(name)}/health`);
  }

  getProjectSecurity(name: string): Observable<ProjectSecurityResponse> {
    return this.http.get<ProjectSecurityResponse>(
      `${API}/projects/${encodeURIComponent(name)}/security`);
  }

  getBatchStatus(name: string): Observable<AnalysisBatchStatus> {
    return this.http.get<AnalysisBatchStatus>(
      `${API}/projects/${encodeURIComponent(name)}/batch-status`);
  }

  reAnalyze(repo: string): Observable<AnalysisBatchStatus> {
    return this.http.post<AnalysisBatchStatus>(`${API}/projects/ReAnalyze`, { repo });
  }

  getProjectReadme(name: string): Observable<{ content: string }> {
    return this.http.get<{ content: string }>(
      `${API}/projects/${encodeURIComponent(name)}/readme`);
  }

  deleteProject(name: string): Observable<void> {
    return this.http.delete<void>(`${API}/projects/${encodeURIComponent(name)}`);
  }

  // Impact analysis (blast radius)
  getImpact(project: string, node: string, depth = 3): Observable<ImpactReport> {
    const params = new HttpParams().set('node', node).set('depth', depth);
    return this.http.get<ImpactReport>(
      `${API}/projects/${encodeURIComponent(project)}/impact`, { params });
  }

  getFileImpact(project: string, path: string, depth = 3): Observable<ImpactReport> {
    const params = new HttpParams().set('path', path).set('depth', depth);
    return this.http.get<ImpactReport>(
      `${API}/projects/${encodeURIComponent(project)}/impact/file`, { params });
  }

  getProjectNodes(name: string, label?: string, dotnetProject?: string, page = 1, pageSize = 50): Observable<NodeListResponse> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (label) params = params.set('label', label);
    if (dotnetProject) params = params.set('dotnetProject', dotnetProject);
    return this.http.get<NodeListResponse>(
      `${API}/projects/${encodeURIComponent(name)}/nodes`, { params });
  }

  // Nodes
  getNode(id: number): Observable<NodeDetailResponse> {
    return this.http.get<NodeDetailResponse>(`${API}/nodes/${id}`);
  }

  setDoNotTrust(id: number, doNotTrust: boolean): Observable<void> {
    return this.http.put<void>(`${API}/nodes/${id}/do-not-trust`, { doNotTrust });
  }

  getNodeSource(id: number): Observable<NodeSourceResponse> {
    return this.http.get<NodeSourceResponse>(`${API}/nodes/${id}/source`);
  }

  findNodeByFile(project: string, filePath: string, line?: number): Observable<{ nodeId: number }> {
    let params = new HttpParams().set('project', project).set('filePath', filePath);
    if (line) params = params.set('line', line);
    return this.http.get<{ nodeId: number }>(`${API}/nodes/by-file`, { params });
  }

  searchNodes(q: string, project?: string, label?: string, page = 1, pageSize = 25): Observable<NodeListResponse> {
    let params = new HttpParams().set('q', q).set('page', page).set('pageSize', pageSize);
    if (project) params = params.set('project', project);
    if (label) params = params.set('label', label);
    return this.http.get<NodeListResponse>(`${API}/nodes/search`, { params });
  }

  // Unified search
  search(q: string, page = 1, pageSize = 25): Observable<UnifiedSearchResponse> {
    const params = new HttpParams().set('q', q).set('page', page).set('pageSize', pageSize);
    return this.http.get<UnifiedSearchResponse>(`${API}/search`, { params });
  }

  // Graph overview
  getGraphOverview(): Observable<GraphOverviewResponse> {
    return this.http.get<GraphOverviewResponse>(`${API}/graph/overview`);
  }

  // Clusters (community detection)
  getClusters(): Observable<ClusterOverviewResponse> {
    return this.http.get<ClusterOverviewResponse>(`${API}/clusters`);
  }

  getClusterGraph(): Observable<ClusterGraphResponse> {
    return this.http.get<ClusterGraphResponse>(`${API}/clusters/graph`);
  }

  getClusterDetail(id: number): Observable<ClusterDetailResponse> {
    return this.http.get<ClusterDetailResponse>(`${API}/clusters/${id}`);
  }

  // Wiki
  listSections(): Observable<WikiSection[]> {
    return this.http.get<WikiSection[]>(`${API}/wiki/sections`);
  }

  getSectionTree(section: string): Observable<WikiTreeNode[]> {
    return this.http.get<WikiTreeNode[]>(`${API}/wiki/${encodeURIComponent(section)}/tree`);
  }

  getWikiPage(section: string, path: string): Observable<WikiPage> {
    return this.http.get<WikiPage>(`${API}/wiki/${encodeURIComponent(section)}/${path}`);
  }

  createWikiPage(section: string, request: WikiPageRequest): Observable<WikiPageListItem> {
    return this.http.post<WikiPageListItem>(`${API}/wiki/${encodeURIComponent(section)}`, request);
  }

  createChildPage(section: string, parentPath: string, request: WikiPageRequest): Observable<WikiPageListItem> {
    return this.http.post<WikiPageListItem>(`${API}/wiki/${encodeURIComponent(section)}/${parentPath}`, request);
  }

  updateWikiPage(section: string, path: string, request: WikiPageRequest): Observable<WikiPageListItem> {
    return this.http.put<WikiPageListItem>(`${API}/wiki/${encodeURIComponent(section)}/${path}`, request);
  }

  deleteWikiPage(section: string, path: string): Observable<void> {
    return this.http.delete<void>(`${API}/wiki/${encodeURIComponent(section)}/${path}`);
  }

  getWikiRevisions(section: string, path: string): Observable<WikiRevisionListItem[]> {
    return this.http.get<WikiRevisionListItem[]>(`${API}/wiki/${encodeURIComponent(section)}/${path}/revisions`);
  }

  getWikiRevision(section: string, path: string, revision: number): Observable<WikiRevision> {
    return this.http.get<WikiRevision>(`${API}/wiki/${encodeURIComponent(section)}/${path}/revisions/${revision}`);
  }

  getWikiAttachments(section: string, path: string): Observable<WikiAttachment[]> {
    return this.http.get<WikiAttachment[]>(`${API}/wiki/${encodeURIComponent(section)}/${path}/attachments`);
  }

  uploadWikiAttachment(section: string, path: string, file: File): Observable<WikiAttachment> {
    const form = new FormData();
    form.append('file', file);
    return this.http.post<WikiAttachment>(`${API}/wiki/${encodeURIComponent(section)}/${path}/attachments`, form);
  }

  deleteWikiAttachment(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/wiki/attachments/${id}`);
  }

  // Ask — returns an AsyncGenerator over SSE events
  async *ask(question: string, signal?: AbortSignal, context?: string, history?: { role: string; content: string }[]): AsyncGenerator<AssistantEvent> {
    const body: Record<string, unknown> = { question };
    if (context) body['context'] = context;
    if (history?.length) body['history'] = history;

    const response = await fetch(`${API}/ask`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      signal
    });

    if (!response.ok || !response.body) {
      yield { type: 'error', content: `HTTP ${response.status}` };
      return;
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';

    while (true) {
      const { value, done } = await reader.read();
      if (done) {
        // Flush any remaining data in the buffer
        buffer += decoder.decode();
      } else {
        buffer += decoder.decode(value, { stream: true });
      }

      const lines = buffer.split('\n\n');
      buffer = done ? '' : (lines.pop() ?? '');

      for (const chunk of lines) {
        const line = chunk.trim();
        if (!line.startsWith('data:')) continue;
        try {
          const event = JSON.parse(line.slice(5).trim()) as AssistantEvent;
          yield event;
          if (event.type === 'done' || event.type === 'error') return;
        } catch {
          // ignore malformed events
        }
      }

      if (done) break;
    }
  }
}
