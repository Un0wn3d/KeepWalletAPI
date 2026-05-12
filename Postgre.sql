-- 1. КОРИСТУВАЧІ ТА РОЛІ
CREATE TYPE user_role AS ENUM ('admin', 'user');
CREATE EXTENSION IF NOT EXISTS "pgcrypto";  -- для gen_random_uuid()

CREATE TABLE users (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    role          user_role NOT NULL DEFAULT 'user',
    username      VARCHAR(45) NOT NULL UNIQUE,
    full_name     VARCHAR(45),
    email         VARCHAR(45) NOT NULL UNIQUE,
    password      VARCHAR(255) NOT NULL,
    is_active     BOOLEAN NOT NULL DEFAULT TRUE,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 2. ГРУПИ ТА УЧАСНИКИ
CREATE TYPE user_group_role AS ENUM ('owner', 'member', 'viewer');
CREATE TABLE groups (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name          VARCHAR(100) NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE group_members (
    group_id    UUID NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role        user_group_role NOT NULL DEFAULT 'owner', -- 'owner', 'member', 'viewer'
    joined_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (group_id, user_id)
);
CREATE INDEX idx_group_members_user_id ON group_members(user_id);

-- Migration for databases created before group memberships supported multiple users.
ALTER TABLE group_members DROP CONSTRAINT IF EXISTS group_members_pkey;
ALTER TABLE group_members ADD PRIMARY KEY (group_id, user_id);
CREATE INDEX IF NOT EXISTS idx_group_members_user_id ON group_members(user_id);

-- 3. РАХУНКИ КОРИСТУВАЧА АБО ГРУПИ
CREATE TABLE accounts (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id      UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    group_id     UUID  REFERENCES groups(id) ON DELETE SET NULL,
    name         VARCHAR(100) NOT NULL, -- 'Основний', 'Готівка', 'Карта Mono'
    currency     VARCHAR(3) NOT NULL DEFAULT 'UAH', -- ISO 4217
    balance      NUMERIC(15,2) DEFAULT 0.00,
    is_default   BOOLEAN DEFAULT FALSE,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_accounts_user_id ON accounts(user_id);

-- 4. КАТЕГОРІЇ ТРАНЗАКЦІЙ ТА БЮДЖЕТИ
CREATE TYPE category_type AS ENUM ('income', 'expense');
CREATE TABLE categories (
    id          SERIAL PRIMARY KEY,
    icon_key    VARCHAR(50),
    name        VARCHAR(100) NOT NULL,
    type        category_type NOT NULL
);

-- Системні категорії (загальні для всіх)
INSERT INTO categories (name, type) VALUES
    ('Їжа',                   'expense'),
    ('Транспорт',             'expense'),
    ('Комунальні послуги',    'expense'),
    ('Розваги',               'expense'),
    ('Здоровя',               'expense'),
    ('Одяг',                  'expense'),
    ('Освіта',                'expense'),
    ('Зарплата',              'income'),
    ('Фріланс',               'income'),
    ('Інші доходи',           'income'),
    ('Інші витрати',          'expense');

CREATE TABLE user_category_preferences (
    user_id UUID PRIMARY KEY REFERENCES users(id),
    category_id INT REFERENCES categories(id)
);

CREATE TABLE budgets (
    id            SERIAL PRIMARY KEY,
    user_id       UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    group_id      UUID REFERENCES groups(id) ON DELETE SET NULL,
    category_id   INT NOT NULL REFERENCES categories(id),
    amount        NUMERIC(15,2) CHECK (amount >= 0),
    budget_period INTERVAL, -- '1 month'
    start_date    DATE NOT NULL DEFAULT CURRENT_DATE,
    is_active     BOOLEAN DEFAULT TRUE
);
CREATE INDEX idx_budgets_user_id     ON budgets(user_id);

-- 5. ТРАНЗАКЦІЇ ТА РЕГУЛЯРНІ / ЗАПЛАНОВАНІ ПЛАТЕЖІ
CREATE TABLE recurring_payments (
    id              SERIAL PRIMARY KEY,
    name            VARCHAR(200) NOT NULL,
    repeat_interval INTERVAL NOT NULL,
    next_due_date   DATE NOT NULL,
    is_active       BOOLEAN DEFAULT TRUE
);

CREATE TABLE transactions (
    id               SERIAL PRIMARY KEY,
    account_id       UUID NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    group_id         UUID REFERENCES groups(id) ON DELETE SET NULL,
    category_id      INT NOT NULL REFERENCES categories(id),
    recurring_payments_id INT REFERENCES recurring_payments(id) ON DELETE SET NULL,
    amount           NUMERIC(15,2) CHECK (amount > 0),
    description      VARCHAR(500),
    transaction_date TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_transactions_account_date ON transactions(account_id, transaction_date DESC);
CREATE INDEX idx_transactions_account_type ON transactions(account_id, category_id);

-- 6. СКАРБНИЧКИ, ВІДСОТКИ, СПИСОК БАЖАНОГО
CREATE TABLE savings (
    id             SERIAL PRIMARY KEY,
    user_id        UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    group_id       UUID REFERENCES groups(id) ON DELETE SET NULL,
    name           VARCHAR(200) NOT NULL,
    target_amount  NUMERIC(15,2) CHECK (target_amount > 0),
    current_amount NUMERIC(15,2) DEFAULT 0.00 CHECK (current_amount >= 0),
    deadline       DATE,
    is_completed   BOOLEAN DEFAULT FALSE,
    created_at     TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX idx_savings_user_id ON savings(user_id);
CREATE INDEX idx_savings_user_completed ON savings(user_id, is_completed);

CREATE TABLE wish_list (
    id             SERIAL PRIMARY KEY,
    saving_id      INT NOT NULL REFERENCES savings(id) ON DELETE CASCADE,
    name           VARCHAR(255) NOT NULL,
    price          NUMERIC(15,2) CHECK (price >= 0),
    priority       SMALLINT,
    is_purchased   BOOLEAN DEFAULT FALSE
);
CREATE INDEX idx_wish_list_savings ON wish_list(saving_id);

-- 7. ЛОГУВАННЯ ДІЙ КОРИСТУВАЧА (AUDIT TRAIL)
CREATE TABLE logs (
    id          BIGSERIAL PRIMARY KEY,
    user_id     UUID REFERENCES users(id) ON DELETE SET NULL,
    action      VARCHAR(100) NOT NULL,   -- 'LOGIN', 'CREATE_TRANSACTION', 'DELETE_SAVING', ...
    entity_type VARCHAR(100),            -- 'transaction', 'saving', 'piggy_bank', ...
    details     JSONB,                    -- JSON-рядок із деталями дії
    device      VARCHAR(45),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_logs_user_id    ON logs(user_id);
CREATE INDEX idx_logs_created_at ON logs(created_at);
CREATE INDEX idx_logs_action     ON logs(action);

-- 8. AUTH / DEVICE SESSIONS
CREATE TABLE refresh_tokens (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id              UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash           VARCHAR(128) NOT NULL UNIQUE,
    jwt_id               UUID NOT NULL UNIQUE,
    expires_at           TIMESTAMPTZ NOT NULL,
    revoked_at           TIMESTAMPTZ NULL,
    replaced_by_token_id UUID NULL REFERENCES refresh_tokens(id) ON DELETE SET NULL,
    created_by_ip        VARCHAR(64) NULL,
    revoked_by_ip        VARCHAR(64) NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_refresh_tokens_user_id ON refresh_tokens(user_id);
CREATE INDEX idx_refresh_tokens_expires_at ON refresh_tokens(expires_at);
CREATE INDEX idx_refresh_tokens_jwt_id ON refresh_tokens(jwt_id);

-- -- Поточний баланс рахунку (зручний для читання)
-- CREATE VIEW v_account_balances AS
-- SELECT
--     a.id          AS account_id,
--     a.user_id,
--     u.username,
--     a.name        AS account_name,
--     a.currency,
--     a.balance,
--     a.is_default,
--     a.updated_at
-- FROM accounts a
-- JOIN users u ON u.id = a.user_id
-- WHERE u.is_active = TRUE;

-- -- Витрати за категоріями за поточний місяць
-- CREATE VIEW v_monthly_expenses AS
-- SELECT
--     t.user_id,
--     c.name        AS category_name,
--     c.color,
--     c.icon,
--     SUM(t.amount) AS total_amount,
--     t.currency,
--     COUNT(*)      AS transaction_count,
--     DATE_TRUNC('month', NOW())::DATE AS month
-- FROM transactions t
-- JOIN categories c ON c.id = t.category_id
-- WHERE t.type = 'expense'
--   AND DATE_TRUNC('month', t.transaction_date) = DATE_TRUNC('month', CURRENT_DATE)
-- GROUP BY t.user_id, c.name, c.color, c.icon, t.currency;

-- -- Прогрес скарбничок
-- CREATE VIEW v_piggy_bank_progress AS
-- SELECT
--     pb.id,
--     pb.user_id,
--     pb.name,
--     pb.current_amount,
--     pb.target_amount,
--     pb.currency,
--     pb.deadline,
--     CASE
--         WHEN pb.target_amount > 0
--         THEN ROUND((pb.current_amount / pb.target_amount) * 100, 2)
--         ELSE NULL
--     END AS progress_pct,
--     (
--         SELECT COALESCE(SUM(price), 0)
--         FROM wish_list_items w
--         WHERE w.piggy_bank_id = pb.id
--           AND w.is_purchased = FALSE
--     ) AS wishlist_remaining
-- FROM piggy_banks pb
-- WHERE pb.is_completed = FALSE;

-- -- ============================================================
-- -- 13. ФУНКЦІЇ / ТРИГЕРИ (PostgreSQL)
-- -- ============================================================

-- -- Оновлення балансу рахунку після транзакції
-- Account balance update after transaction insert/update/delete.
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

-- Automatic updated_at update for tables that actually have this column.
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

-- ============================================================
-- ПРИМІТКИ ДЛЯ SQLite (мобільний клієнт)
-- ============================================================
-- 1. SERIAL -> INTEGER PRIMARY KEY AUTOINCREMENT
-- 2. TIMESTAMPTZ -> TEXT (ISO8601: "YYYY-MM-DD HH:MM:SS")
-- 3. BOOLEAN -> INTEGER (0/1)
-- 4. NUMERIC(x,y) -> REAL або TEXT
-- 5. CHAR(3) -> TEXT
-- 6. Тригери підтримуються SQLite, але функції plpgsql — ні.
--    Баланс рахунку можна оновлювати через тригер або з коду застосунку.
-- 7. Типи CHECK підтримуються в SQLite 3.25+
-- 8. CREATE VIEW підтримується
-- ============================================================

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