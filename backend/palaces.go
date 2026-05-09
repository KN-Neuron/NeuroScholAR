package main

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/gin-gonic/gin"
)

type palaceCreateRequest struct {
	Title       string          `json:"title" binding:"required"`
	Description string          `json:"description"`
	Visibility  string          `json:"visibility"`
	Elements    json.RawMessage `json:"elements"`
}

type palaceUpdateRequest struct {
	Title       *string          `json:"title"`
	Description *string          `json:"description"`
	Visibility  *string          `json:"visibility"`
	Elements    *json.RawMessage `json:"elements"`
}

type palaceSummaryResponse struct {
	ID          int64     `json:"id"`
	Title       string    `json:"title"`
	Description *string   `json:"description"`
	Visibility  string    `json:"visibility"`
	CreatedAt   time.Time `json:"created_at"`
	UpdatedAt   time.Time `json:"updated_at"`
}

type palaceDetailResponse struct {
	ID          int64           `json:"id"`
	Title       string          `json:"title"`
	Description *string         `json:"description"`
	Visibility  string          `json:"visibility"`
	Elements    json.RawMessage `json:"elements"`
	CreatedAt   time.Time       `json:"created_at"`
	UpdatedAt   time.Time       `json:"updated_at"`
}

var errPalaceNotFound = errors.New("palace not found")

func normalizeVisibility(value string) (string, error) {
	v := strings.ToLower(strings.TrimSpace(value))
	if v == "" {
		return "private", nil
	}

	switch v {
	case "private", "public", "unlisted":
		return v, nil
	default:
		return "", errors.New("invalid visibility")
	}
}

func normalizePalaceElements(raw json.RawMessage) (json.RawMessage, error) {
	if len(raw) == 0 {
		return json.RawMessage("[]"), nil
	}
	if !json.Valid(raw) {
		return nil, errors.New("elements must be valid JSON")
	}

	var value any
	if err := json.Unmarshal(raw, &value); err != nil {
		return nil, errors.New("elements must be a JSON array")
	}
	if _, ok := value.([]any); !ok {
		return nil, errors.New("elements must be a JSON array")
	}

	return raw, nil
}

func decodePalaceElements(raw json.RawMessage) ([]map[string]any, error) {
	if len(raw) == 0 {
		return []map[string]any{}, nil
	}
	if !json.Valid(raw) {
		return nil, errors.New("elements must be valid JSON")
	}

	var arr []any
	if err := json.Unmarshal(raw, &arr); err != nil {
		return nil, errors.New("elements must be a JSON array")
	}

	elements := make([]map[string]any, 0, len(arr))
	for _, item := range arr {
		obj, ok := item.(map[string]any)
		if !ok {
			return nil, errors.New("elements must be a JSON array of objects")
		}

		typeVal, ok := obj["type"].(string)
		if !ok || strings.TrimSpace(typeVal) == "" {
			return nil, errors.New("each element must include a non-empty 'type' field")
		}

		obj["type"] = strings.TrimSpace(typeVal)
		delete(obj, "id")
		delete(obj, "sort_order")
		elements = append(elements, obj)
	}

	return elements, nil
}

func replacePalaceObjects(ctx context.Context, tx *sql.Tx, palaceID int64, elements []map[string]any) error {
	if _, err := tx.ExecContext(ctx, `DELETE FROM memory_palace_objects WHERE palace_id = $1`, palaceID); err != nil {
		return err
	}
	if len(elements) == 0 {
		return nil
	}

	const insertQuery = `INSERT INTO memory_palace_objects (palace_id, type, data, sort_order) VALUES ($1, $2, $3::jsonb, $4);`
	for idx, element := range elements {
		typeVal, _ := element["type"].(string)
		typeVal = strings.TrimSpace(typeVal)
		if typeVal == "" {
			return errors.New("each element must include a non-empty 'type' field")
		}
		element["type"] = typeVal
		delete(element, "id")
		delete(element, "sort_order")

		payload, err := json.Marshal(element)
		if err != nil {
			return err
		}

		if _, err := tx.ExecContext(ctx, insertQuery, palaceID, typeVal, string(payload), idx); err != nil {
			return err
		}
	}

	return nil
}

