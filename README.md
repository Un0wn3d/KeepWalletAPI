# KeepWallet API

KeepWallet API is a secure, offline-first backend for personal finance management.

It supports:
- User registration and login with salted password hashing.
- JWT authentication with access token and refresh token.
- Cookie-based sessions for web clients.
- Income and expense tracking with categories.
- Savings goals with sub-goals, interest options, and rate history.
- Recurring payments and automatic transaction generation.
- Group expenses and split calculations.
- PostgreSQL as the source of truth and SQLite for offline user data.
- Device synchronization with change tracking and conflict resolution.

## Documentation

- [architecture.md](./architecture.md) - system design, layers, and module boundaries.
- [database.md](./database.md) - PostgreSQL and SQLite data models.
- [api.md](./api.md) - endpoints, request and response examples, and error model.
- [sync.md](./sync.md) - offline-first sync workflow and conflict handling.
- [security.md](./security.md) - password hashing, JWT lifecycle, cookies, and hardening.

## Tech Stack

- Backend: ASP.NET Core Web API (.NET 10)
- ORM: Entity Framework Core
- Server database: PostgreSQL
- Offline database: SQLite
- Authentication: JWT (access + refresh) and secure HttpOnly cookies

## Suggested Architecture

The target production structure is a modular clean architecture:

- `KeepWallet.API` - controllers, middleware, API contracts.
- `KeepWallet.Application` - use cases, DTOs, validation, service interfaces.
- `KeepWallet.Domain` - entities, value objects, domain rules.
- `KeepWallet.Infrastructure` - EF Core, repositories, auth providers, token persistence.

The current repository can evolve incrementally to this structure without a full rewrite.

## Quick Start

1. Install prerequisites:
- .NET SDK 10
- PostgreSQL 16+

2. Configure `appsettings.json`:
- `ConnectionStrings:DefaultConnection`
- `Jwt:Issuer`
- `Jwt:Audience`
- `Jwt:Key` (minimum 32 random bytes, store in secret manager or environment variable)

3. Apply database schema and migrations:
- `dotnet ef database update`

4. Run API:

```bash
dotnet run --project KeepWalletAPI
```

5. Open OpenAPI in development:
- `GET /openapi/v1.json`

## Security Baseline

- Passwords are never stored in plain text.
- Passwords are salted per user and hashed with Argon2id.
- Access token lifetime is short (recommended 10-20 minutes).
- Refresh tokens are rotated and stored hashed in the database.
- Auth cookies use `HttpOnly`, `Secure`, and explicit `SameSite`.

Details are documented in [security.md](./security.md).
