// Durable write receipts for agent-facing memory writes.

CREATE CONSTRAINT memory_write_receipt_id IF NOT EXISTS
FOR (r:MemoryWriteReceipt) REQUIRE r.id IS UNIQUE;

CREATE INDEX memory_write_receipt_status IF NOT EXISTS
FOR (r:MemoryWriteReceipt) ON (r.status);

CREATE INDEX memory_write_receipt_source IF NOT EXISTS
FOR (r:MemoryWriteReceipt) ON (r.source);

CREATE INDEX memory_write_receipt_created_at IF NOT EXISTS
FOR (r:MemoryWriteReceipt) ON (r.createdAt);
