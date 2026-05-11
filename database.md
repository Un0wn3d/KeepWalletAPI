# Database Design

## 1. Design Principles

- PostgreSQL is the **source of truth** for all users and all devices.
- SQLite is a **user-scoped local replica** for offline behavior.
- Business entities use `uuid` primary keys to allow client-side ID creation offline.
- All syncable tables include:
  - `created_at timestamptz`
  - `updated_at timestamptz`
  - `is_deleted boolean`
  - `deleted_at timestamptz null`
  - `row_version bigint` (optimistic concurrency)

## 2. PostgreSQL Schema

## 2.1 Identity and Security

### `roles`
- `id smallint PK`
- `name varchar(50) UNIQUE`
- `description text`

Indexes:
- `ux_roles_name (name)`

### `users`
- `id uuid PK`
- `role_id smallint FK -> roles.id`
- `username varchar(100) UNIQUE`
- `email varchar(255) UNIQUE`
- `password_hash varchar(512)`
- `full_name varchar(255) null`
- `is_active boolean`
- `created_at`, `updated_at`

Indexes:
- `ux_users_username_ci (lower(username))`
- `ux_users_email_ci (lower(email))`
- `ix_users_role_id (role_id)`

### `refresh_tokens`
- `id uuid PK`
- `user_id uuid FK -> users.id`
- `token_hash varchar(128)` (hash of random refresh token)
- `jti uuid UNIQUE`
- `expires_at timestamptz`
- `revoked_at timestamptz null`
- `replaced_by_token_id uuid null`
- `created_by_ip inet null`
- `user_agent varchar(512) null`
- `created_at`

Indexes:
- `ix_refresh_tokens_user_id (user_id)`
- `ix_refresh_tokens_expires_at (expires_at)`
- `ix_refresh_tokens_active (user_id, revoked_at, expires_at)`

### `user_devices`
- `id uuid PK`
- `user_id uuid FK -> users.id`
- `device_id varchar(128)`
- `platform varchar(30)`
- `app_version varchar(30) null`
- `last_seen_at timestamptz`
- `created_at`

Unique:
- `(user_id, device_id)`

## 2.2 Finance Core

### `accounts`
- `id uuid PK`
- `user_id uuid FK -> users.id`
- `name varchar(100)`
- `currency char(3)`
- `balance numeric(18,2)`
- `is_default boolean`
- `created_at`, `updated_at`, `is_deleted`, `deleted_at`, `row_version`

Indexes:
- `ix_accounts_user_id (user_id)`
- `ix_accounts_default (user_id, is_default)`

### `categories`
- `id uuid PK`
- `user_id uuid null FK -> users.id` (`null` means global system category)
- `name varchar(100)`
- `type varchar(10)` (`income` or `expense`)
- `is_system boolean`
- `parent_id uuid null FK -> categories.id`
- `created_at`, `updated_at`, `is_deleted`, `deleted_at`, `row_version`

Indexes:
- `ix_categories_user_id (user_id)`
- `ix_categories_parent_id (parent_id)`
- `ux_categories_name_scope (coalesce(user_id, '00000000-0000-0000-0000-000000000000'), lower(name), type)`

### `transactions`
- `id uuid PK`
- `user_id uuid FK -> users.id`
- `account_id uuid FK -> accounts.id`
- `category_id uuid null FK -> categories.id`
- `type varchar(10)` (`income` or `expense`)
- `amount numeric(18,2)`
- `currency char(3)`
- `transaction_date timestamptz`
- `description text null`
- `source varchar(20)` (`manual`, `recurring`, `group_settlement`)
- `recurring_payment_id uuid null FK -> recurring_payments.id`
- `created_at`, `updated_at`, `is_deleted`, `deleted_at`, `row_version`

Indexes:
- `ix_transactions_user_date (user_id, transaction_date desc)`
- `ix_transactions_account_date (account_id, transaction_date desc)`
- `ix_transactions_category_date (category_id, transaction_date desc)`
- `ix_transactions_type (user_id, type)`

