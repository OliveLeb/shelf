ALTER TABLE shares DROP CONSTRAINT shares_level_check;
ALTER TABLE shares ADD CONSTRAINT shares_level_check CHECK (level IN ('read', 'write'));

-- A targeted replacement never falls back to creating a file if its target disappears.
ALTER TABLE uploads ADD COLUMN replace_file_id uuid REFERENCES elements(id) ON DELETE CASCADE;
