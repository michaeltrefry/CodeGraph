-- nodes.file_path: file-less nodes (stubs, repository nodes) written by the bulk graph writes
-- stored NULL, which the EF node mapping (a non-nullable string, column default '') cannot read:
-- any lookup that reached such a node by name or qualified name failed. The writers now store ''
-- (sc-26141); this normalizes the rows written before. Idempotent.
UPDATE nodes SET file_path = '' WHERE file_path IS NULL;
