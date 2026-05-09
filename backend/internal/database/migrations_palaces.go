package database

import (
	"database/sql"
	"errors"
)

func memoryPalacesTableMigration() migration {
	return migration{
		name: "create_memory_palaces_table",
		run:  createMemoryPalacesTable,
	}
}

func createMemoryPalacesTable(db *sql.DB) error {
	const query = `
		CREATE TABLE IF NOT EXISTS memory_palaces (
			id BIGSERIAL PRIMARY KEY,
			user_id BIGINT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
			title TEXT NOT NULL,
			description TEXT,
			visibility TEXT NOT NULL DEFAULT 'private' CHECK (visibility IN ('private', 'public', 'unlisted')),
			elements JSONB NOT NULL DEFAULT '[]'::jsonb,
			created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
			updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
		);

		CREATE INDEX IF NOT EXISTS memory_palaces_user_id_idx
			ON memory_palaces (user_id);
	`

	_, err := db.Exec(query)
	return err
}

func memoryPalacesSchemaCheck() schemaCheck {
	return schemaCheck{
		name:     "memory_palaces_table_exists",
		validate: ensureMemoryPalacesTableExists,
	}
}

func ensureMemoryPalacesTableExists(db *sql.DB) error {
	const query = `
		SELECT EXISTS (
			SELECT 1
			FROM information_schema.tables
			WHERE table_schema = 'public' AND table_name = 'memory_palaces'
		);
	`

	var exists bool
	if err := db.QueryRow(query).Scan(&exists); err != nil {
		return err
	}
	if !exists {
		return errors.New("required table 'memory_palaces' does not exist; run migrations with: go run ./cmd/migrate")
	}

	return nil
}
