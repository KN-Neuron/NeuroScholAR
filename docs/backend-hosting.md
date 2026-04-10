# Backend – Usage & Hosting

## Requirements

- [Go 1.22+](https://go.dev/dl/)
- [Docker](https://www.docker.com/)

## Run Locally

```bash
cd backend
cp .env.example .env        # configure environment (Windows: copy .env.example .env)
docker compose up -d db     # start PostgreSQL
docker compose run --rm migrate
go run .                    # start API server
```

Server runs at `http://localhost:8080`.

Startup behavior:

- The API validates required schema on startup.
- If tables are missing, startup fails with a clear message to run migrations.
- Schema changes are applied only by the explicit migration step.

## Environment Variables

|Variable|Description|
|---|---|
|`PORT`|HTTP server port (default: `8080`)|
|`DB_HOST`|PostgreSQL host|
|`DB_PORT`|PostgreSQL port|
|`DB_USER`|Database user|
|`DB_PASSWORD`|Database password|
|`DB_NAME`|Database name|
|`DB_SSLMODE`|SSL mode (for local Docker use `disable`)|
|`JWT_SECRET`|Secret key used to sign JWT tokens (required)|
|`JWT_EXPIRATION_HOURS`|Access token expiration in hours (default: `24`)|
|`GOOGLE_CLIENT_ID`|OAuth 2.0 Client ID from Google Cloud (required for Google login)|
|`GOOGLE_CLIENT_SECRET`|OAuth 2.0 Client Secret from Google Cloud (required for Google login)|
|`GOOGLE_REDIRECT_URL`|OAuth callback URL registered in Google Cloud (for example `http://localhost:8080/auth/google/callback`)|
|`GOOGLE_AUTO_LINK_BY_EMAIL`|Allow automatic linking by matching Google email to existing local account (`false` by default for safer behavior)|

Recommended local database values:

- `DB_HOST=localhost`
- `DB_PORT=5432`
- `DB_USER=postgres`
- `DB_PASSWORD=postgres`
- `DB_NAME=neuroscholar`
- `DB_SSLMODE=disable`

## Endpoints

|Method|Path|Description|
|---|---|---|
|`GET`|`/health`|Server and DB liveness|
|`POST`|`/register`|Register user and return JWT token|
|`POST`|`/login`|Authenticate user and return JWT token|
|`GET`|`/auth/google/login`|Start Google OAuth login (redirect by default, JSON URL with `?mode=json`)|
|`GET`|`/auth/google/callback`|Handle Google OAuth callback, link/create user, return JWT|

## Google OAuth Setup (Google Cloud)

1. Open [Google Cloud Console](https://console.cloud.google.com/) and create/select a project.
2. Configure OAuth consent screen:

   - Go to APIs & Services -> OAuth consent screen.
   - Choose user type and fill required app details.
   - Add scopes: `openid`, `email`, `profile`.

3. Create OAuth client credentials:

   - Go to APIs & Services -> Credentials -> Create Credentials -> OAuth client ID.
   - Application type: `Web application`.
   - Add Authorized redirect URI values:
     - Local: `http://localhost:8080/auth/google/callback`
     - Production: `https://<your-domain>/auth/google/callback`

4. Copy generated credentials into backend environment:

   - `GOOGLE_CLIENT_ID=...`
   - `GOOGLE_CLIENT_SECRET=...`
   - `GOOGLE_REDIRECT_URL=...` (must exactly match one registered redirect URI)

Flow notes:

- `GET /auth/google/login` creates and stores an OAuth state cookie.
- `GET /auth/google/callback` validates the state, exchanges authorization code, and reads Google profile.
- If a user with the same email already exists, backend links `google_sub` only when `GOOGLE_AUTO_LINK_BY_EMAIL=true`.
- Default (`GOOGLE_AUTO_LINK_BY_EMAIL=false`) avoids risky automatic linking based only on email ownership assumptions.
- If no user exists, backend creates a new user with Google identity linked.
