# API Design

## 1. API Conventions

- Base path: `/api/v1`
- Content type: `application/json`
- Date-time format: ISO 8601 UTC
- Authentication:
  - Access token in `HttpOnly` cookie (`access_token`) or `Authorization: Bearer <token>`
  - Refresh token in `HttpOnly` cookie (`refresh_token`)
- Required headers for sync:
  - `X-Device-Id: <stable-device-id>`
  - `X-Request-Id: <uuid>` (recommended for tracing)

## 2. Authentication Endpoints

## `POST /api/v1/auth/register`

Creates a user account.

Request:
```json
{
  "username": "alex",
  "email": "alex@example.com",
  "password": "StrongPassword123!",
  "fullName": "Alex Carter"
}
```

Response `201 Created`:
```json
{
  "userId": "0b2490e2-6eca-4f34-8bfb-ec39e00995cb",
  "username": "alex",
  "email": "alex@example.com",
  "role": "user"
}
```

## `POST /api/v1/auth/login`

Authenticates user and issues access and refresh tokens.

Request:
```json
{
  "login": "alex@example.com",
  "password": "StrongPassword123!"
}
```

Response `200 OK`:
```json
{
  "accessTokenExpiresAt": "2026-05-01T12:30:00Z",
  "refreshTokenExpiresAt": "2026-05-31T12:15:00Z",
  "user": {
    "id": "0b2490e2-6eca-4f34-8bfb-ec39e00995cb",
    "username": "alex",
    "email": "alex@example.com"
  }
}
```

## `POST /api/v1/auth/refresh`

Rotates refresh token and returns a new access token.

Request body:
```json
{}
```

Response `200 OK`:
```json
{
  "accessTokenExpiresAt": "2026-05-01T12:45:00Z"
}
```

## `POST /api/v1/auth/logout`

Revokes current refresh token chain and clears auth cookies.

Response `204 No Content`

## `GET /api/v1/auth/me`

Returns current user profile.

Response `200 OK`:
```json
{
  "id": "0b2490e2-6eca-4f34-8bfb-ec39e00995cb",
  "username": "alex",
  "email": "alex@example.com",
  "fullName": "Alex Carter",
  "role": "user",
  "isActive": true
}
```

## 3. Transactions Endpoints

## `GET /api/v1/transactions`

Query parameters:
- `from`, `to`
- `type` (`income` or `expense`)
- `categoryId`
- `page`, `pageSize`

Response `200 OK`:
```json
{
  "items": [
    {
      "id": "d369f9e8-1ed0-4ed8-8d48-381d048d9862",
      "accountId": "019f7be7-3aa9-4119-afae-c2289e98f6d5",
      "categoryId": "55515270-af01-4f07-96fd-9f4ad7ca6973",
      "type": "expense",
      "amount": 320.50,
      "currency": "USD",
      "transactionDate": "2026-05-01T09:40:00Z",
      "description": "Groceries"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "total": 1
}
```

## `POST /api/v1/transactions`

Request:
```json
{
  "accountId": "019f7be7-3aa9-4119-afae-c2289e98f6d5",
  "categoryId": "55515270-af01-4f07-96fd-9f4ad7ca6973",
  "type": "expense",
  "amount": 320.50,
  "currency": "USD",
  "transactionDate": "2026-05-01T09:40:00Z",
  "description": "Groceries"
}
```

Response `201 Created`:
```json
{
  "id": "d369f9e8-1ed0-4ed8-8d48-381d048d9862"
}
```

## `PUT /api/v1/transactions/{id}`

Uses optimistic concurrency:
- Include `rowVersion` in request.
- Returns `409 Conflict` when stale.

## `DELETE /api/v1/transactions/{id}`

Soft delete (`is_deleted = true`).

Response `204 No Content`

## 4. Savings Endpoints

## `POST /api/v1/savings-goals`

Request:
```json
{
  "name": "Vacation",
  "targetAmount": 25000,
  "description": "Summer trip budget",
  "rateType": "yearly",
  "calculationType": "compound",
  "currentRate": 8.5,
  "startDate": "2026-05-01"
}
```

Response `201 Created`:
```json
{
  "id": "913598f8-b6fc-4433-89ec-b4f05476ce72"
}
```

## `GET /api/v1/savings-goals/{id}`

Returns goal, sub-goals, and rate configuration.

## `PATCH /api/v1/savings-goals/{id}`

Partial update (name, target, description, status).

## `DELETE /api/v1/savings-goals/{id}`

Soft delete.

## `POST /api/v1/savings-goals/{id}/sub-goals`

Request:
```json
{
  "name": "Hotel",
  "targetAmount": 12000
}
```

## `POST /api/v1/savings-goals/{id}/rates`

Adds new rate and closes previous active period.

Request:
```json
{
  "rateType": "yearly",
  "calculationType": "effective",
  "rate": 9.2,
  "validFrom": "2026-06-01",
  "reason": "Bank policy update"
}
```

