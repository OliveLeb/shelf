-- Existing elements are files at the root of their space.
ALTER TABLE elements
    ADD COLUMN kind text NOT NULL DEFAULT 'file' CHECK (kind IN ('file', 'folder')),
    ADD COLUMN parent_id uuid,
    ALTER COLUMN size DROP NOT NULL,
    ALTER COLUMN content_type DROP NOT NULL,
    ALTER COLUMN object_key DROP NOT NULL,
    ADD CONSTRAINT elements_kind_columns CHECK (CASE kind
        WHEN 'file' THEN size IS NOT NULL AND content_type IS NOT NULL AND object_key IS NOT NULL
        ELSE size IS NULL AND content_type IS NULL AND object_key IS NULL END),
    ADD CONSTRAINT elements_id_space UNIQUE (id, space_id),
    -- A parent belongs to the same space as its children.
    ADD CONSTRAINT elements_parent FOREIGN KEY (parent_id, space_id) REFERENCES elements (id, space_id) ON DELETE CASCADE;
ALTER TABLE elements ALTER COLUMN kind DROP DEFAULT;
-- Names are unique per folder ignoring case; the root of a space (no parent) counts as a folder.
DROP INDEX elements_name_unique;
CREATE UNIQUE INDEX elements_name_unique ON elements (space_id, parent_id, upper(name)) NULLS NOT DISTINCT;
ALTER TABLE uploads
    ADD COLUMN parent_id uuid REFERENCES elements (id) ON DELETE CASCADE,
    ADD COLUMN on_conflict text CHECK (on_conflict IN ('keepBoth'));
