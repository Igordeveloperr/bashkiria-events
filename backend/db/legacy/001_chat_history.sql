CREATE TABLE chat_sessions (
    id uuid PRIMARY KEY,
    user_id text NOT NULL CHECK (length(btrim(user_id)) > 0),
    session_key text NOT NULL CHECK (length(btrim(session_key)) > 0),
    title text,
    next_seq bigint NOT NULL DEFAULT 1 CHECK (next_seq > 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (user_id, session_key)
);

CREATE INDEX chat_sessions_user_updated_idx
    ON chat_sessions (user_id, updated_at DESC, id);

CREATE TABLE chat_runs (
    id uuid PRIMARY KEY,
    session_id uuid NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
    request_id uuid NOT NULL,
    status text NOT NULL CHECK (status IN ('running', 'completed', 'failed')),
    trace_id text,
    error text,
    created_at timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz,
    UNIQUE (session_id, request_id),
    UNIQUE (id, session_id),
    CHECK ((status = 'running') = (finished_at IS NULL)),
    CHECK ((status = 'failed') = (error IS NOT NULL))
);

CREATE UNIQUE INDEX chat_runs_one_active_idx
    ON chat_runs (session_id) WHERE status = 'running';

CREATE INDEX chat_runs_session_idx ON chat_runs (session_id);

CREATE TABLE chat_messages (
    id uuid PRIMARY KEY,
    session_id uuid NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
    run_id uuid NOT NULL,
    seq bigint NOT NULL CHECK (seq > 0),
    role text NOT NULL CHECK (role IN ('user', 'assistant')),
    content text NOT NULL,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (run_id, session_id)
        REFERENCES chat_runs(id, session_id) ON DELETE CASCADE,
    UNIQUE (session_id, seq),
    UNIQUE (run_id, role)
);

CREATE INDEX chat_messages_user_seq_idx
    ON chat_messages (session_id, seq DESC) WHERE role = 'user';
