package main

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/url"
	"os"
	"strconv"
	"strings"
	"time"

	"neuroscholar/backend/internal/database"

	"github.com/gin-gonic/gin"
	"github.com/golang-jwt/jwt/v5"
	"github.com/joho/godotenv"
	"golang.org/x/crypto/bcrypt"
)

type registerRequest struct {
	Email    string `json:"email" binding:"required,email"`
	Password string `json:"password" binding:"required,min=8"`
}

type loginRequest struct {
	Email    string `json:"email" binding:"required,email"`
	Password string `json:"password" binding:"required"`
}

type jwtClaims struct {
	UserID int64  `json:"user_id"`
	Email  string `json:"email"`
	jwt.RegisteredClaims
}

type googleTokenResponse struct {
	AccessToken      string `json:"access_token"`
	TokenType        string `json:"token_type"`
	ExpiresIn        int    `json:"expires_in"`
	Scope            string `json:"scope"`
	RefreshToken     string `json:"refresh_token"`
	Error            string `json:"error"`
	ErrorDescription string `json:"error_description"`
}

type googleUserInfo struct {
	Subject       string `json:"sub"`
	Email         string `json:"email"`
	EmailVerified bool   `json:"email_verified"`
	Name          string `json:"name"`
	Picture       string `json:"picture"`
}

var errGoogleAccountConflict = errors.New("google account is already linked to a different user")
var errGoogleAutoLinkDisabled = errors.New("automatic email-based Google account linking is disabled")

const googleOAuthStateCookieName = "google_oauth_state"

func jwtSecret() string {
	return os.Getenv("JWT_SECRET")
}

func jwtExpirationHours() int {
	hours, err := strconv.Atoi(os.Getenv("JWT_EXPIRATION_HOURS"))
	if err != nil || hours <= 0 {
		return 24
	}
	return hours
}

func createToken(userID int64, email string) (string, error) {
	now := time.Now()
	claims := jwtClaims{
		UserID: userID,
		Email:  email,
		RegisteredClaims: jwt.RegisteredClaims{
			IssuedAt:  jwt.NewNumericDate(now),
			ExpiresAt: jwt.NewNumericDate(now.Add(time.Duration(jwtExpirationHours()) * time.Hour)),
			Subject:   strconv.FormatInt(userID, 10),
		},
	}

	token := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	return token.SignedString([]byte(jwtSecret()))
}

func normalizeEmail(email string) string {
	return strings.ToLower(strings.TrimSpace(email))
}

func googleClientID() string {
	return os.Getenv("GOOGLE_CLIENT_ID")
}

func googleClientSecret() string {
	return os.Getenv("GOOGLE_CLIENT_SECRET")
}

func googleRedirectURL() string {
	return os.Getenv("GOOGLE_REDIRECT_URL")
}

func googleOAuthConfigured() bool {
	return googleClientID() != "" && googleClientSecret() != "" && googleRedirectURL() != ""
}

func googleAutoLinkByEmailEnabled() bool {
	value := strings.TrimSpace(strings.ToLower(os.Getenv("GOOGLE_AUTO_LINK_BY_EMAIL")))
	return value == "1" || value == "true" || value == "yes"
}

func newOAuthState() (string, error) {
	stateBytes := make([]byte, 32)
	if _, err := rand.Read(stateBytes); err != nil {
		return "", err
	}

	return base64.RawURLEncoding.EncodeToString(stateBytes), nil
}

func setOAuthStateCookie(c *gin.Context, state string) {
	secure := c.Request.TLS != nil || strings.EqualFold(c.GetHeader("X-Forwarded-Proto"), "https")
	http.SetCookie(c.Writer, &http.Cookie{
		Name:     googleOAuthStateCookieName,
		Value:    state,
		Path:     "/",
		MaxAge:   600,
		HttpOnly: true,
		Secure:   secure,
		SameSite: http.SameSiteLaxMode,
	})
}

func clearOAuthStateCookie(c *gin.Context) {
	secure := c.Request.TLS != nil || strings.EqualFold(c.GetHeader("X-Forwarded-Proto"), "https")
	http.SetCookie(c.Writer, &http.Cookie{
		Name:     googleOAuthStateCookieName,
		Value:    "",
		Path:     "/",
		MaxAge:   -1,
		HttpOnly: true,
		Secure:   secure,
		SameSite: http.SameSiteLaxMode,
	})
}

