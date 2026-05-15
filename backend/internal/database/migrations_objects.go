package database

import (
	"database/sql"
	"errors"
)

func memoryPalaceObjectsTableMigration() migration {
	return migration{
		name: "create_memory_palace_objects_table",
		run:  createMemoryPalaceObjectsTable,
	}
}

func backfillMemoryPalaceElementsToObjectsMigration() migration {
	return migration{
		name: "backfill_memory_palace_elements_to_objects",
		run:  backfillMemoryPalaceElementsToObjects,
	}
}

func createMemoryPalaceObjectsTable(db *sql.DB) error {
	const query = `
		CREATE TABLE IF NOT EXISTS memory_palace_objects (
			id BIGSERIAL PRIMARY KEY,
			palace_id BIGINT NOT NULL REFERENCES memory_palaces(id) ON DELETE CASCADE,
			type TEXT NOT NULL,
			data JSONB NOT NULL DEFAULT '{}'::jsonb,
			sort_order INTEGER NOT NULL DEFAULT 0,
			created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
			updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
		);

		CREATE INDEX IF NOT EXISTS memory_palace_objects_palace_id_idx
			ON memory_palace_objects (palace_id);

		CREATE INDEX IF NOT EXISTS memory_palace_objects_palace_sort_idx
			ON memory_palace_objects (palace_id, sort_order);
	`

	_, err := db.Exec(query)
	return err
}

func backfillMemoryPalaceElementsToObjects(db *sql.DB) error {
	// One-time bridge migration: older builds stored the full elements list on
	// memory_palaces.elements. Newer builds store each element as a row in
	// memory_palace_objects. To avoid losing data, copy elements into objects for
	// any palace that has elements but no objects yet.
	const query = `
		INSERT INTO memory_palace_objects (palace_id, type, data, sort_order)
		SELECT
			p.id,
			COALESCE(elem ->> 'type', 'unknown') AS type,
			elem AS data,
			(ordinality - 1)::integer AS sort_order
		FROM memory_palaces p
		CROSS JOIN LATERAL jsonb_array_elements(p.elements) WITH ORDINALITY AS t(elem, ordinality)
		WHERE jsonb_typeof(p.elements) = 'array'
			AND NOT EXISTS (
				SELECT 1
				FROM memory_palace_objects o
				WHERE o.palace_id = p.id
			);
	`

	_, err := db.Exec(query)
	return err
}

func memoryPalaceObjectsSchemaCheck() schemaCheck {
	return schemaCheck{
		name:     "memory_palace_objects_table_exists",
		validate: ensureMemoryPalaceObjectsTableExists,
	}
}

func ensureMemoryPalaceObjectsTableExists(db *sql.DB) error {
	const query = `
		SELECT EXISTS (
			SELECT 1
			FROM information_schema.tables
			WHERE table_schema = 'public' AND table_name = 'memory_palace_objects'
		);
	`

	var exists bool
	if err := db.QueryRow(query).Scan(&exists); err != nil {
		return err
	}
	if !exists {
		return errors.New("required table 'memory_palace_objects' does not exist; run migrations with: go run ./cmd/migrate")
	}

	return nil
}
