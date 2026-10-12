// Command nltg-lnd-rest serves LND's REST API (grpc-gateway) in front of
// NLightning's LND-compatible gRPC listener, so REST-only LND clients such as
// Ride The Lightning work against an NLightning node.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"os/signal"
	"syscall"
)

// version is the sidecar's version; the LND API it serves is lndAPIVersion.
const (
	version       = "0.1.0"
	lndAPIVersion = "v0.21.4-beta"
)

func main() {
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	if err := run(ctx, os.Args[1:], os.Stdout, os.Stderr); err != nil {
		if errors.Is(err, flag.ErrHelp) {
			return
		}
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func run(ctx context.Context, args []string, stdout, stderr io.Writer) error {
	c, err := parseConfig(args, stderr)
	if err != nil {
		return err
	}
	if c.Version {
		fmt.Fprintf(stdout, "nltg-lnd-rest %s (LND REST API %s)\n", version, lndAPIVersion)
		return nil
	}
	s, err := Start(c)
	if err != nil {
		return err
	}
	if s.CertPath != "" {
		s.logf("listener certificate: %s", s.CertPath)
	}
	select {
	case err := <-s.Done():
		return err
	case <-ctx.Done():
	}
	s.logf("shutting down")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), c.ShutdownTimeout)
	defer cancel()
	_ = s.Shutdown(shutdownCtx)
	return <-s.Done()
}
