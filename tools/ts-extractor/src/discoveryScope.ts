import * as path from 'path';

/**
 * The repository files the indexing pipeline's discovery admits (repo-relative). When CodeGraph
 * passes this list, it is the only source of files the sidecar reads: TypeScript sources,
 * tsconfig.json and package.json files outside it (agent worktrees, dependency directories and
 * every other discovery exclusion) are never analyzed, and no tsconfig `include`/`references`
 * glob can pull them back in.
 */
export class DiscoveryScope {
  private readonly files = new Set<string>();
  private readonly directories = new Set<string>(['']);

  constructor(readonly rootPath: string, includedFiles: readonly string[]) {
    for (const file of includedFiles) {
      const relative = normalizeRelative(file);
      if (!relative || relative.startsWith('../') || path.isAbsolute(relative)) continue;
      this.files.add(relative);
      let directory = path.posix.dirname(relative);
      while (directory !== '.' && directory !== '' && !this.directories.has(directory)) {
        this.directories.add(directory);
        directory = path.posix.dirname(directory);
      }
    }
  }

  /** Absolute paths of the admitted files accepted by `predicate`. */
  absoluteFiles(predicate: (relativePath: string) => boolean = () => true): string[] {
    return [...this.files]
      .filter(predicate)
      .sort()
      .map(relative => path.join(this.rootPath, relative));
  }

  /** True when the directory (repo-relative, '' for the root) holds at least one admitted file. */
  includesDirectory(relativeDirectory: string): boolean {
    return this.directories.has(normalizeRelative(relativeDirectory));
  }
}

function normalizeRelative(filePath: string): string {
  const normalized = path.posix.normalize(filePath.replace(/\\/g, '/')).replace(/^\.\/?/, '').replace(/\/$/, '');
  return normalized === '.' ? '' : normalized;
}
