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

| Variable | Description |
|----------|-------------|
| `PORT` | HTTP server port (default: `8080`) |
| `DB_HOST` | PostgreSQL host |
| `DB_PORT` | PostgreSQL port |
| `DB_USER` | Database user |
| `DB_PASSWORD` | Database password |
| `DB_NAME` | Database name |
| `DB_SSLMODE` | SSL mode (for local Docker use `disable`) |
| `JWT_SECRET` | Secret key used to sign JWT tokens (required) |
| `JWT_EXPIRATION_HOURS` | Access token expiration in hours (default: `24`) |

Recommended local database values:
- `DB_HOST=localhost`
- `DB_PORT=5432`
- `DB_USER=postgres`
- `DB_PASSWORD=postgres`
- `DB_NAME=neuroscholar`
- `DB_SSLMODE=disable`

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Server and DB liveness |
| `POST` | `/register` | Register user and return JWT token |
| `POST` | `/login` | Authenticate user and return JWT token |