## 2.3 Savings and Interest

### `savings_goals`
- `id uuid PK`
- `user_id uuid FK -> users.id`
- `name varchar(200)`
- `target_amount numeric(18,2)`
- `current_amount numeric(18,2)`
- `description text null`
- `rate_type varchar(10)` (`monthly`, `yearly`)
- `calculation_type varchar(10)` (`simple`, `compound`, `effective`)
- `current_rate numeric(9,4)`
- `start_date date`
- `end_date date null`
- `is_active boolean`
- `created_at`, `updated_at`, `is_deleted`, `deleted_at`, `row_version`

Indexes:
- `ix_savings_user_id (user_id)`
- `ix_savings_active (user_id, is_active)`

### `savings_sub_goals`
- `id uuid PK`
- `savings_goal_id uuid FK -> savings_goals.id`
- `name varchar(200)`
- `target_amount numeric(18,2)`
- `sort_order int`
- `created_at`, `updated_at`, `is_deleted`, `deleted_at`, `row_version`

Indexes:
- `ix_savings_sub_goals_parent (savings_goal_id, sort_order)`

### `savings_rate_history`
- `id uuid PK`
- `savings_goal_id uuid FK -> savings_goals.id`
- `rate_type varchar(10)`
- `calculation_type varchar(10)`
- `rate numeric(9,4)`
- `valid_from date`
- `valid_to date null`
- `reason text null`
- `changed_by_user_id uuid FK -> users.id`
- `created_at`

Indexes:
- `ix_rate_history_goal_date (savings_goal_id, valid_from desc)`

### `savings_interest_entries`
- `id uuid PK`
- `savings_goal_id uuid FK -> savings_goals.id`
- `accrual_date date`
- `base_amount numeric(18,2)`
- `rate_used numeric(9,4)`
- `interest_amount numeric(18,2)`
- `total_after numeric(18,2)`
- `created_at`

Indexes:
- `ix_interest_goal_date (savings_goal_id, accrual_date desc)`

## 2.4 Recurring Payments

### `recurring_payments`
- `id uuid PK`
- `user_id uuid FK -> users.id`
- `account_id uuid FK -> accounts.id`
- `category_id uuid null FK -> categories.id`
- `name varchar(200)`
- `type varchar(10)` (`income` or `expense`)
- `amount numeric(18,2)`
- `currency char(3)`
- `recurrence_type varchar(10)` (`daily`, `weekly`, `monthly`, `yearly`, `custom`)
- `recurrence_interval int null`
- `recurrence_payload jsonb null` (for custom rules)
- `next_due_at timestamptz`
- `last_generated_at timestamptz null`
- `auto_generate boolean`
- `is_active boolean`
- `created_at`, `updated_at`, `is_deleted`, `deleted_at`, `row_version`

Indexes:
- `ix_recurring_due (is_active, next_due_at)`
- `ix_recurring_user_active (user_id, is_active)`

### `recurring_generations`
- `id uuid PK`
- `recurring_payment_id uuid FK -> recurring_payments.id`
- `transaction_id uuid FK -> transactions.id`
- `generated_for timestamptz`
- `created_at`

Unique:
- `(recurring_payment_id, generated_for)`

## 2.5 Groups and Shared Expenses

### `expense_groups`
- `id uuid PK`
- `owner_user_id uuid FK -> users.id`
- `name varchar(200)`
- `description text null`
- `default_currency char(3)`
- `created_at`, `updated_at`, `is_deleted`, `deleted_at`, `row_version`

### `expense_group_members`
- `id uuid PK`
- `group_id uuid FK -> expense_groups.id`
- `user_id uuid FK -> users.id`
- `role varchar(20)` (`owner`, `member`)
- `joined_at timestamptz`
- `is_active boolean`

Unique:
- `(group_id, user_id)`

Indexes:
- `ix_group_members_user (user_id, is_active)`

