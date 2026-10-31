/** Budgets count decoded UTF-16 code units (KiB * 1024), matching the owning C# reader. */
export interface SourceReadLimits {
  maxFileSizeKb?: number;
  maxFileSizeKbByExtension?: Record<string, number>;
  maxAnalysisTextKb?: number;
}

export interface ExtractProjectRequest extends SourceReadLimits {
  repositoryName: string;
  rootPath: string;
  tsconfigPath: string;
  /**
   * Repo-relative files CodeGraph's discovery admits (TypeScript sources, tsconfig.json,
   * package.json). When present, the sidecar reads no file outside it; absent, it walks the disk.
   */
  includedFiles?: string[];
  /**
   * Path of a JSON file holding the `includedFiles` array; takes precedence over the inline list.
   * CodeGraph uses it so a large repository's scope never meets a request body limit.
   */
  includedFilesPath?: string;
}

export interface GraphNodeDto {
  label: string;
  name: string;
  qualifiedName: string;
  filePath: string;
  startLine: number;
  endLine: number;
  properties: Record<string, unknown>;
}

export interface PendingEdgeDto {
  sourceQN: string;
  targetQN: string;
  type: string;
  properties?: Record<string, unknown>;
}

export interface UnresolvedImportDto {
  fileQN: string;
  importedNamespace: string;
}

export type ResolvedImportClassification =
  | 'local_file'
  | 'local_workspace_package'
  | 'external_npm_package'
  | 'unresolved_alias'
  | 'unsupported_dynamic_expression';

export type ResolvedImportKind = 'static' | 'export' | 'dynamic';

export interface ResolvedImportDto {
  fileQN: string;
  filePath: string;
  importedNamespace: string;
  importKind: ResolvedImportKind;
  classification: ResolvedImportClassification;
  resolvedFilePath?: string;
  targetFileQN?: string;
  targetWorkspacePackage?: string;
  targetPackageQN?: string;
  externalPackageName?: string;
  diagnostic?: string;
  isBarrel?: boolean;
  barrelTargetFilePath?: string;
}

export interface WorkspacePackageDto {
  name: string;
  qualifiedName: string;
  rootPath: string;
  kind: string;
  packageJsonPath?: string;
  tsconfigPath?: string;
}

export interface UnresolvedCallDto {
  callerQN: string;
  calleeName: string;
  receiverType?: string;
  confidence: number;
}

export interface ExtractProjectResponse {
  /** False means missing/failed source analysis; absence must never imply deletion. */
  complete?: boolean;
  nodes: GraphNodeDto[];
  edges: PendingEdgeDto[];
  workspacePackages: WorkspacePackageDto[];
  resolvedImports: ResolvedImportDto[];
  unresolvedImports: UnresolvedImportDto[];
  unresolvedCalls: UnresolvedCallDto[];
  processedFiles: string[];
  diagnostics?: string[];
}
