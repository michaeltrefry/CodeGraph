// Mirrors the C# DTOs from the API

export interface ProjectInfo {
  name: string;
  repoUrl?: string;
  sourceGroup?: string;
  localPath?: string;
  lastCommitSha?: string;
  indexedAt?: string;
  language?: string;
  framework?: string;
  isFoundational: boolean;
  properties?: Record<string, unknown>;
}

export interface SchemaListItem {
  name: string;
  serverName: string;
  databaseName: string;
  tableCount: number;
  viewCount: number;
  procedureCount: number;
  indexedAt?: string;
  language?: string;
  framework?: string;
  properties?: Record<string, unknown>;
}

export interface SchemaListResponse {
  items: SchemaListItem[];
  total: number;
  totalTables: number;
  totalViews: number;
  totalProcedures: number;
  page: number;
  pageSize: number;
  servers: string[];
  databases: string[];
}

export interface SchemaCatalogResponse {
  projectName: string;
  serverName: string;
  databaseName: string;
  tables: SchemaObject[];
  views: SchemaObject[];
  procedures: SchemaProcedure[];
}

export interface SchemaObject {
  id: number;
  name: string;
  qualifiedName: string;
  label: string;
  comment?: string;
  primaryKeyColumns: string[];
  indexes: SchemaIndex[];
  constraints: SchemaConstraint[];
  foreignKeys: SchemaForeignKey[];
  columns: SchemaColumn[];
}

export interface SchemaProcedure {
  id: number;
  name: string;
  qualifiedName: string;
  routineType: string;
  comment?: string;
  parameters: SchemaParameter[];
}

export interface SchemaColumn {
  id: number;
  name: string;
  qualifiedName: string;
  ordinal: number;
  dataType: string;
  nullable: boolean;
  isPrimaryKey: boolean;
  default?: string;
  key?: string;
  extra?: string;
  comment?: string;
}

export interface SchemaIndex {
  name: string;
  isUnique: boolean;
  indexType?: string;
  columns: string[];
}

export interface SchemaConstraint {
  name: string;
  constraintType: string;
  columns: string[];
  referencedTable?: string;
  referencedColumns?: string[];
  checkClause?: string;
}

export interface SchemaForeignKey {
  name: string;
  columns: string[];
  referencedTable: string;
  referencedColumns: string[];
}

export interface SchemaParameter {
  name: string;
  ordinal: number;
  mode: string;
  dataType: string;
  nullable: boolean;
}

export interface DatabaseIndexIssue {
  name: string;
  type: string;
  state: string;
  entityType: string;
  labelsOrTypes: string[];
  properties: string[];
  failureMessage?: string;
}

export interface DatabaseDuplicateGroup {
  category: string;
  key: string;
  count: number;
  sampleValues: string[];
}

export interface DatabaseHealthResponse {
  status: 'healthy' | 'warning' | 'critical';
  capturedAt: string;
  constraintCount: number;
  expectedConstraintCount: number;
  missingConstraints: string[];
  indexCount: number;
  expectedIndexCount: number;
  missingIndexes: string[];
  offlineIndexes: DatabaseIndexIssue[];
  duplicateGroups: DatabaseDuplicateGroup[];
}

export interface AdminUserResponse {
  username: string;
  createdAt: string;
}

export interface AgentPromptResponse {
  key: string;
  category: string;
  categoryDisplayName: string;
  displayName: string;
  description: string;
  defaultText: string;
  effectiveText: string;
  hasOverride: boolean;
  updatedBy?: string;
  updatedAt?: string;
}

export interface AgentPromptGroupResponse {
  category: string;
  categoryDisplayName: string;
  prompts: AgentPromptResponse[];
}

export interface DatabaseSourceResponse {
  id: number;
  serverName: string;
  databaseName: string;
  connectionString: string;
  enabled: boolean;
  lastSyncedAt?: string;
  createdAt: string;
  updatedAt: string;
}

export interface IndexerAcceptedResponse {
  status: string;
  message?: string;
  runId?: number;
  runStatusUrl?: string;
}