### `group_expenses`
- `id uuid PK`
- `group_id uuid FK -> expense_groups.id`
- `paid_by_user_id uuid FK -> users.id`
- `title varchar(200)`
- `amount numeric(18,2)`
- `currency char(3)`
- `expense_date timestamptz`
- `notes text null`
- `created_at`, `updated_at`, `is_deleted`, `deleted_at`, `row_version`

Indexes:
- `ix_group_expenses_group_date (group_id, expense_date desc)`

### `group_expense_splits`
- `id uuid PK`
- `group_expense_id uuid FK -> group_expenses.id`
- `user_id uuid FK -> users.id`
- `split_type varchar(15)` (`equal`, `fixed`, `percentage`)
- `share_amount numeric(18,2)`
- `share_percentage numeric(9,4) null`

Indexes:
- `ix_group_splits_expense (group_expense_id)`
- `ix_group_splits_user (user_id)`

### `group_settlements`
- `id uuid PK`
- `group_id uuid FK -> expense_groups.id`
- `from_user_id uuid FK -> users.id`
- `to_user_id uuid FK -> users.id`
- `amount numeric(18,2)`
- `currency char(3)`
- `settlement_date timestamptz`
- `created_at`

Indexes:
- `ix_group_settlements_group_date (group_id, settlement_date desc)`

## 2.6 Sync Infrastructure

### `sync_change_log`
- `sequence_id bigint generated always as identity PK`
- `entity_name varchar(100)`
- `entity_id uuid`
- `owner_user_id uuid` (tenant owner)
- `operation varchar(10)` (`insert`, `update`, `delete`)
- `row_version bigint`
- `changed_at timestamptz`
- `changed_by_device_id varchar(128) null`
- `payload jsonb` (optional compact projection for pull)

Indexes:
- `ix_sync_owner_sequence (owner_user_id, sequence_id)`
- `ix_sync_entity (entity_name, entity_id, sequence_id desc)`

### `processed_sync_operations`
- `operation_id uuid PK` (client-generated idempotency key)
- `user_id uuid`
- `device_id varchar(128)`
- `processed_at timestamptz`
- `result_status varchar(20)`

Indexes:
- `ix_processed_ops_user_device (user_id, device_id, processed_at desc)`

## 3. Relationship Summary

- One `user` has many `accounts`, `transactions`, `savings_goals`, `recurring_payments`, and `user_devices`.
- One `savings_goal` has many `savings_sub_goals`, `savings_rate_history`, and `savings_interest_entries`.
- One `expense_group` has many `expense_group_members`, `group_expenses`, and `group_settlements`.
- One `group_expense` has many `group_expense_splits`.
- One `recurring_payment` can generate many `transactions` (through `recurring_generations`).

## 4. SQLite Local Schema (Offline)

SQLite stores only data needed by the current user and joined groups.

### Required local tables

- `local_profile`
- `accounts`
- `categories`
- `transactions`
- `savings_goals`
- `savings_sub_goals`
- `savings_rate_history`
- `recurring_payments`
- `expense_groups`
- `expense_group_members`
- `group_expenses`
- `group_expense_splits`
- `group_settlements`

### Sync support tables (SQLite only)

- `local_outbox`
  - Stores pending create/update/delete operations.
  - Includes `operation_id`, `entity_name`, `entity_id`, `payload`, `base_row_version`, `created_at`, `retry_count`.
- `local_sync_state`
  - Stores `last_server_sequence_id` and last successful sync timestamp.
- `local_conflicts`
  - Stores unresolved conflicts for user-assisted merge flows.

### Data excluded from SQLite

- Password hashes and server-side token tables.
- Other users' private data outside shared groups.
- Full server audit logs.

## 5. Indexing Strategy Notes

- Prioritize query paths used by mobile timelines: `transactions(user_id, transaction_date desc)`.
- Add partial indexes for active rows (`where is_deleted = false`) in large tables.
- Keep write-heavy tables (`sync_change_log`) append-only and partition by time in high scale.
- Validate index usage with `EXPLAIN ANALYZE` before adding secondary indexes.
