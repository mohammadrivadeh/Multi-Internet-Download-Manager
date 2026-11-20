package tun

import (
	"errors"
	"fmt"
	"net/netip"
	"slices"
	"testing"
)

var injectedFailure = errors.New("injected interruption")

type fakeJournal struct {
	record routeRecord
	saveAt int
	saves  int
	after  bool
}

func (j *fakeJournal) load() (routeRecord, error) {
	copy := j.record
	copy.Routes = slices.Clone(copy.Routes)
	return copy, nil
}

func (j *fakeJournal) save(record routeRecord) error {
	j.saves++
	if j.saves == j.saveAt && !j.after {
		return injectedFailure
	}
	record.Routes = slices.Clone(record.Routes)
	j.record = record
	if j.saves == j.saveAt {
		return injectedFailure
	}
	return nil
}

type fakeRouteTable struct {
	routes        map[netip.Prefix]uint32
	addAt         int
	adds          int
	removeAt      int
	removes       int
	after         bool
	inspectionErr error
	collisionAt   int
}

func (table *fakeRouteTable) inspect(route routeIntent) (routeOwnership, error) {
	if table.inspectionErr != nil {
		return routeConflict, table.inspectionErr
	}
	metric, present := table.routes[route.Prefix]
	if !present {
		return routeMissing, nil
	}
	if metric != route.Metric {
		return routeConflict, nil
	}
	return routeOwned, nil
}

func (table *fakeRouteTable) add(route routeIntent) error {
	table.adds++
	if table.adds == table.collisionAt {
		table.routes[route.Prefix] = route.Metric
		return errRouteAlreadyExists
	}
	if table.adds == table.addAt && !table.after {
		return injectedFailure
	}
	table.routes[route.Prefix] = route.Metric
	if table.adds == table.addAt {
		return injectedFailure
	}
	return nil
}

func (table *fakeRouteTable) remove(route routeIntent) error {
	table.removes++
	if table.removes == table.removeAt && !table.after {
		return injectedFailure
	}
	delete(table.routes, route.Prefix)
	if table.removes == table.removeAt {
		return injectedFailure
	}
	return nil
}

func newRouteFixture() (*fakeJournal, *fakeRouteTable) {
	return &fakeJournal{record: routeRecord{Version: 1, Adapter: captureGUID}},
		&fakeRouteTable{routes: make(map[netip.Prefix]uint32)}
}

func TestRecoverInterruptedRouteInstallation(t *testing.T) {
	for _, operation := range []string{"save", "add"} {
		for offset := 1; offset <= len(capturePrefixes); offset++ {
			for _, after := range []bool{false, true} {
				name := fmt.Sprintf("%s/%d/after=%t", operation, offset, after)
				t.Run(name, func(t *testing.T) {
					j, table := newRouteFixture()
					j.after, table.after = after, after
					if operation == "save" {
						j.saveAt = offset
					} else {
						table.addAt = offset
					}
					if err := installCaptureRoutes(j, table, 1234); !errors.Is(err, injectedFailure) {
						t.Fatalf("install error = %v", err)
					}
					j.saveAt = 0
					verifyRecovered(t, j, table)
				})
			}
		}
	}
}

func TestRetryInterruptedRouteRecovery(t *testing.T) {
	for _, operation := range []string{"save", "delete"} {
		for offset := 1; offset <= len(capturePrefixes); offset++ {
			for _, after := range []bool{false, true} {
				name := fmt.Sprintf("%s/%d/after=%t", operation, offset, after)
				t.Run(name, func(t *testing.T) {
					j, table := newRouteFixture()
					if err := installCaptureRoutes(j, table, 1234); err != nil {
						t.Fatal(err)
					}
					j.after, table.after = after, after
					if operation == "save" {
						j.saveAt = j.saves + offset
					} else {
						table.removeAt = offset
					}
					if err := reconcileCaptureRoutes(j, table); !errors.Is(err, injectedFailure) {
						t.Fatalf("recover error = %v", err)
					}
					j.saveAt, table.removeAt = 0, 0
					verifyRecovered(t, j, table)
					verifyRecovered(t, j, table)
				})
			}
		}
	}
}

func verifyRecovered(t *testing.T, j *fakeJournal, table *fakeRouteTable) {
	t.Helper()
	if err := reconcileCaptureRoutes(j, table); err != nil {
		t.Fatal(err)
	}
	if len(j.record.Routes) != 0 || len(table.routes) != 0 {
		t.Fatalf("journal: %v, routes: %v", j.record.Routes, table.routes)
	}
}

func TestRecoveryRetainsConflictAndLeavesForeignRoutes(t *testing.T) {
	j, table := newRouteFixture()
	if err := installCaptureRoutes(j, table, 1234); err != nil {
		t.Fatal(err)
	}
	foreign := capturePrefixes[1]
	table.routes[foreign] = 4567
	if err := reconcileCaptureRoutes(j, table); err == nil {
		t.Fatal("expected an ownership conflict")
	}
	if len(table.routes) != 1 || table.routes[foreign] != 4567 ||
		len(j.record.Routes) != 1 || j.record.Routes[0].Prefix != foreign {
		t.Fatalf("journal: %v, routes: %v", j.record.Routes, table.routes)
	}
	if err := installCaptureRoutes(j, table, 2345); err == nil {
		t.Fatal("started with unresolved ownership")
	}
}

func TestRecoveryRefusesInvalidIdentityAndRetainsUninspectableRoutes(t *testing.T) {
	j, table := newRouteFixture()
	if err := installCaptureRoutes(j, table, 1234); err != nil {
		t.Fatal(err)
	}
	j.record.Adapter = "another adapter"
	if err := reconcileCaptureRoutes(j, table); err == nil || table.removes != 0 {
		t.Fatalf("invalid identity: err = %v, deletes = %d", err, table.removes)
	}
	j.record.Adapter = captureGUID
	table.inspectionErr = injectedFailure
	if err := reconcileCaptureRoutes(j, table); !errors.Is(err, injectedFailure) ||
		table.removes != 0 || len(j.record.Routes) != len(capturePrefixes) {
		t.Fatalf("inspection failure: err = %v, deletes = %d, journal = %v", err, table.removes, j.record.Routes)
	}
}

func TestInstallationDoesNotAdoptPreexistingRoute(t *testing.T) {
	j, table := newRouteFixture()
	table.routes[capturePrefixes[0]] = 1234
	if err := installCaptureRoutes(j, table, 1234); err == nil ||
		j.saves != 0 || table.adds != 0 || len(table.routes) != 1 {
		t.Fatalf("preexisting route: err = %v, journal = %v, routes = %v", err, j.record.Routes, table.routes)
	}
}

func TestInstallationDoesNotAdoptRouteFromKnownAddCollision(t *testing.T) {
	j, table := newRouteFixture()
	table.collisionAt = 2
	if err := installCaptureRoutes(j, table, 1234); !errors.Is(err, errRouteAlreadyExists) {
		t.Fatalf("install error = %v", err)
	}
	if err := reconcileCaptureRoutes(j, table); err != nil {
		t.Fatal(err)
	}
	if len(j.record.Routes) != 0 || len(table.routes) != 1 || table.routes[capturePrefixes[1]] != 1234 {
		t.Fatalf("journal: %v, routes: %v", j.record.Routes, table.routes)
	}
}