export interface IndexerRunResponse {
  id: number;
  operation: string;
  status: string;
  requestedByUsername?: string;
  target?: string;
  message?: string;
  error?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
}

export interface AuthConfigResponse {
  enabled: boolean;
  authority: string;
  authorizationUrl: string;
  tokenUrl: string;
  endSessionUrl: string;
  clientId: string;
  audience: string;
  scope: string;
}

export interface CurrentUserResponse {
  username: string;
  isAdmin: boolean;
}

export interface McpPersonalAccessTokenMetadata {
  id: number;
  tokenName: string;
  tokenPrefix: string;
  lastFour: string;
  createdAtUtc: string;
  expiresAtUtc: string;
  revokedAtUtc?: string;
  lastUsedAtUtc?: string;
  lastUsedFrom?: string;
  status: string;
}

export interface McpPersonalAccessTokenCreateResponse {
  token: McpPersonalAccessTokenMetadata;
  rawToken: string;
}

export interface AdminReportRangeResponse {
  start: string;
  end: string;
}

export interface AdminReportAppliedFiltersResponse {
  user?: string;
  provider?: string;
  model?: string;
  tool?: string;
}

export interface AdminSummaryCardResponse {
  key: string;
  label: string;
  value: number;
}

export interface AdminSeriesPointResponse {
  bucketStart: string;
  value: number;
}

export interface AdminReportSeriesResponse {
  key: string;
  label: string;
  points: AdminSeriesPointResponse[];
}

export interface AdminBreakdownItemResponse {
  dimension: string;
  key: string;
  label: string;
  value: number;
}

export interface AdminReportResponse {
  range: AdminReportRangeResponse;
  interval: string;
  totals: AdminSummaryCardResponse[];
  series: AdminReportSeriesResponse[];
  breakdowns: AdminBreakdownItemResponse[];
  appliedFilters: AdminReportAppliedFiltersResponse;
}

export interface AdminReportFiltersResponse {
  users: string[];
  providers: string[];
  models: string[];
  tools: string[];
}

export interface AssistantRunResponse {
  id: number;
  chatId: string;
  username: string;
  status: string;
  question: string;
  context?: string;
  providerRequested?: string;
  modelRequested?: string;
  providerUsed?: string;
  modelUsed?: string;
  finalAnswer?: string;
  warnings: string[];
  error?: string;
  lastSequence: number;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
}

export interface AssistantDebugExchangeResponse {
  exchangeIndex: number;
  turnIndex: number;
  provider: string;
  model: string;
  requestBody: unknown;
  responseBody?: unknown;
  requestText: string;
  responseText?: string;
  toolUses?: unknown;
  requestMetadata?: unknown;
  responseMetadata?: unknown;
  requestId?: string;
  responseId?: string;
  inputTokens?: number;
  outputTokens?: number;
  totalTokens?: number;
  createdAt: string;
}

export interface AssistantDebugExchangeListResponse {
  run: AssistantRunResponse;
  exchanges: AssistantDebugExchangeResponse[];
}

export interface DotnetSdkSupportInfo {
  version: string;
  channel: string;
  displayName: string;
  supportStatus: 'supported' | 'out_of_support' | 'unknown';
  supportEndedOn?: string;
  isPinnedByGlobalJson: boolean;
}

export interface DotnetTargetFrameworkSupportInfo {
  moniker: string;
  displayName: string;
  supportStatus: 'supported' | 'out_of_support' | 'mixed' | 'not_applicable' | 'os_lifecycle' | 'unknown';
  supportEndedOn?: string;
}

export interface DotnetSupportInfo {
  overallStatus: 'supported' | 'out_of_support' | 'mixed' | 'unknown';
  summary: string;
  sdk?: DotnetSdkSupportInfo;
  targetFrameworks: DotnetTargetFrameworkSupportInfo[];
}

export interface ProjectSummary {
  project: string;
  summary: string;
  confidence: 'low' | 'medium' | 'high';
  sourceHash: string;
  modelUsed?: string;
  createdAt: string;
  updatedAt: string;
}

