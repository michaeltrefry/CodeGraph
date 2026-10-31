// Sized against selected indexed frontends, including relocated checkout snapshots.
// Keep finite shared work/output bounds with room for normal source growth.
export const DEFAULT_EXTRACTION_WORK_LIMIT = 600_000_000;
export const DEFAULT_RETAINED_AST_NODES = 3_000_000;
export const DEFAULT_OUTPUT_ITEMS = 250_000;
export const MAX_PARSE_SOURCE_UNITS = 2 * 1024 * 1024;
export interface ExtractionWorkControl {
  /** Trusted caller/test controls; these are not extraction request JSON options. */
  maxAstNodes?: number;
  maxOutputItems?: number;
  maxWork?: number;
  /** Synchronous polling. A timer/AbortSignal cannot preempt CPU while this stack runs. */
  shouldCancel?: () => boolean;
  onWork?: (work: number) => void;
}
export class ExtractionWorkLimitError extends Error {
  readonly code = 'extraction_work_incomplete';
  constructor(readonly reason: 'work_limit' | 'cancelled' | 'depth_limit' | 'retention_limit', readonly work: number, readonly limit: number) {
    super(`Extraction work refused: ${reason} (work=${work}, limit=${limit})`);
    this.name = 'ExtractionWorkLimitError';
  }
}
/** Per-extraction shared work, including text and emitted evidence. Refusal poisons the run. */
export class ExtractionWorkBudget {
  private spent = 0;
  private astNodes = 0;
  private outputItems = 0;
  private readonly accountedTrees = new WeakMap<object, number>();
  private readonly astLimit: number;
  private readonly outputLimit: number;
  private failure?: ExtractionWorkLimitError;
  readonly limit: number;
  constructor(private readonly control: ExtractionWorkControl = {}) {
    const astLimit=control.maxAstNodes ?? DEFAULT_RETAINED_AST_NODES; const outputLimit=control.maxOutputItems ?? DEFAULT_OUTPUT_ITEMS;
    if (![astLimit,outputLimit].every(value=>Number.isSafeInteger(value)&&value>=0)) throw new RangeError('Retention limits must be nonnegative safe integers');
    this.astLimit=Math.min(astLimit,DEFAULT_RETAINED_AST_NODES); this.outputLimit=Math.min(outputLimit,DEFAULT_OUTPUT_ITEMS);
    this.limit = control.maxWork ?? DEFAULT_EXTRACTION_WORK_LIMIT;
    if (!Number.isSafeInteger(this.limit) || this.limit < 0) throw new RangeError('Extraction work limit must be a nonnegative safe integer');
  }
  get used(): number { return this.spent; }
  get retainedAstNodes(): number { return this.astNodes; }
  get retainedOutputItems(): number { return this.outputItems; }
  reserveSource(codeUnits: number): void {
    this.charge(codeUnits * 4 + 1);
    if(codeUnits>MAX_PARSE_SOURCE_UNITS) { this.failure=new ExtractionWorkLimitError('retention_limit',this.spent,this.limit); throw this.failure; }
  }
  retainAstNode(): void {
    this.charge(32);
    if (this.astNodes >= this.astLimit) { this.failure = new ExtractionWorkLimitError('retention_limit',this.spent,this.limit); throw this.failure; }
    this.astNodes++;
  }
  accountAst(root: object, children: (node: object, visit: (child: object) => void) => void): number {
    const prior=this.accountedTrees.get(root); if(prior!==undefined) { this.charge(); return prior; }
    let count=0; const pending: object[]=[root];
    while(pending.length) { const node=pending.pop()!; this.retainAstNode(); count++; children(node,child=>{pending.push(child);}); }
    this.accountedTrees.set(root,count); return count;
  }
  reserveAstNodes(count:number): void {
    this.charge(count*32);
    if(!Number.isSafeInteger(count)||count<0||count>this.astLimit-this.astNodes) { this.failure=new ExtractionWorkLimitError('retention_limit',this.spent,this.limit); throw this.failure; }
    this.astNodes+=count;
  }
  parse<T>(text:string, factory:()=>T, tree:(value:T)=>object, children:(node:object, visit:(child:object)=>void)=>void):T {
    this.reserveSource(text.length); const value=factory(); this.accountAst(tree(value),children); return value;
  }
  createNode<T>(factory:()=>T):T { this.reserveOutput('node'); return factory(); }
  createEdge<T>(factory:()=>T):T { this.reserveOutput('edge'); return factory(); }
  createMetadata<T>(factory:()=>T):T { this.reserveOutput('metadata'); return factory(); }
  /** Every DTO factory and final evidence/ownership copy shares this per-run ceiling. */
  reserveOutput(kind: 'node' | 'edge' | 'metadata', textUnits = 0): void {
    this.charge((kind === 'node' ? 1024 : kind === 'edge' ? 512 : 128) + textUnits * 2);
    if (this.outputItems >= this.outputLimit) { this.failure = new ExtractionWorkLimitError('retention_limit',this.spent,this.limit); throw this.failure; }
    this.outputItems++;
  }
  charge(units = 1): void {
    if (this.failure) throw this.failure;
    let reason: 'work_limit' | 'cancelled' | undefined;
    if (this.control.shouldCancel?.()) reason = 'cancelled';
    else if (!Number.isSafeInteger(units) || units < 0 || units > this.limit - this.spent) reason = 'work_limit';
    if (reason) { this.failure = new ExtractionWorkLimitError(reason, this.spent, this.limit); throw this.failure; }
    this.spent += units; this.control.onWork?.(this.spent);
  }
  checkDepth(depth: number, maximum: number): void {
    this.charge();
    if (!Number.isSafeInteger(depth) || depth < 0 || depth > maximum) {
      this.failure = new ExtractionWorkLimitError('depth_limit', this.spent, this.limit); throw this.failure;
    }
  }
  text(value: string): string { this.charge(value.length + 1); return value; }
}
