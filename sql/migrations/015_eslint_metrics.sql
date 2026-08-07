-- Add lint signal columns and trust score to file_metrics
ALTER TABLE file_metrics
    ADD COLUMN IF NOT EXISTS lint_errors   INT NOT NULL DEFAULT 0 AFTER longest_function,
    ADD COLUMN IF NOT EXISTS lint_warnings INT NOT NULL DEFAULT 0 AFTER lint_errors,
    ADD COLUMN IF NOT EXISTS trust_score   DOUBLE NOT NULL DEFAULT 0.5 AFTER lint_warnings;
