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

DROP TRIGGER IF EXISTS trg_refresh_tokens_logs ON refresh_tokens;

CREATE TRIGGER trg_refresh_tokens_logs
    AFTER INSERT OR DELETE ON refresh_tokens
    FOR EACH ROW EXECUTE FUNCTION fn_log_refresh_token_changes();
