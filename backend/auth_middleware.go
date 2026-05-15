package main

import (
	"net/http"
	"strings"

	"github.com/gin-gonic/gin"
	"github.com/golang-jwt/jwt/v5"
)

const (
	userIDContextKey    = "user_id"
	userEmailContextKey = "user_email"
)

func authMiddleware() gin.HandlerFunc {
	return func(c *gin.Context) {
		authorization := strings.TrimSpace(c.GetHeader("Authorization"))
		if authorization == "" {
			c.AbortWithStatusJSON(http.StatusUnauthorized, gin.H{"error": "missing authorization header"})
			return
		}

		parts := strings.SplitN(authorization, " ", 2)
		if len(parts) != 2 || !strings.EqualFold(parts[0], "Bearer") || strings.TrimSpace(parts[1]) == "" {
			c.AbortWithStatusJSON(http.StatusUnauthorized, gin.H{"error": "invalid authorization header"})
			return
		}

		claims := &jwtClaims{}
		token, err := jwt.ParseWithClaims(
			parts[1],
			claims,
			func(_ *jwt.Token) (any, error) {
				return []byte(jwtSecret()), nil
			},
			jwt.WithValidMethods([]string{jwt.SigningMethodHS256.Alg()}),
		)
		if err != nil || token == nil || !token.Valid {
			c.AbortWithStatusJSON(http.StatusUnauthorized, gin.H{"error": "invalid token"})
			return
		}

		if claims.UserID <= 0 {
			c.AbortWithStatusJSON(http.StatusUnauthorized, gin.H{"error": "invalid token claims"})
			return
		}

		c.Set(userIDContextKey, claims.UserID)
		c.Set(userEmailContextKey, claims.Email)
		c.Next()
	}
}

func currentUserID(c *gin.Context) (int64, bool) {
	val, ok := c.Get(userIDContextKey)
	if !ok {
		return 0, false
	}

	userID, ok := val.(int64)
	return userID, ok && userID > 0
}
