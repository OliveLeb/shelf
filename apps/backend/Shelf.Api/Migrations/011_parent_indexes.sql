-- The active-name index excludes trash, but foreign-key cascades must find all children.
CREATE INDEX elements_parent_space ON elements (parent_id, space_id);
-- Deleting a folder also detaches pending uploads until their signed URLs expire.
CREATE INDEX uploads_parent_space ON uploads (parent_id, space_id);
