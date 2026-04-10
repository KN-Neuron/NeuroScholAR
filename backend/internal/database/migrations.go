package database

import (
	"database/sql"
	"fmt"
)

type migration struct {
	name string
	run  func(*sql.DB) error
}

func RunMigrations(db *sql.DB) error {
	migrations := []migration{
		usersTableMigration(),
		usersGoogleOAuthMigration(),
	}

	for _, m := range migrations {
		if err := m.run(db); err != nil {
			return fmt.Errorf("migration %s failed: %w", m.name, err)
		}
	}

	return nil
}
