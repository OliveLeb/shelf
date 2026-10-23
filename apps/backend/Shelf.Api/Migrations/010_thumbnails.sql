ALTER TABLE elements ADD COLUMN thumbnail_state text CHECK (thumbnail_state IN ('pending', 'available', 'unavailable'));
ALTER TABLE elements ADD COLUMN thumbnail_key text UNIQUE;
ALTER TABLE elements ADD COLUMN thumbnail_started_at timestamptz;
UPDATE elements SET thumbnail_state='pending' WHERE kind='file';
CREATE INDEX elements_thumbnail_pending ON elements(id) WHERE thumbnail_state='pending';

-- Confirmation and copies enqueue work in the same transaction as publishing the file.
CREATE FUNCTION prepare_thumbnail() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    -- A PUT may have succeeded before publication failed or the worker died. Abandon
    -- the reservation atomically so the outbox (and late-write reconciliation) can reclaim it.
    IF TG_OP='UPDATE' AND NEW.thumbnail_state='unavailable' AND OLD.thumbnail_key IS NOT NULL THEN
        INSERT INTO object_deletions (object_key) VALUES (OLD.thumbnail_key) ON CONFLICT DO NOTHING;
        NEW.thumbnail_key=NULL;
    END IF;
    IF TG_OP='UPDATE' AND NEW.object_key IS DISTINCT FROM OLD.object_key THEN
        NEW.thumbnail_state='pending';
        NEW.thumbnail_key=NULL;
        NEW.thumbnail_started_at=NULL;
    ELSIF NEW.kind='file' AND NEW.thumbnail_state IS NULL THEN
        NEW.thumbnail_state='pending';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER elements_thumbnail_prepare BEFORE INSERT OR UPDATE OF object_key, thumbnail_state ON elements
    FOR EACH ROW EXECUTE FUNCTION prepare_thumbnail();

CREATE FUNCTION enqueue_deleted_thumbnail() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.thumbnail_key IS NOT NULL THEN
        INSERT INTO object_deletions (object_key) VALUES (OLD.thumbnail_key) ON CONFLICT DO NOTHING;
    END IF;
    RETURN OLD;
END;
$$;
CREATE TRIGGER elements_thumbnail_deletion AFTER DELETE ON elements FOR EACH ROW EXECUTE FUNCTION enqueue_deleted_thumbnail();
