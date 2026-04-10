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

func usersGoogleOAuthMigration() migration {
	return migration{
		name: "add_google_oauth_fields_to_users",
		run:  addGoogleOAuthFieldsToUsers,
	}
}

func createUsersTable(db *sql.DB) error {
	const query = `
		CREATE TABLE IF NOT EXISTS users (
			id BIGSERIAL PRIMARY KEY,
			email TEXT NOT NULL UNIQUE,
			password_hash TEXT,
			created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
			updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
		);
	`

	_, err := db.Exec(query)
	return err
}

func addGoogleOAuthFieldsToUsers(db *sql.DB) error {
	const query = `
		ALTER TABLE users
			ADD COLUMN IF NOT EXISTS google_sub TEXT;

		ALTER TABLE users
			ALTER COLUMN password_hash DROP NOT NULL;

		CREATE UNIQUE INDEX IF NOT EXISTS users_google_sub_unique
			ON users (google_sub)
			WHERE google_sub IS NOT NULL;
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

func usersGoogleOAuthSchemaCheck() schemaCheck {
	return schemaCheck{
		name:     "users_google_oauth_columns_exist",
		validate: ensureUsersGoogleOAuthColumnsExist,
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

func ensureUsersGoogleOAuthColumnsExist(db *sql.DB) error {
	const query = `
		SELECT EXISTS (
			SELECT 1
			FROM information_schema.columns
			WHERE table_schema = 'public'
				AND table_name = 'users'
				AND column_name = 'google_sub'
		);
	`

	var exists bool
	if err := db.QueryRow(query).Scan(&exists); err != nil {
		return err
	}
	if !exists {
		return errors.New("required column 'users.google_sub' does not exist; run migrations with: go run ./cmd/migrate")
	}

	return nil
}
