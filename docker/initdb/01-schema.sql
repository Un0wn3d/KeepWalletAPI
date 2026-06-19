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
    icon_key      VARCHAR(50),
    color         VARCHAR(10),
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

-- 3. РАХУНКИ КОРИСТУВАЧА АБО ГРУПИ
CREATE TABLE accounts (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id      UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name         VARCHAR(100) NOT NULL, -- 'Основний', 'Готівка', 'Карта Mono'
    currency     VARCHAR(3) NOT NULL DEFAULT 'UAH', -- ISO 4217
    balance      NUMERIC(15,2) DEFAULT 0.00,
    is_default   BOOLEAN DEFAULT FALSE,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_accounts_user_id ON accounts(user_id);

-- 4. КАТЕГОРІЇ ТРАНЗАКЦІЙ ТА БЮДЖЕТИ
CREATE TYPE category_type AS ENUM ('income', 'expense');
CREATE TABLE categories (
    id          SERIAL PRIMARY KEY,
    name        VARCHAR(100) NOT NULL,
    type        category_type NOT NULL
);

-- Системні категорії (загальні для всіх)
INSERT INTO categories (name, type) VALUES
    ('Покупки',                'expense'),
    ('Накопичення',            'expense'),
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
    user_id UUID REFERENCES users(id),
    category_id INT REFERENCES categories(id),
    icon_key VARCHAR(50),
    color VARCHAR(10),
    is_active BOOLEAN DEFAULT TRUE
);

CREATE TABLE budgets (
    id            SERIAL PRIMARY KEY,
    account_id    UUID NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    group_id      UUID REFERENCES groups(id) ON DELETE SET NULL,
    category_id   INT NOT NULL REFERENCES categories(id),
    amount        NUMERIC(15,2) CHECK (amount >= 0)
);
CREATE INDEX idx_budgets_user_id     ON budgets(account_id);

-- 5. ТРАНЗАКЦІЇ ТА РЕГУЛЯРНІ / ЗАПЛАНОВАНІ ПЛАТЕЖІ
CREATE TABLE recurring_payments (
    id              SERIAL PRIMARY KEY,
    name            VARCHAR(200) NOT NULL,
    repeat_interval INTERVAL NOT NULL,
    next_due_date   TIMESTAMPTZ NOT NULL,
    is_active       BOOLEAN DEFAULT TRUE
);

