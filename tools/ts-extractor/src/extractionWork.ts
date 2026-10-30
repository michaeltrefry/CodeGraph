export const DEFAULT_EXTRACTION_WORK_LIMIT = 20_000_000;
export interface ExtractionWorkControl {
  maxWork?: number;
  /** Synchronous polling. A timer/AbortSignal cannot preempt CPU while this stack runs. */
  shouldCancel?: () => boolean;
  onWork?: (work: number) => void;
}
export class ExtractionWorkLimitError extends Error {
  readonly code = 'extraction_work_incomplete';
  constructor(readonly reason: 'work_limit' | 'cancelled', readonly work: number, readonly limit: number) {
    super(`Extraction work refused: ${reason} (work=${work}, limit=${limit})`);
    this.name = 'ExtractionWorkLimitError';
  }
}
/** Per-extraction shared work, including text and emitted evidence. Refusal poisons the run. */
export class ExtractionWorkBudget {
  private spent = 0;
  private failure?: ExtractionWorkLimitError;
  readonly limit: number;
  constructor(private readonly control: ExtractionWorkControl = {}) {
    this.limit = control.maxWork ?? DEFAULT_EXTRACTION_WORK_LIMIT;
    if (!Number.isSafeInteger(this.limit) || this.limit < 0) throw new RangeError('Extraction work limit must be a nonnegative safe integer');
  }
  get used(): number { return this.spent; }
  charge(units = 1): void {
    if (this.failure) throw this.failure;
    let reason: 'work_limit' | 'cancelled' | undefined;
    if (this.control.shouldCancel?.()) reason = 'cancelled';
    else if (!Number.isSafeInteger(units) || units < 0 || units > this.limit - this.spent) reason = 'work_limit';
    if (reason) { this.failure = new ExtractionWorkLimitError(reason, this.spent, this.limit); throw this.failure; }
    this.spent += units; this.control.onWork?.(this.spent);
  }
  text(value: string): string { this.charge(value.length + 1); return value; }
}
