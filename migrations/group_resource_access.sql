CREATE TABLE IF NOT EXISTS group_resource_access (
    group_id       UUID NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    account_id     UUID REFERENCES accounts(id) ON DELETE CASCADE,
    saving_id      INT REFERENCES savings(id) ON DELETE CASCADE,
    transaction_id INT REFERENCES transactions(id) ON DELETE CASCADE,
    shared_by      UUID REFERENCES users(id) ON DELETE SET NULL,
    CHECK (
        (account_id IS NOT NULL)::int +
        (saving_id IS NOT NULL)::int +
        (transaction_id IS NOT NULL)::int = 1
    ),
    UNIQUE (group_id, account_id),
    UNIQUE (group_id, saving_id),
    UNIQUE (group_id, transaction_id)
);

CREATE INDEX IF NOT EXISTS idx_group_resource_access_group_id
    ON group_resource_access(group_id);

CREATE INDEX IF NOT EXISTS idx_group_resource_access_account_id
    ON group_resource_access(account_id);

CREATE INDEX IF NOT EXISTS idx_group_resource_access_saving_id
    ON group_resource_access(saving_id);

CREATE INDEX IF NOT EXISTS idx_group_resource_access_transaction_id
    ON group_resource_access(transaction_id);

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'accounts'
          AND column_name = 'group_id'
    ) THEN
        INSERT INTO group_resource_access (group_id, account_id, shared_by)
        SELECT group_id, id, user_id
        FROM accounts
        WHERE group_id IS NOT NULL
        ON CONFLICT (group_id, account_id) DO NOTHING;

        ALTER TABLE accounts DROP COLUMN group_id;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'savings'
          AND column_name = 'group_id'
    ) THEN
        INSERT INTO group_resource_access (group_id, saving_id, shared_by)
        SELECT group_id, id, user_id
        FROM savings
        WHERE group_id IS NOT NULL
        ON CONFLICT (group_id, saving_id) DO NOTHING;

        ALTER TABLE savings DROP COLUMN group_id;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'transactions'
          AND column_name = 'group_id'
    ) THEN
        INSERT INTO group_resource_access (group_id, transaction_id, shared_by)
        SELECT t.group_id, t.id, a.user_id
        FROM transactions t
        JOIN accounts a ON a.id = t.account_id
        WHERE t.group_id IS NOT NULL
        ON CONFLICT (group_id, transaction_id) DO NOTHING;

        ALTER TABLE transactions DROP COLUMN group_id;
    END IF;
END $$;
