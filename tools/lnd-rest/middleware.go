package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"time"

	"github.com/gorilla/websocket"
	"google.golang.org/grpc/codes"
)

// writeGatewayError answers in grpc-gateway's error body shape, as the
// gateway itself does: {"code": <gRPC code>, "message": ..., "details": []}.
func writeGatewayError(w http.ResponseWriter, status int, code codes.Code, message string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(map[string]any{"code": int(code), "message": message, "details": []any{}})
}

// limitBody reads a request body up front, bounded in size and time, so a
// slow or oversized upload never reaches the gateway or holds a stream open.
// After the body is read the connection has no read deadline again, so a
// server-streaming response may run for as long as the client keeps it.
// WebSocket upgrades are left alone: their messages are bounded by LND's
// WebSocket proxy (4 MiB) and its handshake has no body.
func limitBody(next http.Handler, maxBytes int64, timeout time.Duration) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if websocket.IsWebSocketUpgrade(r) || r.Body == nil || r.Body == http.NoBody {
			next.ServeHTTP(w, r)
			return
		}
		if r.ContentLength > maxBytes {
			writeGatewayError(w, http.StatusRequestEntityTooLarge, codes.ResourceExhausted, "request body too large")
			return
		}
		rc := http.NewResponseController(w)
		_ = rc.SetReadDeadline(time.Now().Add(timeout))
		body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxBytes))
		_ = rc.SetReadDeadline(time.Time{})
		if err != nil {
			var tooLarge *http.MaxBytesError
			if errors.As(err, &tooLarge) {
				writeGatewayError(w, http.StatusRequestEntityTooLarge, codes.ResourceExhausted, "request body too large")
				return
			}
			writeGatewayError(w, http.StatusBadRequest, codes.InvalidArgument, "could not read the request body")
			return
		}
		r.Body = io.NopCloser(bytes.NewReader(body))
		r.ContentLength = int64(len(body))
		next.ServeHTTP(w, r)
	})
}

// allowCORS is LND's allowCORS (rpcserver.go, v0.21.4-beta, MIT licence):
// without origins CORS is off and the handler is returned unchanged.
func allowCORS(handler http.Handler, origins []string) http.Handler {
	allowHeaders := "Access-Control-Allow-Headers"
	allowMethods := "Access-Control-Allow-Methods"
	allowOrigin := "Access-Control-Allow-Origin"

	if len(origins) == 0 {
		return handler
	}

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		origin := r.Header.Get("Origin")

		// Skip everything if the browser doesn't send the Origin field.
		if origin == "" {
			handler.ServeHTTP(w, r)
			return
		}

		w.Header().Set(allowHeaders, "Content-Type, Accept, Grpc-Metadata-Macaroon")
		w.Header().Set(allowMethods, "GET, POST, DELETE")

		for _, allowedOrigin := range origins {
			if allowedOrigin == "*" || origin == allowedOrigin {
				w.Header().Set(allowOrigin, origin)
				break
			}
		}

		// A pre-flight request only needs the headers.
		if r.Method == "OPTIONS" {
			return
		}

		handler.ServeHTTP(w, r)
	})
}

// logRequests logs each request's method, path, status and duration. It never
// logs headers (the macaroon travels in one), query strings or bodies.
func logRequests(next http.Handler, logf func(string, ...any)) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		sw := &statusWriter{ResponseWriter: w}
		next.ServeHTTP(sw, r)
		status := sw.status
		switch {
		case sw.hijacked:
			logf("%s %s -> websocket closed after %s", r.Method, r.URL.Path, time.Since(start).Round(time.Millisecond))
			return
		case status == 0:
			status = http.StatusOK
		}
		logf("%s %s -> %d in %s", r.Method, r.URL.Path, status, time.Since(start).Round(time.Millisecond))
	})
}

// statusWriter records the response status. It keeps the Flusher and
// Hijacker the gateway's streams and the WebSocket upgrade need.
type statusWriter struct {
	http.ResponseWriter
	status   int
	hijacked bool
}

func (s *statusWriter) WriteHeader(code int) {
	if s.status == 0 {
		s.status = code
	}
	s.ResponseWriter.WriteHeader(code)
}

func (s *statusWriter) Write(b []byte) (int, error) {
	if s.status == 0 {
		s.status = http.StatusOK
	}
	return s.ResponseWriter.Write(b)
}

func (s *statusWriter) Flush() {
	if f, ok := s.ResponseWriter.(http.Flusher); ok {
		f.Flush()
	}
}

func (s *statusWriter) Hijack() (net.Conn, *bufio.ReadWriter, error) {
	h, ok := s.ResponseWriter.(http.Hijacker)
	if !ok {
		return nil, nil, errors.New("response writer cannot be hijacked")
	}
	s.hijacked = true
	return h.Hijack()
}

func (s *statusWriter) Unwrap() http.ResponseWriter { return s.ResponseWriter }
