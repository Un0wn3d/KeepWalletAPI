# Security Design

## 1. Security Objectives

- Never store plain-text passwords.
- Protect authentication tokens against theft and replay.
- Support both bearer-token and secure cookie usage.
- Preserve auditability for account and financial operations.

## 2. Password Hashing Strategy

## Algorithm

Use **Argon2id** with per-user random salt.

Recommended baseline:
- Memory: 64 MiB (`m=65536`)
- Iterations: 3 to 4 (`t=3..4`)
- Parallelism: 1 to 2 (`p=1..2`)
- Salt length: 16 bytes minimum
- Hash length: 32 bytes minimum

Tune parameters to keep hash time around 200-500 ms on production hardware.

## Storage format

Store a single encoded hash string that contains algorithm metadata and unique salt, for example:

```text
$argon2id$v=19$m=65536,t=4,p=2$<base64-salt>$<base64-hash>
```

Each user gets a unique random salt generated with `RandomNumberGenerator`.

## Optional pepper

Add an application-level pepper from secret storage:
- Pepper is not stored in the database.
- Rotate carefully with a staged migration strategy.

## 3. Password Verification Flow

1. User submits login and password.
2. Fetch user by username/email.
3. Parse stored Argon2id parameters and salt from hash string.
4. Recompute hash for provided password.
5. Compare with constant-time comparison.
6. If valid and user active, issue tokens.

C# pseudo-code:

```csharp
public bool VerifyPassword(string password, string encodedHash)
{
    // Parse Argon2id metadata + salt from encoded hash
    var parsed = Argon2EncodedHash.Parse(encodedHash);

    // Recompute hash with the same parameters
    var computed = Argon2idHasher.Hash(
        password,
        parsed.Salt,
        parsed.MemoryKb,
        parsed.Iterations,
        parsed.Parallelism,
        parsed.HashLength);

    // Constant-time comparison prevents timing attacks
    return CryptographicOperations.FixedTimeEquals(computed, parsed.Hash);
}
```

## 4. JWT Authentication Model

Use two-token approach:

- **Access token** (short-lived, stateless JWT)
- **Refresh token** (long-lived, opaque random token, stored hashed in DB)

## Access token

Recommended TTL:
- 10 to 20 minutes

Claims:
- `sub` (user id)
- `jti` (token id)
- `role`
- `device_id`
- `iat`, `exp`, `iss`, `aud`

## Refresh token

Recommended TTL:
- 30 days (sliding via rotation)

Generation:
- 64 random bytes from CSPRNG, Base64Url encoded.

Persistence:
- Store only SHA-256 hash (or HMAC-SHA-256 with server key).
- Save metadata: `user_id`, `expires_at`, `revoked_at`, `replaced_by_token_id`, `created_by_ip`, `user_agent`.

## 5. Refresh Token Rotation and Reuse Detection

At `POST /api/v1/auth/refresh`:

1. Read refresh token from cookie.
2. Hash it and lookup active token row.
3. If token missing/revoked/expired -> reject `401`.
4. Revoke current token row.
5. Create new refresh token row (rotation).
6. Issue new access token and refresh cookie.

Reuse detection:
- If a revoked token is used again, treat as token theft.
- Revoke full token family for that user/device.
- Force re-login and record security event.

## 6. Expiration and Revocation Handling

- Access token expiration is enforced by JWT middleware.
- Refresh endpoint is the only path for session continuation.
- Logout revokes current refresh token and clears cookies.
- "Logout from all devices" revokes all active refresh tokens for the user.

## 7. Secure Cookie Configuration

Use separate cookies:
- `access_token`
- `refresh_token`

Production cookie requirements:
- `HttpOnly = true`
- `Secure = true`
- `SameSite = Lax` or `Strict` for same-site apps
- `Path = /` for access token
- `Path = /api/v1/auth/refresh` for refresh token (optional hardening)

Example:

```csharp
var accessCookie = new CookieOptions
{
    HttpOnly = true,
    Secure = true,
    SameSite = SameSiteMode.Lax,
    Expires = accessTokenExpiresAt,
    Path = "/"
};

var refreshCookie = new CookieOptions
{
    HttpOnly = true,
    Secure = true,
    SameSite = SameSiteMode.Lax,
    Expires = refreshTokenExpiresAt,
    Path = "/api/v1/auth"
};
```

Notes:
- If frontend is cross-site, use `SameSite=None` and keep `Secure=true`.
- Never set tokens in JavaScript-readable cookies.

## 8. CSRF Protection for Cookie Auth

Because cookies are automatically sent by browsers:
- Require CSRF token for state-changing endpoints when using cookie auth.
- Use double-submit token or antiforgery middleware.
- Validate `Origin` and `Referer` for sensitive endpoints.

## 9. Additional Hardening

- Rate limit login and refresh endpoints.
- Lock out account temporarily after repeated failures.
- Require strong password policy.
- Use TLS everywhere.
- Rotate JWT signing keys with key IDs (`kid`).
- Store secrets in secret manager, not `appsettings.json` in production.
- Audit log: login, logout, refresh, password change, suspicious token reuse.

## 10. Minimum Security Checklist

- [ ] Passwords hashed with Argon2id
- [ ] Unique random salt per user
- [ ] Constant-time hash comparison
- [ ] Access + refresh token model
- [ ] Refresh token rotation and reuse detection
- [ ] Secure HttpOnly cookies
- [ ] CSRF defense for cookie sessions
- [ ] Token revocation on logout
- [ ] Login/refresh rate limiting
- [ ] Full HTTPS enforcement