export interface StoredEndpoint {
  route: string;
  httpMethod: string;
  description: string;
  requestModel?: string;
  responseModel?: string;
}

export interface StoredService {
  name: string;
  description: string;
  interfaceName?: string;
  lifetime: string;
}

export interface StoredProjectAnalysis {
  repo: string;
  projectName: string;
  summary: string;
  confidence: 'low' | 'medium' | 'high';
  endpoints?: StoredEndpoint[];
  services?: StoredService[];
  externalDependencies?: string[];
  databaseTables?: string[];
  modelUsed?: string;
  updatedAt: string;
}

export interface GraphNode {
  id: number;
  project: string;
  dotnetProject?: string;
  label: string;
  name: string;
  qualifiedName: string;
  description?: string;
  analysisConfidence?: 'low' | 'medium' | 'high';
  filePath: string;
  startLine: number;
  endLine: number;
  properties: Record<string, unknown>;
  doNotTrust: boolean;
}

export interface EdgeSummary {
  edgeId: number;
  type: string;
  neighborId: number;
  neighborName?: string;
  neighborQualifiedName?: string;
  neighborLabel?: string;
  neighborProject?: string;
  isCrossProject: boolean;
  properties: Record<string, unknown>;
}

export interface CrossRepoEdgeSummary {
  edgeId: number;
  type: string;
  direction: 'inbound' | 'outbound';
  sourceProject: string;
  targetProject: string;
  neighborNodeId: number;
  neighborName?: string;
  neighborQualifiedName?: string;
  neighborLabel?: string;
  properties: Record<string, unknown>;
}

export interface ProjectListResponse {
  items: ProjectInfo[];
  total: number;
  page: number;
  pageSize: number;
  groups: string[];
}

export interface ProjectHealthSummary {
  project: string;
  dotnetProject?: string;
  overallHealth: number;
  totalFiles: number;
  hotspotCount: number;
  alertCount: number;
  topHotspots?: string;
  computedAt: string;
  baseOverallHealth: number;
  scorePenalty: number;
  historyMaturity?: 'young' | 'growing' | 'mature' | 'Young' | 'Growing' | 'Mature';
}

export interface MonthlyCommitPoint {
  month: string;
  commitCount: number;
}

export interface RepositoryVitalitySummary {
  historyMaturity?: 'young' | 'growing' | 'mature' | 'Young' | 'Growing' | 'Mature';
  hasSufficientHistoryForTrends: boolean;
  activityStatus?: string;
  firefightingStatus?: string;
  monthlyCommits: MonthlyCommitPoint[];
  velocityLast6Months: number;
  velocityPrior6Months: number;
  velocityChangePercent: number;
  dormantMonths12m: number;
  maxInactiveStreakMonths: number;
  firefightingCommits90d: number;
  firefightingCommits365d: number;
  firefightingRate90d: number;
  firefightingRate365d: number;
}

export interface ProjectHealthAnalysis {
  project: string;
  dotnetProject?: string;
  analysis: string;
  confidence: 'low' | 'medium' | 'high';
  modelUsed?: string;
  createdAt: string;
  updatedAt: string;
}

export interface ProjectSecuritySummary {
  securityScore: number;
  criticalCount: number;
  highCount: number;
  mediumCount: number;
  lowCount: number;
  computedAt: string;
}

export interface SecurityFinding {
  category: 'secret' | 'vulnerable_package' | 'attack_surface';
  severity: 'critical' | 'high' | 'medium' | 'low';
  title: string;
  description: string;
  filePath?: string;
  lineNumber?: number;
  package?: string;
  packageVersion?: string;
  advisory?: string;
}

export interface ProjectSecurityResponse {
  project: string;
  securityScore: number;
  criticalCount: number;
  highCount: number;
  mediumCount: number;
  lowCount: number;
  findings: SecurityFinding[];
  analysis?: string;
  computedAt: string;
}

export interface StartProjectReviewResponse {
  reviewRunId: number;
  status: string;
}

export interface StartRepositoryReviewResponse {
  reviewRunId: number;
  status: string;
}

