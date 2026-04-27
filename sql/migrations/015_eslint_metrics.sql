-- Add lint signal columns and trust score to file_metrics
ALTER TABLE file_metrics
    ADD COLUMN lint_errors   INT NOT NULL DEFAULT 0 AFTER longest_function,
    ADD COLUMN lint_warnings INT NOT NULL DEFAULT 0 AFTER lint_errors,
    ADD COLUMN trust_score   DOUBLE NOT NULL DEFAULT 0.5 AFTER lint_warnings;
