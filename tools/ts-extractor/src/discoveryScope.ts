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
  /** Direct children (basenames) of each admitted directory, '' for the root. */
  private readonly entries = new Map<string, { files: string[]; directories: string[] }>([
    ['', { files: [], directories: [] }],
  ]);

  constructor(readonly rootPath: string, includedFiles: readonly string[]) {
    for (const file of includedFiles) {
      const relative = normalizeRelative(file);
      if (!relative || relative.startsWith('../') || path.isAbsolute(relative) || this.files.has(relative)) continue;
      this.files.add(relative);
      this.entriesOf(parentOf(relative)).files.push(path.posix.basename(relative));
      let directory = parentOf(relative);
      while (directory !== '' && !this.directories.has(directory)) {
        this.directories.add(directory);
        this.entriesOf(parentOf(directory)).directories.push(path.posix.basename(directory));
        directory = parentOf(directory);
      }
    }
    for (const entry of this.entries.values()) {
      entry.files.sort();
      entry.directories.sort();
    }
  }

  /**
   * The admitted entries (basenames) directly inside an absolute directory, in the shape of
   * TypeScript's file system entries, so tsconfig `include`/`exclude` globs are matched against
   * the admitted files only.
   */
  fileSystemEntries(absoluteDirectory: string): { files: readonly string[]; directories: readonly string[] } {
    const relative = path.relative(this.rootPath, absoluteDirectory).replace(/\\/g, '/');
    if (relative.startsWith('..') || path.isAbsolute(relative)) return { files: [], directories: [] };
    return this.entries.get(normalizeRelative(relative)) ?? { files: [], directories: [] };
  }

  private entriesOf(directory: string): { files: string[]; directories: string[] } {
    let entry = this.entries.get(directory);
    if (!entry) {
      entry = { files: [], directories: [] };
      this.entries.set(directory, entry);
    }
    return entry;
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

function parentOf(relativePath: string): string {
  const parent = path.posix.dirname(relativePath);
  return parent === '.' ? '' : parent;
}

function normalizeRelative(filePath: string): string {
  const normalized = path.posix.normalize(filePath.replace(/\\/g, '/')).replace(/^\.\/?/, '').replace(/\/$/, '');
  return normalized === '.' ? '' : normalized;
}
