-- This is a design aid, not the final migration source of truth.
-- Claude Code should implement final schema using EF Core migrations.

CREATE TABLE public_id_sequences (
    prefix varchar(16) NOT NULL,
    year integer NOT NULL,
    last_value integer NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    PRIMARY KEY (prefix, year)
);

CREATE TABLE outbox_messages (
    id uuid PRIMARY KEY,
    event_type varchar(160) NOT NULL,
    payload_json jsonb NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    available_at_utc timestamptz NOT NULL,
    attempts integer NOT NULL DEFAULT 0,
    claimed_at_utc timestamptz NULL,
    processed_at_utc timestamptz NULL,
    last_error text NULL
);

CREATE INDEX ix_outbox_pending
ON outbox_messages (available_at_utc)
WHERE processed_at_utc IS NULL;

-- Sales conceptual uniqueness must account for state, business year, sale type,
-- campaign, and explicit resale override workflow. Implement using application
-- rule + supporting partial indexes where possible; do not encode a simplistic
-- constraint that makes the approved resale confirmation impossible.