export interface ProjectReviewRunResponse {
  id: number;
  project: string;
  projectName: string;
  reviewedCommitSha?: string;
  status: string;
  reviewMode: string;
  promptVersion: string;
  modelUsed?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  error?: string;
}

export interface ProjectReviewFindingResponse {
  severity: string;
  category: string;
  title: string;
  explanation: string;
  evidence: string;
  filePath: string;
  lineStart?: number;
  lineEnd?: number;
  suggestedImprovement: string;
  confidence: string;
}

export interface ProjectReviewResponse {
  run: ProjectReviewRunResponse;
  overview: string;
  findings: ProjectReviewFindingResponse[];
  strengths: string[];
  reviewedAreas: string[];
  skippedAreas: string[];
  followUps: string[];
}

export interface RepositoryReviewRunResponse {
  id: number;
  repo: string;
  reviewedCommitSha?: string;
  baselineReviewRunId?: number;
  baselineCommitSha?: string;
  status: string;
  reviewMode: string;
  promptVersion: string;
  modelUsed?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  error?: string;
}

export interface RepositoryReviewFindingResponse {
  severity: string;
  category: string;
  title: string;
  explanation: string;
  evidence: string;
  filePath: string;
  lineStart?: number;
  lineEnd?: number;
  suggestedImprovement: string;
  confidence: string;
  projectName?: string;
}

export interface RepositoryReviewProjectSectionResponse {
  projectName: string;
  overview: string;
  strengths: string[];
  reviewedAreas: string[];
  skippedAreas: string[];
  followUps: string[];
  findings: RepositoryReviewFindingResponse[];
  reusedFromBaseline: boolean;
}

export interface RepositoryReviewResponse {
  run: RepositoryReviewRunResponse;
  overview: string;
  findings: RepositoryReviewFindingResponse[];
  strengths: string[];
  reviewedAreas: string[];
  skippedAreas: string[];
  followUps: string[];
  projectReviews: RepositoryReviewProjectSectionResponse[];
}

export interface ProjectDiagnosticResponse {
  source: string;
  diagnosticId: string;
  severity: string;
  message: string;
  category?: string;
  filePath: string;
  lineStart?: number;
  lineEnd?: number;
  computedAt: string;
}

export interface ProjectDiagnosticsResponse {
  project: string;
  dotnetProject?: string;
  errorCount: number;
  warningCount: number;
  infoCount: number;
  diagnostics: ProjectDiagnosticResponse[];
}

export interface ProjectHealthResponse {
  repoHealth?: ProjectHealthSummary;
  projectHealths: ProjectHealthSummary[];
  topHotspots: FileMetrics[];
  analyses: ProjectHealthAnalysis[];
  securitySummary?: ProjectSecuritySummary;
  dotnetSupport?: DotnetSupportInfo;
  repositoryVitality?: RepositoryVitalitySummary;
}

export interface ProjectReviewStatusEvent {
  reviewRunId: number;
  status: string;
  startedAt?: string;
  completedAt?: string;
  error?: string;
}

export interface ProjectReviewProgressEvent {
  reviewRunId: number;
  status: string;
  message: string;
}

export interface ProjectReviewErrorEvent {
  reviewRunId: number;
  status?: string;
  message: string;
}

export type ProjectReviewStreamEvent =
  | { type: 'status'; content: ProjectReviewStatusEvent }
  | { type: 'progress'; content: ProjectReviewProgressEvent }
  | { type: 'finding'; content: ProjectReviewFindingResponse }
  | { type: 'completed'; content: ProjectReviewResponse }
  | { type: 'error'; content: ProjectReviewErrorEvent };

export interface RepositoryReviewStatusEvent {
  reviewRunId: number;
  status: string;
  startedAt?: string;
  completedAt?: string;
  error?: string;
}

export interface RepositoryReviewProgressEvent {
  reviewRunId: number;
  status: string;
  message: string;
}

export interface RepositoryReviewErrorEvent {
  reviewRunId: number;
  status?: string;
  message: string;
}

