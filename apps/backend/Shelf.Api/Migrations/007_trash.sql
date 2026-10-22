CREATE TABLE trash_entries (
    id uuid PRIMARY KEY,
    space_id uuid NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
    original_parent_id uuid,
    original_path text NOT NULL,
    deleted_at timestamptz NOT NULL,
    deleted_by uuid REFERENCES accounts(id) ON DELETE SET NULL
);
CREATE INDEX trash_entries_expiry ON trash_entries(deleted_at);
ALTER TABLE elements ADD COLUMN trash_id uuid REFERENCES trash_entries(id) ON DELETE CASCADE;
CREATE INDEX elements_trash ON elements(trash_id) WHERE trash_id IS NOT NULL;
DROP INDEX elements_name_unique;
CREATE UNIQUE INDEX elements_name_unique ON elements (space_id, parent_id, upper(name)) NULLS NOT DISTINCT WHERE trash_id IS NULL;

-- The outbox has no foreign keys: work survives deletion of elements, spaces and accounts.
CREATE TABLE object_deletions (
    object_key text PRIMARY KEY,
    attempts integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT '-infinity'
);
CREATE INDEX object_deletions_due ON object_deletions(next_attempt_at);
CREATE FUNCTION enqueue_deleted_object() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    -- Upload confirmation transfers the key to an element before deleting the upload.
    IF OLD.object_key IS NOT NULL AND NOT EXISTS (SELECT 1 FROM elements WHERE object_key=OLD.object_key) THEN
        INSERT INTO object_deletions (object_key) VALUES (OLD.object_key) ON CONFLICT DO NOTHING;
    END IF;
    RETURN OLD;
END;
$$;
CREATE TRIGGER elements_object_deletion AFTER DELETE ON elements FOR EACH ROW EXECUTE FUNCTION enqueue_deleted_object();
-- A purged folder may contain pending uploads which already have S3 objects.
CREATE TRIGGER uploads_object_deletion AFTER DELETE ON uploads FOR EACH ROW EXECUTE FUNCTION enqueue_deleted_object();

ALTER TABLE uploads DROP CONSTRAINT uploads_on_conflict_check;
ALTER TABLE uploads ADD CONSTRAINT uploads_on_conflict_check CHECK (on_conflict IN ('keepBoth', 'replace'));
