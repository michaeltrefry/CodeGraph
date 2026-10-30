"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.ExtractionWorkBudget = exports.ExtractionWorkLimitError = exports.DEFAULT_EXTRACTION_WORK_LIMIT = void 0;
// The largest selected frontend uses about 158M units with weighted route output.
// Keep measured headroom and one finite allowance shared by all analysis passes.
exports.DEFAULT_EXTRACTION_WORK_LIMIT = 200000000;
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
        this.limit = control.maxWork ?? exports.DEFAULT_EXTRACTION_WORK_LIMIT;
        if (!Number.isSafeInteger(this.limit) || this.limit < 0)
            throw new RangeError('Extraction work limit must be a nonnegative safe integer');
    }
    get used() { return this.spent; }
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
    text(value) { this.charge(value.length + 1); return value; }
}
exports.ExtractionWorkBudget = ExtractionWorkBudget;
