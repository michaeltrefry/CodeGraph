// Backfill legacy analysis batch records that still use the old anthropicBatchId property,
// then remove the compatibility bridge property once providerBatchId is present.

MATCH (b:AnalysisBatch)
WHERE b.providerBatchId IS NULL AND b.anthropicBatchId IS NOT NULL
SET b.providerBatchId = b.anthropicBatchId;

MATCH (b:AnalysisBatch)
WHERE b.anthropicBatchId IS NOT NULL
REMOVE b.anthropicBatchId;
