package main

import (
	"bytes"
	"database/sql"
	"encoding/json"
	"errors"
	"net/http"
	"strconv"
	"strings"

	"github.com/gin-gonic/gin"
)

func parseObjectID(c *gin.Context) (int64, bool) {
	idStr := strings.TrimSpace(c.Param("objectId"))
	id, err := strconv.ParseInt(idStr, 10, 64)
	if err != nil || id <= 0 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid object id"})
		return 0, false
	}
	return id, true
}

func normalizeObjectType(value string) (string, error) {
	t := strings.TrimSpace(value)
	if t == "" {
		return "", errors.New("type is required")
	}
	return t, nil
}

func palaceExistsForUser(ctx *gin.Context, db *sql.DB, userID, palaceID int64) (bool, error) {
	const query = `SELECT EXISTS (SELECT 1 FROM memory_palaces WHERE id = $1 AND user_id = $2);`
	var exists bool
	err := db.QueryRowContext(ctx.Request.Context(), query, palaceID, userID).Scan(&exists)
	return exists, err
}

func decodeBodyObject(c *gin.Context) (map[string]any, error) {
	raw, err := c.GetRawData()
	if err != nil {
		return nil, err
	}
	if len(bytes.TrimSpace(raw)) == 0 {
		return nil, errors.New("empty request body")
	}
	if !json.Valid(raw) {
		return nil, errors.New("invalid JSON")
	}

	var value any
	if err := json.Unmarshal(raw, &value); err != nil {
		return nil, errors.New("invalid JSON")
	}

	obj, ok := value.(map[string]any)
	if !ok {
		return nil, errors.New("request body must be a JSON object")
	}

	return obj, nil
}

func parseOptionalInt(value any) (*int, error) {
	if value == nil {
		return nil, nil
	}

	switch v := value.(type) {
	case float64:
		asInt := int(v)
		if float64(asInt) != v {
			return nil, errors.New("sort_order must be an integer")
		}
		return &asInt, nil
	case int:
		vv := v
		return &vv, nil
	case int64:
		vv := int(v)
		if int64(vv) != v {
			return nil, errors.New("sort_order is out of range")
		}
		return &vv, nil
	case json.Number:
		i64, err := v.Int64()
		if err != nil {
			return nil, errors.New("sort_order must be an integer")
		}
		vv := int(i64)
		if int64(vv) != i64 {
			return nil, errors.New("sort_order is out of range")
		}
		return &vv, nil
	default:
		return nil, errors.New("sort_order must be an integer")
	}
}

func copyMap(input map[string]any) map[string]any {
	out := make(map[string]any, len(input))
	for k, v := range input {
		out[k] = v
	}
	return out
}

func isLegacyShape(payload map[string]any) bool {
	_, hasData := payload["data"]
	if !hasData {
		return false
	}

	for key := range payload {
		switch key {
		case "type", "data", "sort_order":
			continue
		default:
			return false
		}
	}

	return true
}

func extractElementForCreate(payload map[string]any) (typeVal string, dataJSON []byte, sortOrder *int, err error) {
	if rawSort, ok := payload["sort_order"]; ok {
		sortOrder, err = parseOptionalInt(rawSort)
		if err != nil {
			return "", nil, nil, err
		}
	}

	var element map[string]any
	if isLegacyShape(payload) {
		rawData, ok := payload["data"].(map[string]any)
		if !ok {
			return "", nil, nil, errors.New("data must be a JSON object")
		}
		element = copyMap(rawData)

		if t, ok := payload["type"].(string); ok {
			element["type"] = t
		}
	} else {
		element = copyMap(payload)
		delete(element, "sort_order")
	}

	delete(element, "id")

	rawType, ok := element["type"].(string)
	if !ok {
		return "", nil, nil, errors.New("type is required")
	}

	typeVal, err = normalizeObjectType(rawType)
	if err != nil {
		return "", nil, nil, err
	}
	element["type"] = typeVal

	dataJSON, err = json.Marshal(element)
	if err != nil {
		return "", nil, nil, err
	}

	return typeVal, dataJSON, sortOrder, nil
}

func extractElementPatch(payload map[string]any) (patch map[string]any, sortOrder *int, err error) {
	if rawSort, ok := payload["sort_order"]; ok {
		sortOrder, err = parseOptionalInt(rawSort)
		if err != nil {
			return nil, nil, err
		}
	}

	if isLegacyShape(payload) {
		rawData, ok := payload["data"].(map[string]any)
		if !ok {
			return nil, nil, errors.New("data must be a JSON object")
		}
		patch = copyMap(rawData)
		if t, ok := payload["type"].(string); ok {
			patch["type"] = t
		}
		return patch, sortOrder, nil
	}

	patch = copyMap(payload)
	delete(patch, "sort_order")
	delete(patch, "id")
	return patch, sortOrder, nil
}

