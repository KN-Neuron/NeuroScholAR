package database

import (
	"database/sql"
	"errors"
)

func usersTableMigration() migration {
	return migration{
		name: "create_users_table",
		run:  createUsersTable,
	}
}

func createUsersTable(db *sql.DB) error {
	const query = `
		CREATE TABLE IF NOT EXISTS users (
			id BIGSERIAL PRIMARY KEY,
			email TEXT NOT NULL UNIQUE,
			password_hash TEXT NOT NULL,
			created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
			updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
		);
	`

	_, err := db.Exec(query)
	return err
}

func usersSchemaCheck() schemaCheck {
	return schemaCheck{
		name:     "users_table_exists",
		validate: ensureUsersTableExists,
	}
}

func ensureUsersTableExists(db *sql.DB) error {
	const query = `
		SELECT EXISTS (
			SELECT 1
			FROM information_schema.tables
			WHERE table_schema = 'public' AND table_name = 'users'
		);
	`

	var exists bool
	if err := db.QueryRow(query).Scan(&exists); err != nil {
		return err
	}
	if !exists {
		return errors.New("required table 'users' does not exist; run migrations with: go run ./cmd/migrate")
	}

	return nil
}
