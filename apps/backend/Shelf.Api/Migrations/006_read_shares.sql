CREATE TABLE shares (
    element_id uuid NOT NULL REFERENCES elements(id) ON DELETE CASCADE,
    recipient_id uuid NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    level text NOT NULL CHECK (level IN ('read')),
    PRIMARY KEY (element_id, recipient_id)
);
CREATE INDEX shares_recipient ON shares (recipient_id);
