-- A NULL owner identifies the single common space. Keep 004 available for multipart uploads (#6).
ALTER TABLE spaces ALTER COLUMN owner_id DROP NOT NULL;
CREATE UNIQUE INDEX spaces_common_unique ON spaces ((owner_id IS NULL)) WHERE owner_id IS NULL;
INSERT INTO spaces (id, owner_id) VALUES (gen_random_uuid(), NULL);
-- A transferred subtree and its pending uploads change space in the same transaction.
ALTER TABLE elements ALTER CONSTRAINT elements_parent DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE uploads ALTER CONSTRAINT uploads_parent DEFERRABLE INITIALLY DEFERRED;