func buildGoogleOAuthURL(state string) string {
	params := url.Values{}
	params.Set("client_id", googleClientID())
	params.Set("redirect_uri", googleRedirectURL())
	params.Set("response_type", "code")
	params.Set("scope", "openid email profile")
	params.Set("state", state)

	return "https://accounts.google.com/o/oauth2/v2/auth?" + params.Encode()
}

func exchangeGoogleCodeForToken(ctx context.Context, code string) (*googleTokenResponse, error) {
	form := url.Values{}
	form.Set("code", code)
	form.Set("client_id", googleClientID())
	form.Set("client_secret", googleClientSecret())
	form.Set("redirect_uri", googleRedirectURL())
	form.Set("grant_type", "authorization_code")

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, "https://oauth2.googleapis.com/token", strings.NewReader(form.Encode()))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")

	client := &http.Client{Timeout: 10 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, err
	}

	var tokenResp googleTokenResponse
	if err := json.Unmarshal(body, &tokenResp); err != nil {
		return nil, fmt.Errorf("failed to decode Google token response: %w", err)
	}

	if resp.StatusCode != http.StatusOK {
		detail := tokenResp.ErrorDescription
		if detail == "" {
			detail = tokenResp.Error
		}
		if detail == "" {
			detail = "unknown OAuth error"
		}

		return nil, fmt.Errorf("google token exchange failed: %s", detail)
	}

	if tokenResp.AccessToken == "" {
		return nil, errors.New("google token exchange returned empty access token")
	}

	return &tokenResp, nil
}

func fetchGoogleUserInfo(ctx context.Context, accessToken string) (*googleUserInfo, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, "https://openidconnect.googleapis.com/v1/userinfo", nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Authorization", "Bearer "+accessToken)

	client := &http.Client{Timeout: 10 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, err
	}

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("failed to fetch Google user info: status %d", resp.StatusCode)
	}

	var userInfo googleUserInfo
	if err := json.Unmarshal(body, &userInfo); err != nil {
		return nil, fmt.Errorf("failed to decode Google user info: %w", err)
	}

	if userInfo.Subject == "" || userInfo.Email == "" {
		return nil, errors.New("google user info missing required fields")
	}

	return &userInfo, nil
}

func findOrCreateGoogleUser(ctx context.Context, db *sql.DB, googleSub, email string) (int64, string, error) {
	normalizedEmail := normalizeEmail(email)

	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		return 0, "", fmt.Errorf("failed to start transaction: %w", err)
	}
	defer func() {
		_ = tx.Rollback()
	}()

	var userID int64
	var storedEmail string
	err = tx.QueryRowContext(ctx, `SELECT id, email FROM users WHERE google_sub = $1`, googleSub).Scan(&userID, &storedEmail)
	if err == nil {
		if err := tx.Commit(); err != nil {
			return 0, "", fmt.Errorf("failed to commit transaction: %w", err)
		}

		return userID, storedEmail, nil
	}
	if !errors.Is(err, sql.ErrNoRows) {
		return 0, "", fmt.Errorf("failed to look up user by Google subject: %w", err)
	}

	var existingGoogleSub sql.NullString
	err = tx.QueryRowContext(ctx, `SELECT id, email, google_sub FROM users WHERE email = $1`, normalizedEmail).Scan(&userID, &storedEmail, &existingGoogleSub)
	switch {
	case err == nil:
		if existingGoogleSub.Valid && existingGoogleSub.String != "" && existingGoogleSub.String != googleSub {
			return 0, "", errGoogleAccountConflict
		}

		if !existingGoogleSub.Valid || existingGoogleSub.String == "" {
			if !googleAutoLinkByEmailEnabled() {
				return 0, "", errGoogleAutoLinkDisabled
			}

			if _, err := tx.ExecContext(ctx, `UPDATE users SET google_sub = $1, updated_at = NOW() WHERE id = $2`, googleSub, userID); err != nil {
				return 0, "", fmt.Errorf("failed to link Google account: %w", err)
			}
		}

		if err := tx.Commit(); err != nil {
			return 0, "", fmt.Errorf("failed to commit transaction: %w", err)
		}

		return userID, storedEmail, nil

	case errors.Is(err, sql.ErrNoRows):
		err = tx.QueryRowContext(
			ctx,
			`INSERT INTO users (email, password_hash, google_sub) VALUES ($1, NULL, $2) RETURNING id, email`,
			normalizedEmail,
			googleSub,
		).Scan(&userID, &storedEmail)
		if err != nil {
			if strings.Contains(err.Error(), "duplicate key") {
				return 0, "", errors.New("account changed concurrently; retry login")
			}
			return 0, "", fmt.Errorf("failed to create user for Google account: %w", err)
		}

		if err := tx.Commit(); err != nil {
			return 0, "", fmt.Errorf("failed to commit transaction: %w", err)
		}

		return userID, storedEmail, nil

	default:
		return 0, "", fmt.Errorf("failed to look up user by email: %w", err)
	}
}