func parsePalaceID(c *gin.Context) (int64, bool) {
	idStr := strings.TrimSpace(c.Param("id"))
	id, err := strconv.ParseInt(idStr, 10, 64)
	if err != nil || id <= 0 {
		c.JSON(http.StatusBadRequest, gin.H{"error": "invalid palace id"})
		return 0, false
	}
	return id, true
}

func listPalacesHandler(db *sql.DB) gin.HandlerFunc {
	return func(c *gin.Context) {
		userID, ok := currentUserID(c)
		if !ok {
			c.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}

		rows, err := db.QueryContext(
			c.Request.Context(),
			`SELECT id, title, description, visibility, created_at, updated_at
			 FROM memory_palaces
			 WHERE user_id = $1
			 ORDER BY updated_at DESC`,
			userID,
		)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to list palaces"})
			return
		}
		defer rows.Close()

		palaces := make([]palaceSummaryResponse, 0)
		for rows.Next() {
			var palace palaceSummaryResponse
			var description sql.NullString
			if err := rows.Scan(&palace.ID, &palace.Title, &description, &palace.Visibility, &palace.CreatedAt, &palace.UpdatedAt); err != nil {
				c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to list palaces"})
				return
			}
			if description.Valid {
				palace.Description = &description.String
			}
			palaces = append(palaces, palace)
		}
		if err := rows.Err(); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to list palaces"})
			return
		}

		c.JSON(http.StatusOK, gin.H{"palaces": palaces})
	}
}

func createPalaceHandler(db *sql.DB) gin.HandlerFunc {
	return func(c *gin.Context) {
		userID, ok := currentUserID(c)
		if !ok {
			c.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}

		var req palaceCreateRequest
		if err := c.ShouldBindJSON(&req); err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"error": "invalid request payload"})
			return
		}

		title := strings.TrimSpace(req.Title)
		if title == "" {
			c.JSON(http.StatusBadRequest, gin.H{"error": "title is required"})
			return
		}

		visibility, err := normalizeVisibility(req.Visibility)
		if err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"error": "invalid visibility"})
			return
		}

		elements, err := decodePalaceElements(req.Elements)
		if err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"error": err.Error()})
			return
		}

		var description any
		if strings.TrimSpace(req.Description) != "" {
			description = req.Description
		}

		tx, err := db.BeginTx(c.Request.Context(), nil)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to create palace"})
			return
		}
		defer func() {
			_ = tx.Rollback()
		}()

		var palaceID int64
		insertQuery := `
			INSERT INTO memory_palaces (user_id, title, description, visibility)
			VALUES ($1, $2, $3, $4)
			RETURNING id
		`
		if err := tx.QueryRowContext(c.Request.Context(), insertQuery, userID, title, description, visibility).Scan(&palaceID); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to create palace"})
			return
		}

		if err := replacePalaceObjects(c.Request.Context(), tx, palaceID, elements); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to create palace"})
			return
		}

		if err := tx.Commit(); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to create palace"})
			return
		}

		resp, err := fetchPalaceDetail(c, db, userID, palaceID)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to create palace"})
			return
		}

		c.JSON(http.StatusCreated, resp)
	}
}

func getPalaceHandler(db *sql.DB) gin.HandlerFunc {
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

		resp, err := fetchPalaceDetail(c, db, userID, palaceID)
		if err != nil {
			if errors.Is(err, errPalaceNotFound) {
				c.JSON(http.StatusNotFound, gin.H{"error": "palace not found"})
				return
			}
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to fetch palace"})
			return
		}

		c.JSON(http.StatusOK, resp)
	}
}

