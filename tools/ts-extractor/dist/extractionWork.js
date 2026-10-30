"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.ExtractionWorkBudget = exports.ExtractionWorkLimitError = exports.MAX_PARSE_SOURCE_UNITS = exports.DEFAULT_OUTPUT_ITEMS = exports.DEFAULT_RETAINED_AST_NODES = exports.DEFAULT_EXTRACTION_WORK_LIMIT = void 0;
// Sized against selected indexed frontends, including relocated checkout snapshots.
// Keep finite shared work/output bounds with room for normal source growth.
exports.DEFAULT_EXTRACTION_WORK_LIMIT = 400000000;
exports.DEFAULT_RETAINED_AST_NODES = 3000000;
exports.DEFAULT_OUTPUT_ITEMS = 250000;
exports.MAX_PARSE_SOURCE_UNITS = 2 * 1024 * 1024;
class ExtractionWorkLimitError extends Error {
    constructor(reason, work, limit) {
        super(`Extraction work refused: ${reason} (work=${work}, limit=${limit})`);
        this.reason = reason;
        this.work = work;
        this.limit = limit;
        this.code = 'extraction_work_incomplete';
        this.name = 'ExtractionWorkLimitError';
    }
}
exports.ExtractionWorkLimitError = ExtractionWorkLimitError;
/** Per-extraction shared work, including text and emitted evidence. Refusal poisons the run. */
class ExtractionWorkBudget {
    constructor(control = {}) {
        this.control = control;
        this.spent = 0;
        this.astNodes = 0;
        this.outputItems = 0;
        const astLimit = control.maxAstNodes ?? exports.DEFAULT_RETAINED_AST_NODES;
        const outputLimit = control.maxOutputItems ?? exports.DEFAULT_OUTPUT_ITEMS;
        if (![astLimit, outputLimit].every(value => Number.isSafeInteger(value) && value >= 0))
            throw new RangeError('Retention limits must be nonnegative safe integers');
        this.astLimit = Math.min(astLimit, exports.DEFAULT_RETAINED_AST_NODES);
        this.outputLimit = Math.min(outputLimit, exports.DEFAULT_OUTPUT_ITEMS);
        this.limit = control.maxWork ?? exports.DEFAULT_EXTRACTION_WORK_LIMIT;
        if (!Number.isSafeInteger(this.limit) || this.limit < 0)
            throw new RangeError('Extraction work limit must be a nonnegative safe integer');
    }
    get used() { return this.spent; }
    get retainedAstNodes() { return this.astNodes; }
    get retainedOutputItems() { return this.outputItems; }
    reserveSource(codeUnits) {
        this.charge(codeUnits * 4 + 1);
        if (codeUnits > exports.MAX_PARSE_SOURCE_UNITS) {
            this.failure = new ExtractionWorkLimitError('retention_limit', this.spent, this.limit);
            throw this.failure;
        }
    }
    retainAstNode() {
        this.charge(32);
        if (this.astNodes >= this.astLimit) {
            this.failure = new ExtractionWorkLimitError('retention_limit', this.spent, this.limit);
            throw this.failure;
        }
        this.astNodes++;
    }
    /** Core DTOs and final evidence/ownership copies. Framework passes retain their own precharged emitters. */
    reserveOutput(kind, textUnits = 0) {
        this.charge((kind === 'node' ? 1024 : kind === 'edge' ? 512 : 128) + textUnits * 2);
        if (this.outputItems >= this.outputLimit) {
            this.failure = new ExtractionWorkLimitError('retention_limit', this.spent, this.limit);
            throw this.failure;
        }
        this.outputItems++;
    }
    charge(units = 1) {
        if (this.failure)
            throw this.failure;
        let reason;
        if (this.control.shouldCancel?.())
            reason = 'cancelled';
        else if (!Number.isSafeInteger(units) || units < 0 || units > this.limit - this.spent)
            reason = 'work_limit';
        if (reason) {
            this.failure = new ExtractionWorkLimitError(reason, this.spent, this.limit);
            throw this.failure;
        }
        this.spent += units;
        this.control.onWork?.(this.spent);
    }
    checkDepth(depth, maximum) {
        this.charge();
        if (!Number.isSafeInteger(depth) || depth < 0 || depth > maximum) {
            this.failure = new ExtractionWorkLimitError('depth_limit', this.spent, this.limit);
            throw this.failure;
        }
    }
    text(value) { this.charge(value.length + 1); return value; }
}
exports.ExtractionWorkBudget = ExtractionWorkBudget;
