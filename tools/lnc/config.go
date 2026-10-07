package main

import (
	"errors"
	"flag"
	"strings"
	"time"
)

type Config struct {
	StateDir, Backend, TLSCert, TLSServerName, AdminMacaroon, Relay, RelayTLSCert, Name, Profile, ID string
	TTL                                                                                              time.Duration
}

func parseConfig(command string, args []string) (Config, error) {
	var c Config
	f := flag.NewFlagSet(command, flag.ContinueOnError)
	f.StringVar(&c.StateDir, "state-dir", "./lnc-state", "private session directory")
	f.StringVar(&c.Backend, "backend", "127.0.0.1:10009", "NLightning TLS gRPC host:port")
	f.StringVar(&c.TLSCert, "tls-cert", "", "trusted backend TLS certificate PEM")
	f.StringVar(&c.TLSServerName, "tls-server-name", "", "backend TLS server name override")
	f.StringVar(&c.AdminMacaroon, "admin-macaroon", "", "local admin macaroon file (create/revoke only)")
	f.StringVar(&c.Relay, "relay", "mailbox.terminal.lightning.today:443", "LNC mailbox host:port")
	f.StringVar(&c.RelayTLSCert, "relay-tls-cert", "", "trusted custom relay TLS certificate PEM")
	f.StringVar(&c.Name, "name", "wallet", "session display name")
	f.StringVar(&c.Profile, "profile", "readonly", "readonly or wallet permission profile")
	f.DurationVar(&c.TTL, "ttl", 24*time.Hour, "session lifetime")
	f.StringVar(&c.ID, "id", "", "session ID for revoke")
	if e := f.Parse(args); e != nil {
		return c, e
	}
	if f.NArg() != 0 {
		return c, errors.New("unexpected positional arguments")
	}
	if command != "list" && c.TLSCert == "" {
		return c, errors.New("--tls-cert is required")
	}
	if (command == "create" || command == "revoke") && c.AdminMacaroon == "" {
		return c, errors.New("--admin-macaroon is required")
	}
	if command == "create" {
		if c.TTL <= 0 || c.TTL > 365*24*time.Hour {
			return c, errors.New("--ttl must be positive and at most 365 days")
		}
		if _, e := profileMethods(c.Profile); e != nil {
			return c, e
		}
	}
	if command == "revoke" && !validID(c.ID) {
		return c, errors.New("--id must be a session ID")
	}
	return c, nil
}
func profileMethods(profile string) ([]string, error) {
	methods := strings.Fields(`/verrpc.Versioner/GetVersion /lnrpc.Lightning/GetInfo /lnrpc.Lightning/WalletBalance /lnrpc.Lightning/ChannelBalance /lnrpc.Lightning/ListChannels /lnrpc.Lightning/PendingChannels /lnrpc.Lightning/ClosedChannels /lnrpc.Lightning/ListPeers /lnrpc.Lightning/GetTransactions /lnrpc.Lightning/ListUnspent /lnrpc.Lightning/ListInvoices /lnrpc.Lightning/LookupInvoice /lnrpc.Lightning/ListPayments /lnrpc.Lightning/DecodePayReq /lnrpc.Lightning/QueryRoutes /lnrpc.Lightning/GetNodeInfo /lnrpc.Lightning/GetChanInfo /lnrpc.Lightning/DescribeGraph /lnrpc.Lightning/SubscribeInvoices /lnrpc.Lightning/SubscribeTransactions /lnrpc.Lightning/SubscribePeerEvents /lnrpc.Lightning/SubscribeChannelEvents /lnrpc.Lightning/SubscribeChannelGraph /routerrpc.Router/TrackPaymentV2 /routerrpc.Router/TrackPayments /routerrpc.Router/SubscribeHtlcEvents /invoicesrpc.Invoices/SubscribeSingleInvoice`)
	switch profile {
	case "readonly":
		return methods, nil
	case "wallet":
		return append(methods, strings.Fields(`/lnrpc.Lightning/AddInvoice /lnrpc.Lightning/NewAddress /lnrpc.Lightning/SendCoins /lnrpc.Lightning/SendMany /lnrpc.Lightning/ConnectPeer /lnrpc.Lightning/DisconnectPeer /lnrpc.Lightning/SignMessage /routerrpc.Router/SendPaymentV2 /routerrpc.Router/SendToRouteV2 /invoicesrpc.Invoices/AddHoldInvoice /invoicesrpc.Invoices/SettleInvoice /invoicesrpc.Invoices/CancelInvoice`)...), nil
	default:
		return nil, errors.New("--profile must be readonly or wallet")
	}
}
