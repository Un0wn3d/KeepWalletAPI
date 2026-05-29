CREATE TYPE user_role AS ENUM ('admin', 'user');
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

CREATE TABLE users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    role user_role NOT NULL DEFAULT 'user',
    username VARCHAR(45) NOT NULL UNIQUE,
    full_name VARCHAR(45),
    email VARCHAR(45) NOT NULL UNIQUE,
    password VARCHAR(255) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TYPE user_group_role AS ENUM ('owner', 'member', 'viewer');
CREATE TABLE groups (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    icon_key VARCHAR(50),
    name VARCHAR(100) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE group_members (
    group_id UUID NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role user_group_role NOT NULL DEFAULT 'owner',
    joined_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (group_id, user_id)
);
CREATE INDEX idx_group_members_user_id ON group_members(user_id);

CREATE TABLE accounts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    group_id UUID REFERENCES groups(id) ON DELETE SET NULL,
    name VARCHAR(100) NOT NULL,
    currency VARCHAR(3) NOT NULL DEFAULT 'UAH',
    balance NUMERIC(15,2) DEFAULT 0.00,
    is_default BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_accounts_user_id ON accounts(user_id);

CREATE TYPE category_type AS ENUM ('income', 'expense');
CREATE TABLE categories (
    id SERIAL PRIMARY KEY,
    icon_key VARCHAR(50) DEFAULT 'other',
    color VARCHAR(10),
    name VARCHAR(100) NOT NULL,
    type category_type NOT NULL
);

INSERT INTO categories (name, type, icon_key) VALUES
    ('Food', 'expense', 'food'),
    ('Transport', 'expense', 'car'),
    ('Utilities', 'expense', 'house'),
    ('Entertainment', 'expense', 'gamepad'),
    ('Health', 'expense', 'health'),
    ('Clothing', 'expense', 'shopping'),
    ('Education', 'expense', 'book'),
    ('Salary', 'income', 'income'),
    ('Freelance', 'income', 'income'),
    ('Other income', 'income', 'income'),
    ('Other expenses', 'expense', 'other');

CREATE TABLE user_category_preferences (
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    category_id INTEGER NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, category_id)
);
CREATE INDEX idx_user_category_preferences_user_id ON user_category_preferences(user_id);

CREATE TABLE budgets (
    id SERIAL PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    group_id UUID REFERENCES groups(id) ON DELETE SET NULL,
    category_id INTEGER NOT NULL REFERENCES categories(id),
    amount NUMERIC(15,2) CHECK (amount >= 0),
    budget_period INTERVAL,
    start_date DATE NOT NULL DEFAULT CURRENT_DATE,
    is_active BOOLEAN DEFAULT TRUE
);
CREATE INDEX idx_budgets_user_id ON budgets(user_id);

CREATE TABLE recurring_payments (
    id SERIAL PRIMARY KEY,
    name VARCHAR(200) NOT NULL,
    repeat_interval INTERVAL NOT NULL,
    next_due_date DATE NOT NULL,
    is_active BOOLEAN DEFAULT TRUE
);

