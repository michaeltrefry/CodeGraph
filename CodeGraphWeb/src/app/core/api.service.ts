import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthService } from './auth.service';
import {
  ProjectListResponse,
  ProjectDetailResponse,
  SchemaCatalogResponse,
  SchemaListResponse,
  ProjectHealthResponse,
  AnalysisBatchStatus,
  NodeListResponse,
  NodeDetailResponse,
  GraphOverviewResponse,
  MemoryClaimBundle,
  MemoryDiagnostics,
  MemoryEntityBundle,
  MemoryGraphResponse,
  MemorySearchResult,
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
  ImpactReport,
  DatabaseHealthResponse,
  AdminUserResponse,
  AgentPromptGroupResponse,
  AgentPromptResponse,
  DatabaseSourceResponse,
  IndexerAcceptedResponse,
  IndexerRunResponse,
  McpPersonalAccessTokenMetadata,
  McpPersonalAccessTokenCreateResponse,
  AdminReportFiltersResponse,
  AdminReportResponse,
  AssistantDebugExchangeListResponse,
  LlmAnalysisResponse,
  LlmAnalysisWriteRequest,
  LlmAssistantResponse,
  LlmAssistantWriteRequest,
  LlmProviderModelResponse,
  LlmProviderResponse,
  LlmProviderWriteRequest,
  LlmReviewResponse,
  LlmReviewWriteRequest,
  ProjectDiagnosticsResponse,
  ProjectReviewResponse,
  ProjectReviewStreamEvent,
  StartProjectReviewResponse,
  RepositoryReviewResponse,
  RepositoryReviewStreamEvent,
  StartRepositoryReviewResponse
} from './models';

const API = environment.apiUrl;

