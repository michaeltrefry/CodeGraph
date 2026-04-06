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

export interface ProjectHealthResponse {
  repoHealth?: ProjectHealthSummary;
  projectHealths: ProjectHealthSummary[];
  topHotspots: FileMetrics[];
  analyses: ProjectHealthAnalysis[];
  securitySummary?: ProjectSecuritySummary;
}

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
}

export interface ProjectDetailResponse {
  project: ProjectInfo;
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
  anthropicBatchId: string;
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
  'Project', 'Namespace', 'Folder', 'File',
  'Class', 'Interface', 'Enum', 'Struct', 'Record',
  'Function', 'Method', 'Property', 'Constructor', 'Delegate',
  'Route', 'Service', 'Table', 'View', 'StoredProcedure',
  'Event', 'Queue', 'Exchange',
  'Component', 'Module',
  'Job', 'NuGetPackage'
] as const;

export type NodeLabel = typeof NODE_LABELS[number];

export const LABEL_ICONS: Record<string, string> = {
  Project: '📦', Namespace: '📂', Folder: '📁', File: '📄',
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
