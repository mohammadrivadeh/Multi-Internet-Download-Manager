package tun

import (
	"errors"
	"fmt"
	"net/netip"
	"slices"
)

const captureGUID = "{a056ea70-e16d-4a57-9acb-65c2105d2db4}"

var errRouteAlreadyExists = errors.New("capture route already exists")

var capturePrefixes = []netip.Prefix{
	netip.MustParsePrefix("0.0.0.0/1"), netip.MustParsePrefix("128.0.0.0/1"),
	netip.MustParsePrefix("::/1"), netip.MustParsePrefix("8000::/1"),
}

type routeIntent struct {
	Prefix netip.Prefix
	Metric uint32
}

type routeRecord struct {
	Version int
	Adapter string
	Routes  []routeIntent
}

type routeJournal interface {
	load() (routeRecord, error)
	save(routeRecord) error
}

type routeOwnership int

const (
	routeMissing routeOwnership = iota
	routeOwned
	routeConflict
)

type captureRouteTable interface {
	inspect(routeIntent) (routeOwnership, error)
	add(routeIntent) error
	remove(routeIntent) error
}

func validateRecord(record routeRecord) error {
	if record.Version != 1 || record.Adapter != captureGUID || len(record.Routes) > len(capturePrefixes) {
		return errors.New("invalid capture route journal")
	}
	seen := make(map[netip.Prefix]bool, len(record.Routes))
	for _, route := range record.Routes {
		if !slices.Contains(capturePrefixes, route.Prefix) || seen[route.Prefix] ||
			route.Metric < 1024 || route.Metric > 65535 {
			return errors.New("invalid route intent in capture journal")
		}
		seen[route.Prefix] = true
	}
	return nil
}

func installCaptureRoutes(journal routeJournal, table captureRouteTable, metric uint32) error {
	record, err := journal.load()
	if err != nil {
		return err
	}
	if err := validateRecord(record); err != nil {
		return err
	}
	if len(record.Routes) != 0 {
		return errors.New("capture route recovery is pending")
	}
	if metric < 1024 || metric > 65535 {
		return errors.New("invalid capture route metric")
	}
	for _, prefix := range capturePrefixes {
		route := routeIntent{Prefix: prefix, Metric: metric}
		state, err := table.inspect(route)
		if err != nil {
			return err
		}
		if state != routeMissing {
			return fmt.Errorf("a route for %v already exists on the capture adapter", prefix)
		}
		record.Routes = append(record.Routes, route)
		if err := journal.save(record); err != nil {
			return err
		}
		if err := table.add(route); err != nil {
			if errors.Is(err, errRouteAlreadyExists) {
				// The API explicitly reported a collision. Never adopt the other writer's route.
				record.Routes = record.Routes[:len(record.Routes)-1]
				err = errors.Join(err, journal.save(record))
			}
			return fmt.Errorf("add capture route %v: %w", prefix, err)
		}
		state, err = table.inspect(route)
		if err != nil {
			return fmt.Errorf("verify capture route %v: %w", prefix, err)
		}
		if state != routeOwned {
			return fmt.Errorf("capture route %v does not match the recorded ownership", prefix)
		}
	}
	return nil
}

func reconcileCaptureRoutes(journal routeJournal, table captureRouteTable) error {
	record, err := journal.load()
	if err != nil {
		return err
	}
	if err := validateRecord(record); err != nil {
		return err
	}
	var unresolved error
	for i := len(record.Routes) - 1; i >= 0; i-- {
		route := record.Routes[i]
		state, err := table.inspect(route)
		if err == nil {
			switch state {
			case routeMissing:
			case routeOwned:
				err = table.remove(route)
				if err == nil {
					state, err = table.inspect(route)
					if err == nil && state != routeMissing {
						err = errors.New("route still exists after removal")
					}
				}
			default:
				err = errors.New("route ownership does not match the journal")
			}
		}
		if err != nil {
			unresolved = errors.Join(unresolved, fmt.Errorf("recover capture route %v: %w", route.Prefix, err))
			continue
		}
		record.Routes = slices.Delete(record.Routes, i, i+1)
		if err := journal.save(record); err != nil {
			return errors.Join(unresolved, err)
		}
	}
	return unresolved
}
