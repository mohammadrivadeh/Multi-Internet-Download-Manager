package main

import (
	"encoding/json"
	"fmt"
	"net"
	"os"

	"net-merge/internal/nics"
)

type diagnosticAdapter struct {
	Name   string `json:"name"`
	Index  int    `json:"index"`
	IPv4   string `json:"ipv4"`
	IPv6   string `json:"ipv6"`
	Reason string `json:"reason"`
}

func writeDiagnostics() error {
	eligible, skipped, err := nics.DiscoverNicsDetailed(nil, nil)
	if err != nil {
		return err
	}
	adapters := make([]diagnosticAdapter, 0, len(eligible)+len(skipped))
	for _, adapter := range eligible {
		adapters = append(adapters, diagnosticAdapter{
			Name: adapter.Name, Index: adapter.Index,
			IPv4: addressText(adapter.IP), IPv6: addressText(adapter.IP6),
		})
	}
	for _, adapter := range skipped {
		adapters = append(adapters, diagnosticAdapter{Name: adapter.Name, Reason: adapter.Reason})
	}
	return json.NewEncoder(os.Stdout).Encode(struct {
		Version  int                 `json:"version"`
		Adapters []diagnosticAdapter `json:"adapters"`
	}{Version: 1, Adapters: adapters})
}

func main() {
	if err := writeDiagnostics(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func addressText(ip net.IP) string {
	if ip == nil {
		return ""
	}
	return ip.String()
}