CREATE TABLE savings (
    id SERIAL PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    group_id UUID REFERENCES groups(id) ON DELETE SET NULL,
    name VARCHAR(200) NOT NULL,
    currency VARCHAR(3) NOT NULL DEFAULT 'UAH',
    icon_key VARCHAR(50),
    color VARCHAR(10),
    target_amount NUMERIC(15,2) CHECK (target_amount > 0),
    current_amount NUMERIC(15,2) DEFAULT 0.00 CHECK (current_amount >= 0),
    deadline DATE,
    is_completed BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX idx_savings_user_id ON savings(user_id);
CREATE INDEX idx_savings_user_completed ON savings(user_id, is_completed);

CREATE TABLE transactions (
    id SERIAL PRIMARY KEY,
    account_id UUID NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    group_id UUID REFERENCES groups(id) ON DELETE SET NULL,
    category_id INTEGER NOT NULL REFERENCES categories(id),
    saving_id INTEGER REFERENCES savings(id) ON DELETE SET NULL,
    recurring_payments_id INTEGER REFERENCES recurring_payments(id) ON DELETE SET NULL,
    amount NUMERIC(15,2) CHECK (amount > 0),
    description VARCHAR(500),
    transaction_date TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_transactions_account_date ON transactions(account_id, transaction_date DESC);
CREATE INDEX idx_transactions_account_type ON transactions(account_id, category_id);
CREATE INDEX idx_transactions_saving_id ON transactions(saving_id);

CREATE TABLE wish_list (
    id SERIAL PRIMARY KEY,
    saving_id INTEGER NOT NULL REFERENCES savings(id) ON DELETE CASCADE,
    name VARCHAR(255) NOT NULL,
    price NUMERIC(15,2) CHECK (price >= 0),
    priority SMALLINT,
    is_purchased BOOLEAN DEFAULT FALSE
);
CREATE INDEX idx_wish_list_savings ON wish_list(saving_id);

CREATE TABLE logs (
    id BIGSERIAL PRIMARY KEY,
    user_id UUID REFERENCES users(id) ON DELETE SET NULL,
    action VARCHAR(100) NOT NULL,
    entity_type VARCHAR(100),
    details JSONB,
    device VARCHAR(45),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_logs_user_id ON logs(user_id);
CREATE INDEX idx_logs_created_at ON logs(created_at);
CREATE INDEX idx_logs_action ON logs(action);

CREATE TABLE refresh_tokens (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash VARCHAR(128) NOT NULL UNIQUE,
    jwt_id UUID NOT NULL UNIQUE,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ NULL,
    replaced_by_token_id UUID NULL REFERENCES refresh_tokens(id) ON DELETE SET NULL,
    created_by_ip VARCHAR(64) NULL,
    revoked_by_ip VARCHAR(64) NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_refresh_tokens_user_id ON refresh_tokens(user_id);
CREATE INDEX idx_refresh_tokens_expires_at ON refresh_tokens(expires_at);
CREATE INDEX idx_refresh_tokens_jwt_id ON refresh_tokens(jwt_id);

DROP TRIGGER IF EXISTS trg_update_balance_after_insert ON transactions;
DROP TRIGGER IF EXISTS trg_update_balance_after_change ON transactions;
DROP FUNCTION IF EXISTS fn_update_account_balance();
DROP FUNCTION IF EXISTS fn_apply_transaction_balance(UUID, INT, NUMERIC, INT);

CREATE OR REPLACE FUNCTION fn_apply_transaction_balance(
    p_account_id UUID,
    p_category_id INT,
    p_amount NUMERIC,
    p_multiplier INT
)
RETURNS VOID AS $fn$
DECLARE
    v_category_type category_type;
    v_delta NUMERIC(15,2);
BEGIN
    SELECT c.type
    INTO v_category_type
    FROM categories c
    WHERE c.id = p_category_id;

    IF v_category_type IS NULL THEN
        RAISE EXCEPTION 'Category % does not exist', p_category_id;
    END IF;

    v_delta := CASE
        WHEN v_category_type = 'income' THEN p_amount
        ELSE -p_amount
    END;

    UPDATE accounts
    SET balance = balance + (v_delta * p_multiplier),
        updated_at = NOW()
    WHERE id = p_account_id;
END;
$fn$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION fn_update_account_balance()
RETURNS TRIGGER AS $trg$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.recurring_payments_id IS NULL THEN
            PERFORM fn_apply_transaction_balance(NEW.account_id, NEW.category_id, NEW.amount, 1);
        END IF;
        RETURN NEW;
    ELSIF TG_OP = 'UPDATE' THEN
        IF OLD.recurring_payments_id IS NULL THEN
            PERFORM fn_apply_transaction_balance(OLD.account_id, OLD.category_id, OLD.amount, -1);
        END IF;

        IF NEW.recurring_payments_id IS NULL THEN
            PERFORM fn_apply_transaction_balance(NEW.account_id, NEW.category_id, NEW.amount, 1);
        END IF;
        RETURN NEW;
    ELSIF TG_OP = 'DELETE' THEN
        IF OLD.recurring_payments_id IS NULL THEN
            PERFORM fn_apply_transaction_balance(OLD.account_id, OLD.category_id, OLD.amount, -1);
        END IF;
        RETURN OLD;
    END IF;

    RETURN NULL;
END;
$trg$ LANGUAGE plpgsql;

CREATE TRIGGER trg_update_balance_after_change
    AFTER INSERT OR UPDATE OR DELETE ON transactions
    FOR EACH ROW
    EXECUTE FUNCTION fn_update_account_balance();

DROP TRIGGER IF EXISTS trg_accounts_updated_at ON accounts;
DROP TRIGGER IF EXISTS trg_refresh_tokens_updated_at ON refresh_tokens;
DROP FUNCTION IF EXISTS fn_set_updated_at();

CREATE OR REPLACE FUNCTION fn_set_updated_at()
RETURNS TRIGGER AS $updated_at$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$updated_at$ LANGUAGE plpgsql;

CREATE TRIGGER trg_accounts_updated_at
    BEFORE UPDATE ON accounts
    FOR EACH ROW
    EXECUTE FUNCTION fn_set_updated_at();

CREATE TRIGGER trg_refresh_tokens_updated_at
    BEFORE UPDATE ON refresh_tokens
    FOR EACH ROW
    EXECUTE FUNCTION fn_set_updated_at();

CREATE OR REPLACE VIEW popular_categories_last_30_days AS
SELECT
    a.user_id,
    c.id AS category_id,
    c.name AS category_name,
    c.type AS category_type,
    COUNT(t.id) AS transactions_count,
    SUM(t.amount) AS total_amount
FROM transactions t
JOIN accounts a ON a.id = t.account_id
JOIN categories c ON c.id = t.category_id
WHERE t.recurring_payments_id IS NULL
  AND t.transaction_date >= (CURRENT_DATE - INTERVAL '30 days')
  AND t.transaction_date < (CURRENT_DATE + INTERVAL '1 day')
GROUP BY a.user_id, c.id, c.type
ORDER BY a.user_id, transactions_count DESC, total_amount DESC;
