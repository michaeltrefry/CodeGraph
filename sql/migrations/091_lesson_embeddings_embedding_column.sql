-- 084 first named this column `vector`, which MariaDB 11.7+ reserves as a type name. Databases that
-- applied that version (on an older server) still have the old name; fresh ones already have embedding.
ALTER TABLE lesson_embeddings CHANGE COLUMN IF EXISTS `vector` embedding LONGBLOB NOT NULL;