export type RepositoryReviewStreamEvent =
  | { type: 'status'; content: RepositoryReviewStatusEvent }
  | { type: 'progress'; content: RepositoryReviewProgressEvent }
  | { type: 'finding'; content: RepositoryReviewFindingResponse }
  | { type: 'completed'; content: RepositoryReviewResponse }
  | { type: 'error'; content: RepositoryReviewErrorEvent };

export interface FileMetrics {
  filePath: string;
  dotnetProject?: string;
  healthScore: number;
  changes: number;
  complexityScore: number;
  maxCouplingStrength: number;
  couplingPartners: number;
  truckFactor: number;
  lintErrors: number;
  lintWarnings: number;
  trustScore: number;
  riskScore: number;
  role: string;
  concernScore: number;
  churn30d: number;
  churn90d: number;
  churn365d: number;
  bugFixCommits90d: number;
  bugFixCommits365d: number;
  bugFixRatio365d: number;
  bugFixWeightedTouches365d: number;
  recurringChurnScore: number;
}

export interface ProjectDetailResponse {
  project: ProjectInfo;
  dotnetSupport?: DotnetSupportInfo;
  summary?: ProjectSummary;
  analyses: StoredProjectAnalysis[];
  nodeCounts: Record<string, number>;
  dotnetProjects: Record<string, Record<string, number>>;
  inboundEdgeCount: number;
  outboundEdgeCount: number;
  inboundProjects: string[];
  outboundProjects: string[];
  health?: ProjectHealthSummary;
}

export interface NodeListResponse {
  items: GraphNode[];
  total: number;
  page: number;
  pageSize: number;
}

export interface NodeDetailResponse {
  node: GraphNode;
  outboundEdges: EdgeSummary[];
  inboundEdges: EdgeSummary[];
  crossRepoEdges: CrossRepoEdgeSummary[];
}

export interface NodeSourceResponse {
  filePath: string;
  startLine: number;
  endLine: number;
  content: string;
  language: string;
}

// Graph overview
export interface GraphOverviewNode {
  name: string;
  sourceGroup?: string;
  language?: string;
  framework?: string;
  isFoundational: boolean;
}

export interface GraphOverviewEdge {
  source: string;
  target: string;
  count: number;
  typeCounts: Record<string, number>;
}

export interface GraphOverviewResponse {
  nodes: GraphOverviewNode[];
  edges: GraphOverviewEdge[];
}

export interface MemoryGraphNode {
  id: string;
  label: string;
  type: string;
  summary: string;
  source?: string;
  createdAt: string;
  updatedAt: string;
}

export interface MemoryGraphLink {
  source: string;
  target: string;
  relationship: string;
  context?: string;
  timestamp: string;
}

export interface MemoryGraphResponse {
  nodes: MemoryGraphNode[];
  links: MemoryGraphLink[];
  totalNodeCount: number;
}

export interface MemoryEntity {
  id: string;
  label: string;
  type: string;
  externalId?: string;
  canonicalName?: string;
  aliases: string[];
  summary: string;
  source: string;
  createdAt: string;
  updatedAt: string;
}

export type MemoryClaimStatus = 'Active' | 'Superseded' | 'Conflicted' | 'Deprecated';

export interface MemoryClaim {
  id: string;
  claimKey: string;
  factGroupKey: string;
  subjectEntityId: string;
  predicate: string;
  objectEntityId?: string;
  valueText?: string;
  valueJson?: string;
  normalizedText: string;
  status: MemoryClaimStatus;
  confidence?: number;
  effectiveAt?: string;
  recordedAt: string;
  supersedesClaimId?: string;
  source: string;
}

export interface MemoryEntityEdge {
  fromEntityId: string;
  toEntityId: string;
  edgeType: string;
  bestActiveClaimId?: string;
  weight?: number;
  createdAt: string;
  updatedAt: string;
}

export interface MemoryObservation {
  id: string;
  claim: string;
  conflictsWith: string;
  source: string;
  timestamp: string;
  resolved: boolean;
  resolution?: string;
  resolvedByMemoryId?: string;
  aboutEntityIds: string[];
  aboutClaimIds: string[];
}