func buildFlattenedObject(id int64, sortOrder int, typeVal string, data []byte) map[string]any {
	var obj map[string]any
	if err := json.Unmarshal(data, &obj); err != nil || obj == nil {
		obj = map[string]any{}
	}
	obj["type"] = typeVal
	obj["id"] = id
	obj["sort_order"] = sortOrder
	return obj
}

func listPalaceObjectsHandler(db *sql.DB) gin.HandlerFunc {
	return func(c *gin.Context) {
		userID, ok := currentUserID(c)
		if !ok {
			c.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		palaceID, ok := parsePalaceID(c)
		if !ok {
			return
		}

		exists, err := palaceExistsForUser(c, db, userID, palaceID)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to list objects"})
			return
		}
		if !exists {
			c.JSON(http.StatusNotFound, gin.H{"error": "palace not found"})
			return
		}

		rows, err := db.QueryContext(
			c.Request.Context(),
			`SELECT o.id, o.type, o.data, o.sort_order
			 FROM memory_palace_objects o
			 WHERE o.palace_id = $1
			 ORDER BY o.sort_order ASC, o.updated_at DESC`,
			palaceID,
		)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to list objects"})
			return
		}
		defer rows.Close()

		objects := make([]map[string]any, 0)
		for rows.Next() {
			var id int64
			var typeVal string
			var dataOut []byte
			var sortOrder int
			if err := rows.Scan(&id, &typeVal, &dataOut, &sortOrder); err != nil {
				c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to list objects"})
				return
			}

			objects = append(objects, buildFlattenedObject(id, sortOrder, typeVal, dataOut))
		}
		if err := rows.Err(); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to list objects"})
			return
		}

		c.JSON(http.StatusOK, gin.H{"objects": objects})
	}
}

func createPalaceObjectHandler(db *sql.DB) gin.HandlerFunc {
	return func(c *gin.Context) {
		userID, ok := currentUserID(c)
		if !ok {
			c.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		palaceID, ok := parsePalaceID(c)
		if !ok {
			return
		}

		payload, err := decodeBodyObject(c)
		if err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"error": err.Error()})
			return
		}

		typeVal, dataJSON, sortOrder, err := extractElementForCreate(payload)
		if err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"error": err.Error()})
			return
		}

		tx, err := db.BeginTx(c.Request.Context(), nil)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to create object"})
			return
		}
		defer func() { _ = tx.Rollback() }()

		query := `
			INSERT INTO memory_palace_objects (palace_id, type, data, sort_order)
			SELECT p.id, $2, $3::jsonb,
				COALESCE($4::integer, COALESCE((SELECT MAX(o.sort_order) + 1 FROM memory_palace_objects o WHERE o.palace_id = p.id), 0))
			FROM memory_palaces p
			WHERE p.id = $1 AND p.user_id = $5
			RETURNING id, palace_id, type, data, sort_order
		`

		sortOrderParam := any(nil)
		if sortOrder != nil {
			sortOrderParam = *sortOrder
		}

		var id int64
		var palaceIDOut int64
		var typeOut string
		var dataOut []byte
		var sortOrderOut int
		err = tx.QueryRowContext(
			c.Request.Context(),
			query,
			palaceID,
			typeVal,
			string(dataJSON),
			sortOrderParam,
			userID,
		).Scan(&id, &palaceIDOut, &typeOut, &dataOut, &sortOrderOut)
		if err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				c.JSON(http.StatusNotFound, gin.H{"error": "palace not found"})
				return
			}
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to create object"})
			return
		}

		if _, err := tx.ExecContext(c.Request.Context(), `UPDATE memory_palaces SET updated_at = NOW() WHERE id = $1 AND user_id = $2`, palaceIDOut, userID); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to create object"})
			return
		}

		if err := tx.Commit(); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to create object"})
			return
		}

		c.JSON(http.StatusCreated, buildFlattenedObject(id, sortOrderOut, typeOut, dataOut))
	}
}

