import * as fs from 'fs';
import * as path from 'path';
import { StringDecoder } from 'string_decoder';
import type { SourceReadLimits } from './protocol';

export class SourceAdmissionError extends Error { readonly code = 'source_admission_incomplete'; }

const DEFAULT_FILE_KIB = 512;
const DEFAULT_TEXT_KIB = 65536;
const MAX_DIRECTORY_ENTRIES = 100000;
const MAX_READ_FILES = 20000;

/** Scope metadata has its own cap: zero-byte sources still consume parser/file bookkeeping. */
export function validateSourceScope(files: readonly string[] | undefined): void {
  if (files === undefined) return;
  if (!Array.isArray(files) || files.length > 50000) throw new SourceAdmissionError('source scope exceeds 50000 admitted paths');
  let characters = 0;
  for (const file of files) {
    if (typeof file !== 'string') throw new SourceAdmissionError('source scope must contain only path strings');
    const normalized = path.normalize(file.replace(/\\/g, '/'));
    if (file.includes('\0') || path.isAbsolute(normalized) || /^[A-Za-z]:[\\/]/.test(file) || normalized === '..' || normalized.startsWith(`..${path.sep}`)) throw new SourceAdmissionError('source scope paths must stay within the repository');
    characters += file.length;
    if (characters > 8 * 1024 * 1024) throw new SourceAdmissionError('source scope exceeds 8Mi decoded path characters');
  }
}

/** One extraction's admitted text snapshot. Every consumer shares the same decoded UTF-16
 * budgets, cached refusals and handle-verified bytes; parser libraries never reopen source paths. */