export interface MemoryEvidence {
  id: string;
  claimId?: string;
  observationId?: string;
  evidenceType: string;
  sourceRef: string;
  snippet?: string;
  metadataJson?: string;
  createdAt: string;
}

export interface MemoryEntityBundle {
  entity: MemoryEntity;
  activeClaims: MemoryClaim[];
  conflictingClaims: MemoryClaim[];
  supersededClaims: MemoryClaim[];
  neighborEdges: MemoryEntityEdge[];
  observations: MemoryObservation[];
}

export interface MemoryClaimBundle {
  claim: MemoryClaim;
  factGroupPeers: MemoryClaim[];
  supersessionChain: MemoryClaim[];
  conflicts: MemoryClaim[];
  evidence: MemoryEvidence[];
  observations: MemoryObservation[];
}

export interface MemorySeedDiagnostics {
  retrievalStage: string;
  scoreBreakdown: Record<string, number>;
  matchedFields: string[];
  matchedEntityIds: string[];
  matchedClaimIds: string[];
}

export interface MemoryEntitySeed {
  entityId: string;
  label: string;
  type: string;
  score: number;
  matchKind: string;
  diagnostics: MemorySeedDiagnostics;
}

export interface MemoryClaimSeed {
  claimId: string;
  normalizedText: string;
  predicate: string;
  status: MemoryClaimStatus;
  score: number;
  matchKind: string;
  diagnostics: MemorySeedDiagnostics;
}

export interface MemorySearchResult {
  query: string;
  entities: MemoryEntitySeed[];
  claims: MemoryClaimSeed[];
}

// Cluster (community detection)
export interface ClusterSummary {
  clusterId: number;
  label?: string;
  members: string[];
  internalEdgeCount: number;
  externalEdgeCount: number;
  density: number;
  bridgeRepos: string[];
}

export interface ClusterOverviewResponse {
  clusters: ClusterSummary[];
  modularity: number;
  totalProjects: number;
  clusteredProjects: number;
  computedAt?: string;
}

export interface ClusterMember {
  projectName: string;
  betweennessCentrality: number;
  internalEdges: number;
  externalEdges: number;
}

export interface ClusterConnection {
  targetClusterId: number;
  targetLabel?: string;
  edgeCount: number;
  edgeTypes: string[];
}

export interface ClusterDetailResponse {
  clusterId: number;
  label?: string;
  members: ClusterMember[];
  internalEdgeCount: number;
  externalEdgeCount: number;
  topConnections: ClusterConnection[];
}

export interface ClusterGraphNode {
  name: string;
  sourceGroup?: string;
  language?: string;
  framework?: string;
  isFoundational: boolean;
  clusterId?: number;
  betweennessCentrality: number;
}

export interface ClusterGraphEdge {
  source: string;
  target: string;
  count: number;
  typeCounts: Record<string, number>;
  isCrossCluster: boolean;
}

export interface ClusterInfo {
  clusterId: number;
  label?: string;
  memberCount: number;
}

export interface ClusterGraphResponse {
  nodes: ClusterGraphNode[];
  edges: ClusterGraphEdge[];
  clusters: ClusterInfo[];
  modularity: number;
}

export interface AssistantEvent {
  type: 'text' | 'tool_use' | 'done' | 'error';
  content: string;
}

// Wiki
export interface WikiSection {
  id: number;
  slug: string;
  title: string;
  description?: string;
  icon?: string;
  sortOrder: number;
  isSystem: boolean;
  allowUserPages: boolean;
  hasRawContent: boolean;
}

export interface WikiTreeNode {
  id: number;
  slug: string;
  title: string;
  depth: number;
  sortOrder: number;
  isAutoGenerated: boolean;
  children: WikiTreeNode[];
}

