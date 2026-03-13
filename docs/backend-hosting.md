# Backend – Usage & Hosting

## Requirements

- [Go 1.22+](https://go.dev/dl/)
- [Docker](https://www.docker.com/)

## Run Locally

```bash
cp .env.example .env        # configure environment
docker compose up -d        # start PostgreSQL
go run main.go              # start server
```

Server runs at `http://localhost:8080`.

## Environment Variables

| Variable      | Description        |
|---------------|--------------------|
| `PORT`        | HTTP server port   |
| `DB_HOST`     | PostgreSQL host    |
| `DB_PORT`     | PostgreSQL port    |
| `DB_USER`     | Database user      |
| `DB_PASSWORD` | Database password  |
| `DB_NAME`     | Database name      |
| `DB_SSLMODE`  | SSL mode           |

## Endpoints

| Method | Path      | Description            |
|--------|-----------|------------------------|
| `GET`  | `/health` | Server and DB liveness |
