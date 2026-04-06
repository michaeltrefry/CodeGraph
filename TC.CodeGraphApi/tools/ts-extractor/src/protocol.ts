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

export interface UnresolvedCallDto {
  callerQN: string;
  calleeName: string;
  receiverType?: string;
  confidence: number;
}

export interface ExtractProjectResponse {
  nodes: GraphNodeDto[];
  edges: PendingEdgeDto[];
  unresolvedImports: UnresolvedImportDto[];
  unresolvedCalls: UnresolvedCallDto[];
  diagnostics?: string[];
}