export interface WikiPageListItem {
  id: number;
  slug: string;
  title: string;
  author: string;
  revision: number;
  depth: number;
  isAutoGenerated: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface WikiPage extends WikiPageListItem {
  sectionId: number;
  parentId?: number;
  content: string;
  rawContent?: string;
  sortOrder: number;
  hasRawContent: boolean;
}

export interface WikiPageRequest {
  slug?: string;
  title: string;
  content: string;
  rawContent?: string;
  author?: string;
  parentId?: number;
  sortOrder?: number;
}

export interface WikiRevisionListItem {
  revision: number;
  title: string;
  author: string;
  createdAt: string;
}

export interface WikiRevision {
  revision: number;
  title: string;
  content: string;
  rawContent?: string;
  author: string;
  createdAt: string;
}

export interface WikiAttachment {
  id: number;
  filename: string;
  contentType: string;
  sizeBytes: number;
  uploadedBy: string;
  downloadUrl: string;
  createdAt: string;
}

// Impact analysis (blast radius)
export type RiskLevel = 'Critical' | 'High' | 'Medium' | 'Low';

export interface AffectedNode {
  nodeId: number;
  name: string;
  qualifiedName: string;
  label: string;
  project: string;
  dotnetProject?: string;
  filePath?: string;
  depth: number;
  edgeType: string;
  risk: RiskLevel;
  riskFactors: string[];
}

export interface CrossRepoImpact {
  sourceProject: string;
  targetProject: string;
  edgeType: string;
  affectedNodeCount: number;
}

export interface ImpactSummary {
  totalAffected: number;
  crossRepoCount: number;
  criticalCount: number;
  highCount: number;
  mediumCount: number;
  lowCount: number;
  affectedProjects: string[];
}

export interface ImpactReport {
  changedNodes: AffectedNode[];
  affectedNodes: AffectedNode[];
  crossRepoImpacts: CrossRepoImpact[];
  summary: ImpactSummary;
}

export const RISK_COLORS: Record<RiskLevel, string> = {
  Critical: '#dc2626',
  High: '#ea580c',
  Medium: '#ca8a04',
  Low: '#16a34a'
};

export const RISK_BG_COLORS: Record<RiskLevel, string> = {
  Critical: '#fef2f2',
  High: '#fff7ed',
  Medium: '#fefce8',
  Low: '#f0fdf4'
};

export interface AnalysisBatchStatus {
  id: number;
  repo: string;
  providerBatchId: string;
  providerName: string;
  executionMode: string;
  includeAllSource: boolean;
  status: string;
  requestCount: number;
  completedCount: number;
  submittedAt: string;
  completedAt?: string;
}

// Unified search
export interface UnifiedSearchResponse {
  items: SearchResultItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface SearchResultItem {
  type: 'repository' | 'project' | 'node';
  name: string;
  description?: string;
  nodeLabel?: string;
  project?: string;
  nodeId?: number;
  qualifiedName?: string;
}

export const NODE_LABELS = [
  'Repository', 'DotnetProject', 'Project', 'Namespace', 'Folder', 'File',
  'Class', 'Interface', 'Enum', 'Struct', 'Record',
  'Function', 'Method', 'Property', 'Constructor', 'Delegate',
  'Route', 'Service', 'Table', 'View', 'StoredProcedure',
  'Event', 'Queue', 'Exchange',
  'Component', 'Module',
  'Job', 'NuGetPackage'
] as const;

export type NodeLabel = typeof NODE_LABELS[number];

export const LABEL_ICONS: Record<string, string> = {
  Repository: '📦', DotnetProject: '🧱', Project: '📦', Namespace: '📂', Folder: '📁', File: '📄',
  Class: '🔷', Interface: '🔶', Enum: '🔢', Struct: '🔷', Record: '🔷',
  Function: '⚡', Method: '⚡', Property: '🔑', Constructor: '🏗️', Delegate: '⚡',
  Route: '🌐', Service: '⚙️', Table: '🗃️', View: '👁️', StoredProcedure: '🗃️',
  Event: '📨', Queue: '📬', Exchange: '🔀',
  Component: '🧩', Module: '📦',
  Job: '⏰', NuGetPackage: '📦'
};

export const CONFIDENCE_COLORS: Record<string, string> = {
  high: '#22c55e',
  medium: '#f59e0b',
  low: '#ef4444'
};