func updatePalaceHandler(db *sql.DB) gin.HandlerFunc {
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

		var req palaceUpdateRequest
		if err := c.ShouldBindJSON(&req); err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"error": "invalid request payload"})
			return
		}

		var title any
		if req.Title != nil {
			trimmed := strings.TrimSpace(*req.Title)
			if trimmed == "" {
				c.JSON(http.StatusBadRequest, gin.H{"error": "title cannot be empty"})
				return
			}
			title = trimmed
		}

		var description any
		if req.Description != nil {
			description = *req.Description
		}

		var visibility any
		if req.Visibility != nil {
			normalized, err := normalizeVisibility(*req.Visibility)
			if err != nil {
				c.JSON(http.StatusBadRequest, gin.H{"error": "invalid visibility"})
				return
			}
			visibility = normalized
		}

		var elements []map[string]any
		hasElements := false
		if req.Elements != nil {
			hasElements = true
			decoded, err := decodePalaceElements(*req.Elements)
			if err != nil {
				c.JSON(http.StatusBadRequest, gin.H{"error": err.Error()})
				return
			}
			elements = decoded
		}

		if req.Title == nil && req.Description == nil && req.Visibility == nil && !hasElements {
			c.JSON(http.StatusBadRequest, gin.H{"error": "no fields to update"})
			return
		}

		tx, err := db.BeginTx(c.Request.Context(), nil)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to update palace"})
			return
		}
		defer func() {
			_ = tx.Rollback()
		}()

		updateQuery := `
			UPDATE memory_palaces
			SET
				title = COALESCE($1, title),
				description = COALESCE($2, description),
				visibility = COALESCE($3, visibility),
				updated_at = NOW()
			WHERE id = $4 AND user_id = $5
			RETURNING id
		`

		var updatedID int64
		if err := tx.QueryRowContext(c.Request.Context(), updateQuery, title, description, visibility, palaceID, userID).Scan(&updatedID); err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				c.JSON(http.StatusNotFound, gin.H{"error": "palace not found"})
				return
			}
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to update palace"})
			return
		}

		if hasElements {
			if err := replacePalaceObjects(c.Request.Context(), tx, palaceID, elements); err != nil {
				c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to update palace"})
				return
			}
		}

		if err := tx.Commit(); err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to update palace"})
			return
		}

		resp, err := fetchPalaceDetail(c, db, userID, palaceID)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to update palace"})
			return
		}

		c.JSON(http.StatusOK, resp)
	}
}

func deletePalaceHandler(db *sql.DB) gin.HandlerFunc {
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

		res, err := db.ExecContext(
			c.Request.Context(),
			`DELETE FROM memory_palaces WHERE id = $1 AND user_id = $2`,
			palaceID,
			userID,
		)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to delete palace"})
			return
		}
		affected, err := res.RowsAffected()
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to delete palace"})
			return
		}
		if affected == 0 {
			c.JSON(http.StatusNotFound, gin.H{"error": "palace not found"})
			return
		}

		c.Status(http.StatusNoContent)
	}
}

func fetchPalaceDetail(c *gin.Context, db *sql.DB, userID, palaceID int64) (palaceDetailResponse, error) {
	var resp palaceDetailResponse
	var descriptionOut sql.NullString

	query := `
		SELECT id, title, description, visibility, created_at, updated_at
		FROM memory_palaces
		WHERE id = $1 AND user_id = $2
	`

	err := db.QueryRowContext(c.Request.Context(), query, palaceID, userID).Scan(
		&resp.ID,
		&resp.Title,
		&descriptionOut,
		&resp.Visibility,
		&resp.CreatedAt,
		&resp.UpdatedAt,
	)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return palaceDetailResponse{}, errPalaceNotFound
		}
		return palaceDetailResponse{}, err
	}

	if descriptionOut.Valid {
		resp.Description = &descriptionOut.String
	}

	rows, err := db.QueryContext(
		c.Request.Context(),
		`SELECT type, data
		 FROM memory_palace_objects
		 WHERE palace_id = $1
		 ORDER BY sort_order ASC, updated_at DESC`,
		palaceID,
	)
	if err != nil {
		return palaceDetailResponse{}, err
	}
	defer rows.Close()

	elements := make([]json.RawMessage, 0)
	for rows.Next() {
		var typeVal string
		var dataOut []byte
		if err := rows.Scan(&typeVal, &dataOut); err != nil {
			return palaceDetailResponse{}, err
		}

		var obj map[string]any
		if err := json.Unmarshal(dataOut, &obj); err != nil || obj == nil {
			obj = map[string]any{}
		}
		obj["type"] = typeVal
		delete(obj, "id")
		delete(obj, "sort_order")
		payload, err := json.Marshal(obj)
		if err != nil {
			return palaceDetailResponse{}, err
		}
		elements = append(elements, json.RawMessage(payload))
	}
	if err := rows.Err(); err != nil {
		return palaceDetailResponse{}, err
	}

	payload, err := json.Marshal(elements)
	if err != nil {
		return palaceDetailResponse{}, err
	}
	resp.Elements = json.RawMessage(payload)

	return resp, nil
}
