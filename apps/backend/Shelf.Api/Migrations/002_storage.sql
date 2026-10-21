CREATE TABLE spaces (
    id uuid PRIMARY KEY,
    owner_id uuid NOT NULL UNIQUE REFERENCES accounts(id) ON DELETE CASCADE
);
INSERT INTO spaces (id, owner_id) SELECT gen_random_uuid(), id FROM accounts;
CREATE TABLE elements (
    id uuid PRIMARY KEY,
    space_id uuid NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 255),
    size bigint NOT NULL CHECK (size >= 0),
    content_type text NOT NULL,
    object_key text NOT NULL UNIQUE,
    added_at timestamptz NOT NULL,
    added_by uuid REFERENCES accounts(id) ON DELETE SET NULL
);
CREATE UNIQUE INDEX elements_name_unique ON elements (space_id, upper(name));
CREATE TABLE uploads (
    id uuid PRIMARY KEY,
    space_id uuid NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
    account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    name text NOT NULL,
    size bigint NOT NULL,
    content_type text NOT NULL,
    object_key text NOT NULL UNIQUE,
    created_at timestamptz NOT NULL
);
