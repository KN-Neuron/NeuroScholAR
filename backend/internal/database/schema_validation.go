package database

import (
	"database/sql"
	"fmt"
)

type schemaCheck struct {
	name     string
	validate func(*sql.DB) error
}

func ValidateRequiredSchema(db *sql.DB) error {
	checks := []schemaCheck{
		usersSchemaCheck(),
		usersGoogleOAuthSchemaCheck(),
	}

	for _, c := range checks {
		if err := c.validate(db); err != nil {
			return fmt.Errorf("schema check %s failed: %w", c.name, err)
		}
	}

	return nil
}