CREATE TABLE transactions (
    id               SERIAL PRIMARY KEY,
    account_id       UUID NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    category_id      INT NOT NULL REFERENCES categories(id),
    saving_id        INT,
    recurring_payments_id INT REFERENCES recurring_payments(id) ON DELETE SET NULL,
    amount           NUMERIC(15,2) CHECK (amount >= 0),
    name             VARCHAR(500),
    transaction_date TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_transactions_account_date ON transactions(account_id, transaction_date DESC);
CREATE INDEX idx_transactions_account_type ON transactions(account_id, category_id);
CREATE INDEX idx_transactions_saving_id ON transactions(saving_id);

-- 6. СКАРБНИЧКИ, ВІДСОТКИ, СПИСОК БАЖАНОГО
CREATE TABLE savings (
    id             SERIAL PRIMARY KEY,
    user_id        UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name           VARCHAR(200) NOT NULL,
    currency       VARCHAR(3) NOT NULL DEFAULT 'UAH',
    icon_key       VARCHAR(50),
    color          VARCHAR(10),
    target_amount  NUMERIC(15,2) CHECK (target_amount > 0),
    current_amount NUMERIC(15,2) DEFAULT 0.00 CHECK (current_amount >= 0),
    deadline       DATE,
    is_completed   BOOLEAN DEFAULT FALSE
);
CREATE INDEX idx_savings_user_id ON savings(user_id);
CREATE INDEX idx_savings_user_completed ON savings(user_id, is_completed);

ALTER TABLE transactions
    ADD CONSTRAINT transactions_saving_id_fkey
    FOREIGN KEY (saving_id) REFERENCES savings(id) ON DELETE SET NULL;

CREATE TABLE group_resource_access (
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

CREATE INDEX idx_group_resource_access_group_id
    ON group_resource_access(group_id);

CREATE INDEX idx_group_resource_access_account_id
    ON group_resource_access(account_id);

CREATE INDEX idx_group_resource_access_saving_id
    ON group_resource_access(saving_id);

CREATE INDEX idx_group_resource_access_transaction_id
    ON group_resource_access(transaction_id);

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
    details     JSONB,                    -- JSON-рядок із деталями дії
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_logs_user_id    ON logs(user_id);
CREATE INDEX idx_logs_created_at ON logs(created_at);
CREATE INDEX idx_logs_action     ON logs(action);
CREATE INDEX idx_logs_details_gin ON logs USING GIN(details);

-- 8. AUTH / DEVICE SESSIONS
CREATE TABLE refresh_tokens (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id              UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash           VARCHAR(128) NOT NULL UNIQUE,
    expires_at           TIMESTAMPTZ NOT NULL,
    created_by_ip        VARCHAR(64) NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_refresh_tokens_user_id ON refresh_tokens(user_id);
CREATE INDEX idx_refresh_tokens_expires_at ON refresh_tokens(expires_at);

CREATE OR REPLACE FUNCTION fn_audit_current_user_id(p_fallback UUID DEFAULT NULL)
RETURNS UUID AS $fn$
DECLARE
    v_user_id_text TEXT;
BEGIN
    v_user_id_text := NULLIF(current_setting('app.current_user_id', TRUE), '');
    IF v_user_id_text IS NULL THEN
        RETURN p_fallback;
    END IF;

    RETURN v_user_id_text::UUID;
EXCEPTION WHEN OTHERS THEN
    RETURN p_fallback;
END;
$fn$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION fn_audit_device()
RETURNS TEXT AS $fn$
BEGIN
    RETURN NULLIF(current_setting('app.device', TRUE), '');
EXCEPTION WHEN OTHERS THEN
    RETURN NULL;
END;
$fn$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION fn_audit_user_label(p_user_id UUID)
RETURNS TEXT AS $fn$
DECLARE
    v_label TEXT;
BEGIN
    IF p_user_id IS NULL THEN
        RETURN NULL;
    END IF;

    SELECT COALESCE(NULLIF(u.username, ''), NULLIF(u.full_name, ''), NULLIF(u.email, ''), u.id::TEXT)
    INTO v_label
    FROM users u
    WHERE u.id = p_user_id;

    RETURN COALESCE(v_label, p_user_id::TEXT);
END;
$fn$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION fn_enrich_log_data(p_table_name TEXT, p_row JSONB)
RETURNS JSONB AS $fn$
DECLARE
    v_result JSONB := p_row;
    v_user_id UUID;
    v_group_id UUID;
    v_account_id UUID;
    v_shared_by UUID;
    v_category_id INT;
    v_saving_id INT;
    v_transaction_id INT;
    v_recurring_payment_id INT;
    v_account_name TEXT;
    v_category_name TEXT;
    v_group_name TEXT;
    v_saving_name TEXT;
    v_transaction_name TEXT;
    v_recurring_payment_name TEXT;
BEGIN
    IF p_row IS NULL THEN
        RETURN NULL;
    END IF;

    IF p_table_name = 'users' THEN
        v_result := v_result - 'password';
    ELSIF p_table_name = 'refresh_tokens' THEN
        v_result := v_result - 'token_hash';
    END IF;

    IF p_row ? 'user_id' THEN
        v_user_id := NULLIF(p_row->>'user_id', '')::UUID;
        v_result := v_result || jsonb_build_object('username', fn_audit_user_label(v_user_id));
    ELSIF p_table_name = 'users' AND p_row ? 'id' THEN
        v_user_id := NULLIF(p_row->>'id', '')::UUID;
        v_result := v_result || jsonb_build_object('username', fn_audit_user_label(v_user_id));
    END IF;

    IF p_row ? 'account_id' THEN
        v_account_id := NULLIF(p_row->>'account_id', '')::UUID;
        SELECT a.name INTO v_account_name FROM accounts a WHERE a.id = v_account_id;
        v_result := v_result || jsonb_build_object('account_name', COALESCE(v_account_name, v_account_id::TEXT));
    ELSIF p_table_name = 'accounts' AND p_row ? 'id' THEN
        v_account_id := NULLIF(p_row->>'id', '')::UUID;
        v_result := v_result || jsonb_build_object('account_name', COALESCE(p_row->>'name', v_account_id::TEXT));
    END IF;

    IF p_row ? 'group_id' THEN
        v_group_id := NULLIF(p_row->>'group_id', '')::UUID;
        SELECT g.name INTO v_group_name FROM groups g WHERE g.id = v_group_id;
        v_result := v_result || jsonb_build_object('group_name', COALESCE(v_group_name, v_group_id::TEXT));
    ELSIF p_table_name = 'groups' AND p_row ? 'id' THEN
        v_group_id := NULLIF(p_row->>'id', '')::UUID;
        v_result := v_result || jsonb_build_object('group_name', COALESCE(p_row->>'name', v_group_id::TEXT));
    END IF;

    IF p_row ? 'category_id' THEN
        v_category_id := NULLIF(p_row->>'category_id', '')::INT;
        SELECT c.name INTO v_category_name FROM categories c WHERE c.id = v_category_id;
        v_result := v_result || jsonb_build_object('category_name', COALESCE(v_category_name, v_category_id::TEXT));
    ELSIF p_table_name = 'categories' AND p_row ? 'id' THEN
        v_category_id := NULLIF(p_row->>'id', '')::INT;
        v_result := v_result || jsonb_build_object('category_name', COALESCE(p_row->>'name', v_category_id::TEXT));
    END IF;

    IF p_row ? 'saving_id' THEN
        v_saving_id := NULLIF(p_row->>'saving_id', '')::INT;
        SELECT s.name INTO v_saving_name FROM savings s WHERE s.id = v_saving_id;
        v_result := v_result || jsonb_build_object('saving_name', COALESCE(v_saving_name, v_saving_id::TEXT));
    ELSIF p_table_name = 'savings' AND p_row ? 'id' THEN
        v_saving_id := NULLIF(p_row->>'id', '')::INT;
        v_result := v_result || jsonb_build_object('saving_name', COALESCE(p_row->>'name', v_saving_id::TEXT));
    END IF;

    IF p_row ? 'transaction_id' THEN
        v_transaction_id := NULLIF(p_row->>'transaction_id', '')::INT;
        SELECT COALESCE(NULLIF(t.name, ''), '#' || t.id::TEXT)
        INTO v_transaction_name
        FROM transactions t
        WHERE t.id = v_transaction_id;
        v_result := v_result || jsonb_build_object('transaction_name', COALESCE(v_transaction_name, v_transaction_id::TEXT));
    ELSIF p_table_name = 'transactions' AND p_row ? 'id' THEN
        v_transaction_id := NULLIF(p_row->>'id', '')::INT;
        v_result := v_result || jsonb_build_object('transaction_name', COALESCE(NULLIF(p_row->>'name', ''), '#' || v_transaction_id::TEXT));
    END IF;

    IF p_row ? 'recurring_payments_id' THEN
        v_recurring_payment_id := NULLIF(p_row->>'recurring_payments_id', '')::INT;
        SELECT rp.name INTO v_recurring_payment_name FROM recurring_payments rp WHERE rp.id = v_recurring_payment_id;
        v_result := v_result || jsonb_build_object('recurring_payment_name', COALESCE(v_recurring_payment_name, v_recurring_payment_id::TEXT));
    ELSIF p_table_name = 'recurring_payments' AND p_row ? 'id' THEN
        v_recurring_payment_id := NULLIF(p_row->>'id', '')::INT;
        v_result := v_result || jsonb_build_object('recurring_payment_name', COALESCE(p_row->>'name', v_recurring_payment_id::TEXT));
    END IF;

    IF p_row ? 'shared_by' THEN
        v_shared_by := NULLIF(p_row->>'shared_by', '')::UUID;
        v_result := v_result || jsonb_build_object('shared_by_username', fn_audit_user_label(v_shared_by));
    END IF;

    RETURN v_result;
EXCEPTION WHEN OTHERS THEN
    RETURN v_result;
END;
$fn$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION fn_log_changes()
RETURNS TRIGGER AS $log$
DECLARE
    v_user_id UUID;
    v_fallback_user_id UUID;
    v_device TEXT;
BEGIN
    IF TG_TABLE_NAME = 'users' THEN
        IF TG_OP = 'DELETE' THEN
            v_fallback_user_id := OLD.id;
        ELSE
            v_fallback_user_id := NEW.id;
        END IF;
    ELSIF TG_OP IN ('INSERT', 'UPDATE') AND to_jsonb(NEW) ? 'user_id' THEN
        v_fallback_user_id := NULLIF(to_jsonb(NEW)->>'user_id', '')::UUID;
    ELSIF TG_OP = 'DELETE' AND to_jsonb(OLD) ? 'user_id' THEN
        v_fallback_user_id := NULLIF(to_jsonb(OLD)->>'user_id', '')::UUID;
    END IF;

    v_user_id := fn_audit_current_user_id(v_fallback_user_id);
    v_device := fn_audit_device();

    IF TG_OP = 'INSERT' THEN
        INSERT INTO logs (user_id, action, details)
        VALUES (
            v_user_id,
            'CREATE_' || UPPER(TG_TABLE_NAME),
            jsonb_build_object('new_data', fn_enrich_log_data(TG_TABLE_NAME, to_jsonb(NEW)), 'device', v_device)
        );
        RETURN NEW;
    ELSIF TG_OP = 'UPDATE' THEN
        INSERT INTO logs (user_id, action, details)
        VALUES (
            v_user_id,
            'UPDATE_' || UPPER(TG_TABLE_NAME),
            jsonb_build_object(
                'old_data', fn_enrich_log_data(TG_TABLE_NAME, to_jsonb(OLD)),
                'new_data', fn_enrich_log_data(TG_TABLE_NAME, to_jsonb(NEW)),
                'device', v_device
            )
        );
        RETURN NEW;
    ELSIF TG_OP = 'DELETE' THEN
        INSERT INTO logs (user_id, action, details)
        VALUES (
            v_user_id,
            'DELETE_' || UPPER(TG_TABLE_NAME),
            jsonb_build_object('deleted_data', fn_enrich_log_data(TG_TABLE_NAME, to_jsonb(OLD)), 'device', v_device)
        );
        RETURN OLD;
    END IF;

    RETURN NULL;
END;
$log$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION fn_log_refresh_token_changes()
RETURNS TRIGGER AS $log$
DECLARE
    v_device TEXT;
BEGIN
    v_device := fn_audit_device();

    IF TG_OP = 'INSERT' THEN
        INSERT INTO logs (user_id, action, details)
        VALUES (
            NEW.user_id,
            'LOGIN',
            jsonb_build_object(
                'new_data', fn_enrich_log_data(TG_TABLE_NAME, to_jsonb(NEW)),
                'username', fn_audit_user_label(NEW.user_id),
                'device', COALESCE(v_device, NEW.created_by_ip),
                'message', 'User signed in'
            )
        );
        RETURN NEW;
    ELSIF TG_OP = 'DELETE' THEN
        INSERT INTO logs (user_id, action, details)
        VALUES (
            OLD.user_id,
            'LOGOUT',
            jsonb_build_object(
                'deleted_data', fn_enrich_log_data(TG_TABLE_NAME, to_jsonb(OLD)),
                'username', fn_audit_user_label(OLD.user_id),
                'device', COALESCE(v_device, OLD.created_by_ip),
                'message', 'User signed out'
            )
        );
        RETURN OLD;
    END IF;

    RETURN NULL;
END;
$log$ LANGUAGE plpgsql;

-- -- Оновлення балансу рахунку після транзакції
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
    SET balance = balance + (v_delta * p_multiplier)
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

CREATE TRIGGER trg_accounts_logs
    AFTER INSERT OR UPDATE OR DELETE ON accounts
    FOR EACH ROW EXECUTE FUNCTION fn_log_changes();

CREATE TRIGGER trg_budgets_logs
    AFTER INSERT OR UPDATE OR DELETE ON budgets
    FOR EACH ROW EXECUTE FUNCTION fn_log_changes();

CREATE TRIGGER trg_categories_logs
    AFTER INSERT OR UPDATE OR DELETE ON categories
    FOR EACH ROW EXECUTE FUNCTION fn_log_changes();

CREATE TRIGGER trg_group_members_logs
    AFTER INSERT OR UPDATE OR DELETE ON group_members
    FOR EACH ROW EXECUTE FUNCTION fn_log_changes();

CREATE TRIGGER trg_groups_logs
    AFTER INSERT OR UPDATE OR DELETE ON groups
    FOR EACH ROW EXECUTE FUNCTION fn_log_changes();

CREATE TRIGGER trg_recurring_payments_logs
    AFTER INSERT OR UPDATE OR DELETE ON recurring_payments
    FOR EACH ROW EXECUTE FUNCTION fn_log_changes();

CREATE TRIGGER trg_refresh_tokens_logs
    AFTER INSERT OR DELETE ON refresh_tokens
    FOR EACH ROW EXECUTE FUNCTION fn_log_refresh_token_changes();

CREATE TRIGGER trg_savings_logs
    AFTER INSERT OR UPDATE OR DELETE ON savings
    FOR EACH ROW EXECUTE FUNCTION fn_log_changes();

CREATE TRIGGER trg_transactions_logs
    AFTER INSERT OR UPDATE OR DELETE ON transactions
    FOR EACH ROW EXECUTE FUNCTION fn_log_changes();

CREATE TRIGGER trg_user_category_preferences_logs
    AFTER INSERT OR UPDATE OR DELETE ON user_category_preferences
    FOR EACH ROW EXECUTE FUNCTION fn_log_changes();

CREATE TRIGGER trg_users_logs
    AFTER INSERT OR UPDATE OR DELETE ON users
    FOR EACH ROW EXECUTE FUNCTION fn_log_changes();

CREATE TRIGGER trg_wish_list_logs
    AFTER INSERT OR UPDATE OR DELETE ON wish_list
    FOR EACH ROW EXECUTE FUNCTION fn_log_changes();