func updateObjectHandler(db *sql.DB) gin.HandlerFunc {
	return func(c *gin.Context) {
		userID, ok := currentUserID(c)
		if !ok {
			c.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		objectID, ok := parseObjectID(c)
		if !ok {
			return
		}

		payload, err := decodeBodyObject(c)
		if err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"error": err.Error()})
			return
		}

		patch, sortOrder, err := extractElementPatch(payload)
		if err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"error": err.Error()})
			return
		}
		if len(patch) == 0 && sortOrder == nil {
			c.JSON(http.StatusBadRequest, gin.H{"error": "no fields to update"})
			return
		}

		tx, err := db.BeginTx(c.Request.Context(), nil)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to update object"})
			return
		}
		defer func() { _ = tx.Rollback() }()

		var palaceID int64
		var currentType string
		var currentData []byte
		var currentSortOrder int
		selectQuery := `
			SELECT o.palace_id, o.type, o.data, o.sort_order
			FROM memory_palace_objects o
			JOIN memory_palaces p ON p.id = o.palace_id
			WHERE o.id = $1 AND p.user_id = $2
		`
		if err := tx.QueryRowContext(c.Request.Context(), selectQuery, objectID, userID).Scan(&palaceID, &currentType, &currentData, &currentSortOrder); err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				c.JSON(http.StatusNotFound, gin.H{"error": "object not found"})
				return
			}
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to update object"})
			return
		}

		var element map[string]any
		if err := json.Unmarshal(currentData, &element); err != nil || element == nil {
			element = map[string]any{}
		}

		for k, v := range patch {
			element[k] = v
		}

		newType := currentType
		if rawType, ok := element["type"].(string); ok {
			normalized, err := normalizeObjectType(rawType)
			if err != nil {
				c.JSON(http.StatusBadRequest, gin.H{"error": err.Error()})
				return
			}
			newType = normalized
		}
		element["type"] = newType
		delete(element, "id")
		delete(element, "sort_order")

		dataJSON, err := json.Marshal(element)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to update object"})
			return
		}

		newSortOrder := currentSortOrder
		if sortOrder != nil {
			newSortOrder = *sortOrder
		}

		updateQuery := `
			UPDATE memory_palace_objects o
			SET type = $3,
				data = $4::jsonb,
				sort_order = $5,
				updated_at = NOW()
			FROM memory_palaces p
			WHERE o.palace_id = p.id
				AND o.id = $1
				AND p.user_id = $2
			RETURNING o.id, o.type, o.data, o.sort_order
		`

		var idOut int64
		var typeOut string
		var dataOut []byte
		var sortOrderOut int
		if err := tx.QueryRowContext(c.Request.Context(), updateQuery, objectID, userID, newType, string(dataJSON), newSortOrder).Scan(&idOut, &typeOut, &dataOut, &sortOrderOut); err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				c.JSON(http.StatusNotFound, gin.H{"error": "object not found"})
				return
			}
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to update object"})
			return
		}

		if _, err := tx.ExecContext(c.Request.Context(), `UPDATE memory_palaces SET updated_at = NOW() WHERE id = $1 AND user_id = $2`, palaceID, userID); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to update object"})
			return
		}

		if err := tx.Commit(); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to update object"})
			return
		}

		c.JSON(http.StatusOK, buildFlattenedObject(idOut, sortOrderOut, typeOut, dataOut))
	}
}

func deleteObjectHandler(db *sql.DB) gin.HandlerFunc {
	return func(c *gin.Context) {
		userID, ok := currentUserID(c)
		if !ok {
			c.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		objectID, ok := parseObjectID(c)
		if !ok {
			return
		}

		tx, err := db.BeginTx(c.Request.Context(), nil)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to delete object"})
			return
		}
		defer func() { _ = tx.Rollback() }()

		var palaceID int64
		deleteQuery := `
			DELETE FROM memory_palace_objects o
			USING memory_palaces p
			WHERE o.palace_id = p.id
				AND o.id = $1
				AND p.user_id = $2
			RETURNING o.palace_id
		`
		err = tx.QueryRowContext(c.Request.Context(), deleteQuery, objectID, userID).Scan(&palaceID)
		if err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				c.JSON(http.StatusNotFound, gin.H{"error": "object not found"})
				return
			}
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to delete object"})
			return
		}

		if _, err := tx.ExecContext(c.Request.Context(), `UPDATE memory_palaces SET updated_at = NOW() WHERE id = $1 AND user_id = $2`, palaceID, userID); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to delete object"})
			return
		}

		if err := tx.Commit(); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to delete object"})
			return
		}

		c.Status(http.StatusNoContent)
	}
}