@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);
  private auth = inject(AuthService);

  // Projects
  listProjects(search?: string, group?: string, page = 1, pageSize = 25): Observable<ProjectListResponse> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (search) params = params.set('search', search);
    if (group) params = params.set('group', group);
    return this.http.get<ProjectListResponse>(`${API}/projects`, { params });
  }

  listSchemas(search?: string, server?: string, database?: string, page = 1, pageSize = 25): Observable<SchemaListResponse> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (search) params = params.set('search', search);
    if (server) params = params.set('server', server);
    if (database) params = params.set('database', database);
    return this.http.get<SchemaListResponse>(`${API}/schemas`, { params });
  }

  getSchemaCatalog(name: string): Observable<SchemaCatalogResponse> {
    return this.http.get<SchemaCatalogResponse>(`${API}/schemas/${encodeURIComponent(name)}/catalog`);
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

  startRepositoryReview(repo: string, mode = 'full'): Observable<StartRepositoryReviewResponse> {
    return this.http.post<StartRepositoryReviewResponse>(
      `${API}/projects/${encodeURIComponent(repo)}/code-review`,
      { mode });
  }

  getLatestRepositoryReview(repo: string): Observable<RepositoryReviewResponse> {
    return this.http.get<RepositoryReviewResponse>(
      `${API}/projects/${encodeURIComponent(repo)}/code-review/latest`);
  }

  getRepositoryReview(repo: string, reviewRunId: number): Observable<RepositoryReviewResponse> {
    return this.http.get<RepositoryReviewResponse>(
      `${API}/projects/${encodeURIComponent(repo)}/code-review/${reviewRunId}`);
  }

  startProjectReview(repo: string, projectName: string, mode = 'standard'): Observable<StartProjectReviewResponse> {
    return this.http.post<StartProjectReviewResponse>(
      `${API}/projects/${encodeURIComponent(repo)}/reviews`,
      { projectName, mode });
  }

  getLatestProjectReview(repo: string, projectName: string): Observable<ProjectReviewResponse> {
    const params = new HttpParams().set('projectName', projectName);
    return this.http.get<ProjectReviewResponse>(
      `${API}/projects/${encodeURIComponent(repo)}/reviews/latest`,
      { params });
  }

  getProjectDiagnostics(repo: string, dotnetProject?: string): Observable<ProjectDiagnosticsResponse> {
    let params = new HttpParams();
    if (dotnetProject) params = params.set('dotnetProject', dotnetProject);
    return this.http.get<ProjectDiagnosticsResponse>(
      `${API}/projects/${encodeURIComponent(repo)}/diagnostics`,
      { params });
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

  // Memory
  getMemoryGraph(limit = 200, skip = 0): Observable<MemoryGraphResponse> {
    const params = new HttpParams().set('limit', limit).set('skip', skip);
    return this.http.get<MemoryGraphResponse>(`${API}/memory/graph`, { params });
  }

  getMemoryEntityGraph(id: string, neighborLimit = 200): Observable<MemoryGraphResponse> {
    const params = new HttpParams().set('neighborLimit', neighborLimit);
    return this.http.get<MemoryGraphResponse>(`${API}/memory/entities/${encodeURIComponent(id)}/graph`, { params });
  }

  getMemoryDiagnostics(staleAfterMinutes = 15, sampleLimit = 10): Observable<MemoryDiagnostics> {
    const params = new HttpParams()
      .set('staleAfterMinutes', staleAfterMinutes)
      .set('sampleLimit', sampleLimit);
    return this.http.get<MemoryDiagnostics>(`${API}/memory/diagnostics`, { params });
  }

  searchMemory(query: string, entityLimit = 8, claimLimit = 8): Observable<MemorySearchResult> {
    const params = new HttpParams()
      .set('query', query)
      .set('entityLimit', entityLimit)
      .set('claimLimit', claimLimit);
    return this.http.get<MemorySearchResult>(`${API}/memory/search`, { params });
  }

  getMemoryEntityBundle(
    id: string,
    options?: { includeSuperseded?: boolean; includeConflicts?: boolean; neighborLimit?: number }
  ): Observable<MemoryEntityBundle> {
    let params = new HttpParams();
    if (options?.includeSuperseded != null) params = params.set('includeSuperseded', options.includeSuperseded);
    if (options?.includeConflicts != null) params = params.set('includeConflicts', options.includeConflicts);
    if (options?.neighborLimit != null) params = params.set('neighborLimit', options.neighborLimit);
    return this.http.get<MemoryEntityBundle>(`${API}/memory/entities/${encodeURIComponent(id)}/bundle`, { params });
  }

  getMemoryClaimBundle(
    id: string,
    options?: { includeSupersessionChain?: boolean; includeConflicts?: boolean; includeEvidence?: boolean }
  ): Observable<MemoryClaimBundle> {
    let params = new HttpParams();
    if (options?.includeSupersessionChain != null) params = params.set('includeSupersessionChain', options.includeSupersessionChain);
    if (options?.includeConflicts != null) params = params.set('includeConflicts', options.includeConflicts);
    if (options?.includeEvidence != null) params = params.set('includeEvidence', options.includeEvidence);
    return this.http.get<MemoryClaimBundle>(`${API}/memory/claims/${encodeURIComponent(id)}`, { params });
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

  getDatabaseHealth(): Observable<DatabaseHealthResponse> {
    return this.http.get<DatabaseHealthResponse>(`${API}/settings/db-health`);
  }

  // Admin users
  listAdmins(): Observable<AdminUserResponse[]> {
    return this.http.get<AdminUserResponse[]>(`${API}/admin/admins`);
  }

  addAdmin(username: string): Observable<AdminUserResponse> {
    return this.http.post<AdminUserResponse>(`${API}/admin/admins`, { username });
  }

  removeAdmin(username: string): Observable<void> {
    return this.http.delete<void>(`${API}/admin/admins/${encodeURIComponent(username)}`);
  }

  // Admin prompt overrides
  getAdminPrompts(): Observable<AgentPromptGroupResponse[]> {
    return this.http.get<AgentPromptGroupResponse[]>(`${API}/admin/prompts`);
  }

  updateAdminPrompt(key: string, promptText: string): Observable<AgentPromptResponse> {
    return this.http.put<AgentPromptResponse>(
      `${API}/admin/prompts/${encodeURIComponent(key)}`,
      { promptText });
  }

  resetAdminPrompt(key: string): Observable<void> {
    return this.http.delete<void>(`${API}/admin/prompts/${encodeURIComponent(key)}`);
  }

  // Database sources
  listDatabaseSources(): Observable<DatabaseSourceResponse[]> {
    return this.http.get<DatabaseSourceResponse[]>(`${API}/database-sources`);
  }

  createDatabaseSource(request: {
    serverName: string;
    databaseName?: string | null;
    connectionString: string;
    enabled?: boolean;
  }): Observable<DatabaseSourceResponse> {
    return this.http.post<DatabaseSourceResponse>(`${API}/database-sources`, request);
  }

  updateDatabaseSource(id: number, request: {
    serverName?: string;
    databaseName?: string | null;
    connectionString?: string;
    enabled?: boolean;
  }): Observable<DatabaseSourceResponse> {
    return this.http.put<DatabaseSourceResponse>(`${API}/database-sources/${id}`, request);
  }

  deleteDatabaseSource(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/database-sources/${id}`);
  }

  generateDatabaseSourceKey(): Observable<{ key: string }> {
    return this.http.post<{ key: string }>(`${API}/database-sources/generate-key`, {});
  }

  syncDatabaseSource(id: number): Observable<IndexerAcceptedResponse> {
    return this.http.post<IndexerAcceptedResponse>(`${API}/database-sources/${id}/sync`, {});
  }

  syncAllDatabaseSources(): Observable<IndexerAcceptedResponse> {
    return this.http.post<IndexerAcceptedResponse>(`${API}/database-sources/sync-all`, {});
  }

  listIndexerRuns(params?: { status?: string; operation?: string; take?: number }): Observable<IndexerRunResponse[]> {
    let query = new HttpParams();
    if (params?.status) query = query.set('status', params.status);
    if (params?.operation) query = query.set('operation', params.operation);
    if (params?.take) query = query.set('take', params.take);
    return this.http.get<IndexerRunResponse[]>(`${API}/indexer/runs`, { params: query });
  }

  getIndexerRun(id: number): Observable<IndexerRunResponse> {
    return this.http.get<IndexerRunResponse>(`${API}/indexer/runs/${id}`);
  }

  // User MCP personal access tokens
  listMcpTokens(): Observable<McpPersonalAccessTokenMetadata[]> {
    return this.http.get<McpPersonalAccessTokenMetadata[]>(`${API}/user/mcp-tokens`);
  }

  createMcpToken(name: string, expiresInDays: number): Observable<McpPersonalAccessTokenCreateResponse> {
    return this.http.post<McpPersonalAccessTokenCreateResponse>(
      `${API}/user/mcp-tokens`,
      { name, expiresInDays });
  }

  revokeMcpToken(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/user/mcp-tokens/${id}`);
  }

  // Admin reports
  getAdminReport(
    report: 'assistant/usage' | 'assistant/activity' | 'mcp/usage' | 'code-review/usage' | 'repository-analysis/usage',
    filters?: { start?: string; end?: string; interval?: string; user?: string; provider?: string; model?: string; tool?: string }
  ): Observable<AdminReportResponse> {
    return this.http.get<AdminReportResponse>(`${API}/admin/reports/${report}`, {
      params: this.buildReportParams(filters)
    });
  }

  getAdminReportFilters(
    filters?: { start?: string; end?: string; interval?: string; user?: string; provider?: string; model?: string; tool?: string }
  ): Observable<AdminReportFiltersResponse> {
    return this.http.get<AdminReportFiltersResponse>(`${API}/admin/reports/filters`, {
      params: this.buildReportParams(filters)
    });
  }

  // LLM configuration
  listLlmProviders(): Observable<LlmProviderResponse[]> {
    return this.http.get<LlmProviderResponse[]>(`${API}/admin/llm-providers`);
  }

  updateLlmProvider(provider: string, request: LlmProviderWriteRequest): Observable<LlmProviderResponse> {
    return this.http.put<LlmProviderResponse>(
      `${API}/admin/llm-providers/${encodeURIComponent(provider)}`,
      request);
  }

  listLlmProviderModels(): Observable<LlmProviderModelResponse[]> {
    return this.http.get<LlmProviderModelResponse[]>(`${API}/llm-providers/models`);
  }

  getLlmAnalysis(): Observable<LlmAnalysisResponse> {
    return this.http.get<LlmAnalysisResponse>(`${API}/admin/llm-analysis`);
  }

  updateLlmAnalysis(request: LlmAnalysisWriteRequest): Observable<LlmAnalysisResponse> {
    return this.http.put<LlmAnalysisResponse>(`${API}/admin/llm-analysis`, request);
  }

  getLlmReview(): Observable<LlmReviewResponse> {
    return this.http.get<LlmReviewResponse>(`${API}/admin/llm-review`);
  }

  updateLlmReview(request: LlmReviewWriteRequest): Observable<LlmReviewResponse> {
    return this.http.put<LlmReviewResponse>(`${API}/admin/llm-review`, request);
  }

  getLlmAssistant(): Observable<LlmAssistantResponse> {
    return this.http.get<LlmAssistantResponse>(`${API}/admin/llm-assistant`);
  }

  updateLlmAssistant(request: LlmAssistantWriteRequest): Observable<LlmAssistantResponse> {
    return this.http.put<LlmAssistantResponse>(`${API}/admin/llm-assistant`, request);
  }

  getAssistantDebugExchanges(runId: number): Observable<AssistantDebugExchangeListResponse> {
    return this.http.get<AssistantDebugExchangeListResponse>(`${API}/ask/runs/${runId}/debug-exchanges`);
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
      headers: this.fetchHeaders({ 'Content-Type': 'application/json' }),
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

  async *streamProjectReview(repo: string, reviewRunId: number, signal?: AbortSignal): AsyncGenerator<ProjectReviewStreamEvent> {
    const response = await fetch(
      `${API}/projects/${encodeURIComponent(repo)}/reviews/${reviewRunId}/stream`,
      {
        method: 'GET',
        headers: this.fetchHeaders({ Accept: 'text/event-stream' }),
        signal
      });

    if (!response.ok || !response.body) {
      throw new Error(`HTTP ${response.status}`);
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';

    while (true) {
      const { value, done } = await reader.read();
      if (done) {
        buffer += decoder.decode();
      } else {
        buffer += decoder.decode(value, { stream: true });
      }

      const lines = buffer.split('\n\n');
      buffer = done ? '' : (lines.pop() ?? '');

      for (const chunk of lines) {
        const line = chunk.trim();
        if (!line.startsWith('data:')) continue;
        yield JSON.parse(line.slice(5).trim()) as ProjectReviewStreamEvent;
      }

      if (done) break;
    }
  }

  async *streamRepositoryReview(repo: string, reviewRunId: number, signal?: AbortSignal): AsyncGenerator<RepositoryReviewStreamEvent> {
    const response = await fetch(
      `${API}/projects/${encodeURIComponent(repo)}/code-review/${reviewRunId}/stream`,
      {
        method: 'GET',
        headers: this.fetchHeaders({ Accept: 'text/event-stream' }),
        signal
      });

    if (!response.ok || !response.body) {
      throw new Error(`HTTP ${response.status}`);
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';

    while (true) {
      const { value, done } = await reader.read();
      if (done) {
        buffer += decoder.decode();
      } else {
        buffer += decoder.decode(value, { stream: true });
      }

      const lines = buffer.split('\n\n');
      buffer = done ? '' : (lines.pop() ?? '');

      for (const chunk of lines) {
        const line = chunk.trim();
        if (!line.startsWith('data:')) continue;
        yield JSON.parse(line.slice(5).trim()) as RepositoryReviewStreamEvent;
      }

      if (done) break;
    }
  }

  private buildReportParams(filters?: {
    start?: string;
    end?: string;
    interval?: string;
    user?: string;
    provider?: string;
    model?: string;
    tool?: string;
  }): HttpParams {
    let params = new HttpParams();
    if (!filters) return params;
    if (filters.start) params = params.set('start', filters.start);
    if (filters.end) params = params.set('end', filters.end);
    if (filters.interval) params = params.set('interval', filters.interval);
    if (filters.user) params = params.set('user', filters.user);
    if (filters.provider) params = params.set('provider', filters.provider);
    if (filters.model) params = params.set('model', filters.model);
    if (filters.tool) params = params.set('tool', filters.tool);
    return params;
  }

  private fetchHeaders(headers: Record<string, string>): Record<string, string> {
    const token = this.auth.getAccessToken();
    return token
      ? { ...headers, Authorization: `Bearer ${token}` }
      : headers;
  }
}