export class BoundedTextReader {
  complete = true;
  private readonly cache = new Map<string, string | undefined>();
  private readonly directories = new Map<string, fs.Dirent[]>();
  private readonly root: string;
  private readonly realRoot: string;
  private readonly fileLimit: number;
  private readonly textLimit: number;
  private readonly extensionLimits = new Map<string, number>();
  private decoded = 0;
  private directoryEntries = 0;
  private reports = 0;
  constructor(root: string, private readonly admitted?: (absolute: string) => boolean,
    limits: SourceReadLimits = {}, private readonly diagnostic: (message: string) => void = () => {}) {
    this.root = path.resolve(root);
    this.fileLimit = this.limit(limits.maxFileSizeKb, DEFAULT_FILE_KIB);
    this.textLimit = this.limit(limits.maxAnalysisTextKb, DEFAULT_TEXT_KIB);
    for (const [extension, value] of Object.entries(limits.maxFileSizeKbByExtension ?? {})) {
      this.extensionLimits.set(`.${extension.replace(/^\./, '').toLowerCase()}`, this.limit(value, DEFAULT_FILE_KIB));
    }
    try { this.realRoot = fs.realpathSync(this.root); }
    catch { this.realRoot = this.root; this.refuse(this.root, 'repository root is unavailable'); }
  }
  private limit(value: number | undefined, fallback: number): number {
    if (value === undefined) return fallback * 1024;
    if (!Number.isSafeInteger(value) || value < 0 || value > Number.MAX_SAFE_INTEGER / 4096) {
      this.refuse(this.root ?? '', 'invalid source text budget'); return fallback * 1024;
    }
    return value * 1024;
  }
  private refuse(file: string, reason: string): undefined {
    this.complete = false;
    if (this.reports++ < 25) this.diagnostic(`sourceAdmission: ${(path.relative(this.root ?? '.', file).replace(/\\/g, '/') || '.').slice(0, 240)}: ${reason.slice(0, 160)}`);
    return undefined;
  }
  allows(file: string): boolean {
    const absolute = path.resolve(file); const relative = path.relative(this.root, absolute);
    return relative !== '' && relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative) && (!this.admitted || this.admitted(absolute));
  }
  /** Refuse symlinks below the checkout root, including parent-directory escapes. The root itself
   * may have a platform alias (e.g. macOS /var); all identity checks use its canonical location. */
  private inspect(file: string): fs.Stats {
    const relative = path.relative(this.root, file);
    let current = this.root;
    const pieces = relative.split(path.sep);
    for (let index = 0; index < pieces.length; index++) {
      current = path.join(current, pieces[index]);
      const stat = fs.lstatSync(current);
      if (stat.isSymbolicLink()) throw new Error('symlink input is not admitted');
      if (index < pieces.length - 1 && !stat.isDirectory()) throw new Error('input ancestor is not a directory');
      if (index === pieces.length - 1) return stat;
    }
    throw new Error('input must be a file');
  }
  private verify(file: string, handle: fs.Stats): void {
    const current = this.inspect(file);
    const real = fs.realpathSync(file); const relative = path.relative(this.realRoot, real);
    if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative) || !sameFile(handle, current)) throw new Error('input identity or containment changed');
  }
  exists(file: string): boolean {
    const absolute = path.resolve(file);
    if (!this.allows(absolute)) return false;
    try {
      const stat = this.inspect(absolute);
      if (stat.isDirectory()) return false; // Module resolution may next probe this directory's index file.
      if (!stat.isFile()) { this.refuse(absolute, 'input is not a regular file'); return false; }
      return true;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT' && (error as NodeJS.ErrnoException).code !== 'ENOTDIR') this.refuse(absolute, accessReason(error));
      return false;
    }
  }
  read(file: string, required = false): string | undefined {
    const absolute = path.resolve(file);
    if (this.cache.has(absolute)) {
      const cached = this.cache.get(absolute);
      if (cached === undefined && required) this.refuse(absolute, 'required input was previously missing or refused');
      return cached;
    }
    if (!this.allows(absolute)) return required ? this.refuse(absolute, 'input is outside the admitted repository scope') : undefined;
    this.cache.set(absolute, undefined);
    if (this.decoded > this.textLimit) return this.refuse(absolute, 'aggregate decoded text budget exhausted');
    if (this.cache.size > MAX_READ_FILES) return this.refuse(absolute, 'file read count limit exceeded');
    let descriptor: number | undefined;
    try {
      const before = this.inspect(absolute);
      if (!before.isFile()) throw new Error('input is not a regular file');
      const limit = this.extensionLimits.get(path.extname(absolute).toLowerCase()) ?? this.fileLimit;
      if (before.size > 4 * limit + 4) throw new Error('file exceeds its decoded text size budget');
      descriptor = fs.openSync(absolute, fs.constants.O_RDONLY | fs.constants.O_NOFOLLOW | fs.constants.O_NONBLOCK);
      const opened = fs.fstatSync(descriptor);
      if (!opened.isFile() || !sameFile(opened, before)) throw new Error('opened input is not the admitted regular file');
      this.verify(absolute, opened);
      const decoder = new StringDecoder('utf8');
      const buffer = Buffer.allocUnsafe(8192);
      const parts: string[] = [];
      let length = 0;
      const append = (text: string) => {
        length += text.length; this.decoded += text.length;
        // Charge decoded prefixes even for refused files, so failures cannot reset the budget.
        if (length > limit || this.decoded > this.textLimit) throw new Error('decoded file or aggregate text budget exceeded');
        parts.push(text);
      };
      while (true) {
        const remaining = Math.min(limit - length, this.textLimit - this.decoded);
        const count = fs.readSync(descriptor, buffer, 0, Math.min(buffer.length, remaining + 1), null);
        if (!count) break;
        append(decoder.write(buffer.subarray(0, count)));
      }
      append(decoder.end());
      const after = fs.fstatSync(descriptor);
      if (!sameFile(opened, after)) throw new Error('input changed during bounded read');
      this.verify(absolute, after);
      const text = parts.join(''); this.cache.set(absolute, text); return text;
    } catch (error) {
      if (!required && ['ENOENT', 'ENOTDIR'].includes((error as NodeJS.ErrnoException).code ?? '')) return undefined;
      return this.refuse(absolute, accessReason(error));
    } finally { if (descriptor !== undefined) fs.closeSync(descriptor); }
  }
  directoryExists(directory: string): boolean {
    const absolute = path.resolve(directory);
    if (absolute === this.root) {
      try { if (fs.realpathSync(this.root) === this.realRoot) return true; } catch { /* Refuse a removed/swapped root. */ }
      this.refuse(absolute, 'repository directory identity changed'); return false;
    }
    const relative = path.relative(this.root, absolute);
    if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) return false;
    try { return this.inspect(absolute).isDirectory(); } catch { return false; }
  }
  entries(directory: string): fs.Dirent[] {
    const absolute = path.resolve(directory);
    if (this.directories.has(absolute)) return this.directories.get(absolute)!;
    if (!this.directoryExists(absolute)) return [];
    const entries: fs.Dirent[] = []; this.directories.set(absolute, entries);
    let handle: fs.Dir | undefined;
    try {
      handle = fs.opendirSync(absolute);
      let entry: fs.Dirent | null;
      while ((entry = handle.readSync()) !== null) {
        if (++this.directoryEntries > MAX_DIRECTORY_ENTRIES) { this.refuse(absolute, 'directory discovery budget exceeded'); break; }
        // Do not follow or count symlinked directories as admitted source trees.
        if (!entry.isSymbolicLink()) entries.push(entry);
      }
    } catch (error) { this.refuse(absolute, accessReason(error)); }
    finally { handle?.closeSync(); }
    return entries;
  }
}
function sameFile(left: fs.Stats, right: fs.Stats): boolean {
  return left.dev === right.dev && left.ino === right.ino && left.size === right.size && left.mtimeMs === right.mtimeMs && left.ctimeMs === right.ctimeMs;
}

function accessReason(error: unknown): string {
  const code = (error as NodeJS.ErrnoException)?.code;
  return code ? `file access failed (${code})` : error instanceof Error ? error.message : 'file access failed';
}