func main() {
	if err := godotenv.Load(); err != nil {
		log.Println("No .env file found, using environment variables")
	}

	db, err := database.Connect()
	if err != nil {
		log.Fatalf("Failed to connect to database: %v", err)
	}
	defer db.Close()

	if jwtSecret() == "" {
		log.Fatal("JWT_SECRET is required")
	}

	if err := database.ValidateRequiredSchema(db); err != nil {
		log.Fatalf("Database schema validation failed: %v", err)
	}

	r := gin.New()
	r.Use(gin.Recovery())
	r.Use(gin.LoggerWithFormatter(func(param gin.LogFormatterParams) string {
		path := param.Path
		if idx := strings.Index(path, "?"); idx >= 0 {
			path = path[:idx]
		}

		return fmt.Sprintf("[GIN] %s | %3d | %13v | %15s | %-7s %q\n",
			param.TimeStamp.Format("2006/01/02 - 15:04:05"),
			param.StatusCode,
			param.Latency,
			param.ClientIP,
			param.Method,
			path,
		)
	}))

	r.GET("/health", func(c *gin.Context) {
		if err := db.Ping(); err != nil {
			c.JSON(http.StatusServiceUnavailable, gin.H{
				"status": "unhealthy",
				"db":     "disconnected",
			})
			return
		}
		c.JSON(http.StatusOK, gin.H{
			"status": "healthy",
			"db":     "connected",
		})
	})

	r.POST("/register", func(c *gin.Context) {
		var req registerRequest
		if err := c.ShouldBindJSON(&req); err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"error": "invalid request payload"})
			return
		}

		email := normalizeEmail(req.Email)
		hash, err := bcrypt.GenerateFromPassword([]byte(req.Password), bcrypt.DefaultCost)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to process password"})
			return
		}

		var userID int64
		insertQuery := `INSERT INTO users (email, password_hash) VALUES ($1, $2) RETURNING id`
		if err := db.QueryRow(insertQuery, email, string(hash)).Scan(&userID); err != nil {
			if strings.Contains(err.Error(), "duplicate key") {
				c.JSON(http.StatusConflict, gin.H{"error": "email already registered"})
				return
			}

			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to create user"})
			return
		}

		token, err := createToken(userID, email)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to generate token"})
			return
		}

		c.JSON(http.StatusCreated, gin.H{
			"token": token,
			"user": gin.H{
				"id":    userID,
				"email": email,
			},
		})
	})

	r.POST("/login", func(c *gin.Context) {
		var req loginRequest
		if err := c.ShouldBindJSON(&req); err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"error": "invalid request payload"})
			return
		}

		email := normalizeEmail(req.Email)

		var userID int64
		var passwordHash sql.NullString
		selectQuery := `SELECT id, password_hash FROM users WHERE email = $1`
		err := db.QueryRow(selectQuery, email).Scan(&userID, &passwordHash)
		if err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				c.JSON(http.StatusUnauthorized, gin.H{"error": "invalid credentials"})
				return
			}

			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to authenticate user"})
			return
		}

		if !passwordHash.Valid || passwordHash.String == "" {
			c.JSON(http.StatusUnauthorized, gin.H{"error": "invalid credentials"})
			return
		}

		if err := bcrypt.CompareHashAndPassword([]byte(passwordHash.String), []byte(req.Password)); err != nil {
			c.JSON(http.StatusUnauthorized, gin.H{"error": "invalid credentials"})
			return
		}

		token, err := createToken(userID, email)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to generate token"})
			return
		}

		c.JSON(http.StatusOK, gin.H{
			"token": token,
			"user": gin.H{
				"id":    userID,
				"email": email,
			},
		})
	})

	r.GET("/auth/google/login", func(c *gin.Context) {
		if !googleOAuthConfigured() {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "google oauth is not configured"})
			return
		}

		state, err := newOAuthState()
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to initialize oauth state"})
			return
		}

		setOAuthStateCookie(c, state)
		authURL := buildGoogleOAuthURL(state)

		if strings.EqualFold(c.Query("mode"), "json") {
			c.JSON(http.StatusOK, gin.H{"auth_url": authURL})
			return
		}

		c.Redirect(http.StatusTemporaryRedirect, authURL)
	})

	r.GET("/auth/google/callback", func(c *gin.Context) {
		if !googleOAuthConfigured() {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "google oauth is not configured"})
			return
		}

		if oauthErr := strings.TrimSpace(c.Query("error")); oauthErr != "" {
			description := strings.TrimSpace(c.Query("error_description"))
			if description == "" {
				description = "google authentication failed"
			}
			c.JSON(http.StatusBadRequest, gin.H{"error": description, "oauth_error": oauthErr})
			return
		}

		code := strings.TrimSpace(c.Query("code"))
		state := strings.TrimSpace(c.Query("state"))
		if code == "" || state == "" {
			if strings.Contains(strings.ToLower(c.GetHeader("Accept")), "text/html") {
				c.Redirect(http.StatusTemporaryRedirect, "/auth/google/login")
				return
			}

			c.JSON(http.StatusBadRequest, gin.H{"error": "missing oauth callback parameters; start oauth using /auth/google/login"})
			return
		}

		stateCookie, err := c.Cookie(googleOAuthStateCookieName)
		if err != nil || stateCookie == "" || stateCookie != state {
			c.JSON(http.StatusUnauthorized, gin.H{"error": "invalid oauth state"})
			return
		}
		clearOAuthStateCookie(c)

		tokenResp, err := exchangeGoogleCodeForToken(c.Request.Context(), code)
		if err != nil {
			c.JSON(http.StatusUnauthorized, gin.H{"error": "failed to exchange oauth code"})
			return
		}

		userInfo, err := fetchGoogleUserInfo(c.Request.Context(), tokenResp.AccessToken)
		if err != nil {
			c.JSON(http.StatusUnauthorized, gin.H{"error": "failed to fetch google profile"})
			return
		}

		if !userInfo.EmailVerified {
			c.JSON(http.StatusUnauthorized, gin.H{"error": "google email is not verified"})
			return
		}

		userID, email, err := findOrCreateGoogleUser(c.Request.Context(), db, userInfo.Subject, userInfo.Email)
		if err != nil {
			if errors.Is(err, errGoogleAutoLinkDisabled) {
				c.JSON(http.StatusConflict, gin.H{"error": "an account with this email already exists; Google auto-link is disabled"})
				return
			}

			if errors.Is(err, errGoogleAccountConflict) {
				c.JSON(http.StatusConflict, gin.H{"error": "google account is linked to a different user"})
				return
			}

			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to process google account"})
			return
		}

		token, err := createToken(userID, email)
		if err != nil {
			c.JSON(http.StatusInternalServerError, gin.H{"error": "failed to generate token"})
			return
		}

		c.JSON(http.StatusOK, gin.H{
			"token": token,
			"user": gin.H{
				"id":    userID,
				"email": email,
			},
		})
	})

	port := os.Getenv("PORT")
	if port == "" {
		port = "8080"
	}

	log.Printf("Server starting on :%s", port)
	if err := r.Run(":" + port); err != nil {
		log.Fatalf("Failed to start server: %v", err)
	}
}