## 5. Recurring Payments Endpoints

## `POST /api/v1/recurring-payments`

Request:
```json
{
  "name": "Netflix",
  "type": "expense",
  "amount": 15.99,
  "currency": "USD",
  "accountId": "019f7be7-3aa9-4119-afae-c2289e98f6d5",
  "categoryId": "f04f95e2-b3e1-4298-898d-aed4bb9d609f",
  "recurrenceType": "monthly",
  "recurrenceInterval": 1,
  "nextDueAt": "2026-05-15T00:00:00Z",
  "autoGenerate": true
}
```

## `GET /api/v1/recurring-payments`

List recurring rules and next due dates.

## `PATCH /api/v1/recurring-payments/{id}`

Update recurrence settings and activation.

## `POST /api/v1/recurring-payments/{id}/generate-now`

Manually triggers generation (admin/system endpoint).

## 6. Group Expense Endpoints

## `POST /api/v1/groups`

Request:
```json
{
  "name": "Apartment",
  "description": "Shared household expenses",
  "defaultCurrency": "USD"
}
```

## `POST /api/v1/groups/{groupId}/members`

Request:
```json
{
  "userId": "bc56fb38-8d47-4b23-97ad-36f5a7a472bd"
}
```

## `POST /api/v1/groups/{groupId}/expenses`

Request:
```json
{
  "title": "Electricity Bill",
  "paidByUserId": "0b2490e2-6eca-4f34-8bfb-ec39e00995cb",
  "amount": 120,
  "currency": "USD",
  "expenseDate": "2026-05-01T08:00:00Z",
  "splits": [
    {
      "userId": "0b2490e2-6eca-4f34-8bfb-ec39e00995cb",
      "splitType": "equal",
      "shareAmount": 60
    },
    {
      "userId": "bc56fb38-8d47-4b23-97ad-36f5a7a472bd",
      "splitType": "equal",
      "shareAmount": 60
    }
  ]
}
```

## `GET /api/v1/groups/{groupId}/balances`

Returns who owes whom.

## 7. Sync Endpoints

## `POST /api/v1/sync/push`

Pushes client outbox operations.

Request:
```json
{
  "deviceId": "iphone-15-alex",
  "operations": [
    {
      "operationId": "f052ca98-7de5-4570-bfd4-c6ad4f971b6f",
      "entity": "transactions",
      "entityId": "d369f9e8-1ed0-4ed8-8d48-381d048d9862",
      "action": "update",
      "baseRowVersion": 4,
      "payload": {
        "description": "Groceries + household"
      }
    }
  ]
}
```

Response `200 OK`:
```json
{
  "acceptedOperationIds": [
    "f052ca98-7de5-4570-bfd4-c6ad4f971b6f"
  ],
  "conflicts": []
}
```

## `GET /api/v1/sync/pull?afterSequence=1400&limit=500`

Returns remote changes after last cursor.

Response `200 OK`:
```json
{
  "changes": [
    {
      "sequenceId": 1401,
      "entity": "transactions",
      "entityId": "d369f9e8-1ed0-4ed8-8d48-381d048d9862",
      "action": "update",
      "rowVersion": 5,
      "changedAt": "2026-05-01T10:30:00Z",
      "payload": {
        "description": "Groceries + household"
      }
    }
  ],
  "nextSequence": 1401,
  "hasMore": false
}
```

## 8. DTO and Entity Mapping

### Core DTO examples

- `RegisterRequestDto`
- `LoginRequestDto`
- `CreateTransactionRequestDto`
- `UpdateTransactionRequestDto`
- `CreateSavingsGoalRequestDto`
- `CreateRecurringPaymentRequestDto`
- `CreateGroupExpenseRequestDto`
- `SyncPushRequestDto`
- `SyncPullResponseDto`

### Core entity examples

- `User`
- `RefreshToken`
- `Transaction`
- `SavingsGoal`
- `SavingsRateHistory`
- `RecurringPayment`
- `ExpenseGroup`
- `GroupExpense`
- `SyncChangeLog`

Mapping should be explicit and centralized (manual mapper or AutoMapper profile per module).

## 9. Error Handling

Standard error payload:

```json
{
  "code": "validation_error",
  "message": "One or more validation errors occurred.",
  "details": {
    "email": ["Email is invalid"]
  },
  "traceId": "00-f6d39a8f2f9086f98f01b1a938f20e58-bf4b3c8d1d86d7ae-01",
  "timestamp": "2026-05-01T10:31:54Z"
}
```

Typical status codes:
- `200` success
- `201` resource created
- `204` no content
- `400` bad request
- `401` unauthorized
- `403` forbidden
- `404` not found
- `409` conflict (row version mismatch, sync conflict)
- `422` semantic validation failure
- `429` rate limited
- `500` internal server error
