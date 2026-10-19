CREATE TABLE accounts (
    id uuid PRIMARY KEY,
    username text NOT NULL CHECK (length(username) BETWEEN 1 AND 100 AND position('@' in username) = 0),
    email text NOT NULL CHECK (length(email) BETWEEN 3 AND 320),
    password_hash text NOT NULL,
    is_admin boolean NOT NULL DEFAULT false,
    is_active boolean NOT NULL DEFAULT true
);
CREATE UNIQUE INDEX accounts_username_unique ON accounts (upper(username));
CREATE UNIQUE INDEX accounts_email_unique ON accounts (upper(email));
CREATE TABLE invitations (
    id uuid PRIMARY KEY,
    email text NOT NULL,
    token_hash text NOT NULL UNIQUE,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    revoked boolean NOT NULL DEFAULT false,
    consumed boolean NOT NULL DEFAULT false,
    account_id uuid REFERENCES accounts(id) ON DELETE CASCADE
);
CREATE TABLE sessions (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    token_hash text NOT NULL UNIQUE,
    device_name text NOT NULL,
    created_at timestamptz NOT NULL,
    last_seen_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL
);
CREATE INDEX sessions_account ON sessions(account_id);
CREATE TABLE password_resets (
    account_id uuid PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
    token_hash text NOT NULL UNIQUE,
    expires_at timestamptz NOT NULL
);
