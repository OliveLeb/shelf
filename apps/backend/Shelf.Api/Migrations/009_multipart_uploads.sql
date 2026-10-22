-- Pending uploads outlive their account and destination, so the cleanup task can still purge their objects.
ALTER TABLE uploads
    ALTER COLUMN space_id DROP NOT NULL,
    ALTER COLUMN account_id DROP NOT NULL,
    DROP CONSTRAINT uploads_space_id_fkey,
    DROP CONSTRAINT uploads_account_id_fkey,
    DROP CONSTRAINT uploads_parent,
    -- Preserve the target ID after deletion so replacement cannot fall back to creating a file.
    DROP CONSTRAINT uploads_replace_file_id_fkey,
    -- Keep the upload row when its folder disappears, for cleanup after signed URLs expire.
    ADD CONSTRAINT uploads_parent FOREIGN KEY (parent_id, space_id) REFERENCES elements (id, space_id) ON DELETE SET NULL (space_id) DEFERRABLE INITIALLY DEFERRED,
    ADD FOREIGN KEY (space_id) REFERENCES spaces(id) ON DELETE SET NULL,
    ADD FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE SET NULL,
    -- S3 multipart upload ID; null for an upload sent with a single PUT.
    ADD COLUMN multipart_id text,
    ADD COLUMN part_size bigint;
UPDATE uploads SET part_size = size;
ALTER TABLE uploads ALTER COLUMN part_size SET NOT NULL;
