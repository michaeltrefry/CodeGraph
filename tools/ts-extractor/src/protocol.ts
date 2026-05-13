export interface ExtractProjectRequest {
  projectName: string;
  rootPath: string;
  tsconfigPath: string;
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
  nodes: GraphNodeDto[];
  edges: PendingEdgeDto[];
  workspacePackages: WorkspacePackageDto[];
  resolvedImports: ResolvedImportDto[];
  unresolvedImports: UnresolvedImportDto[];
  unresolvedCalls: UnresolvedCallDto[];
  diagnostics?: string[];
}
