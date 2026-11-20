package tun

import (
	"testing"
	"unsafe"

	"golang.org/x/sys/windows"
)

func TestNativeRouteLayoutAndLookupKey(t *testing.T) {
	var row nativeRoute
	if unsafe.Sizeof(row) != 104 || unsafe.Offsetof(row.Destination) != 12 ||
		unsafe.Offsetof(row.NextHop) != 44 || unsafe.Offsetof(row.Metric) != 84 {
		t.Fatal("unexpected Windows route ABI")
	}
	initialize.Call(uintptr(unsafe.Pointer(&row)))
	if row.ValidLifetime != ^uint32(0) || row.PreferredLifetime != ^uint32(0) || row.Metric != ^uint32(0) {
		t.Fatalf("unexpected Windows defaults: %+v", row)
	}
	table := nativeRouteTable{luid: 42}
	v4 := table.key(routeIntent{Prefix: capturePrefixes[1]})
	v6 := table.key(routeIntent{Prefix: capturePrefixes[3]})
	if v4.LUID != 42 || v4.InterfaceIndex != 0 || v4.Destination.Length != 1 ||
		v4.Destination.Address.Family != windows.AF_INET || v4.Destination.Address.Data[0] != 128 ||
		v4.NextHop != (routeSocketAddress{Family: windows.AF_INET}) {
		t.Fatalf("unexpected IPv4 route: %+v", v4)
	}
	if v6.Destination.Address.Family != windows.AF_INET6 || v6.Destination.Address.Data[4] != 128 ||
		v6.NextHop != (routeSocketAddress{Family: windows.AF_INET6}) {
		t.Fatalf("unexpected IPv6 route: %+v", v6)
	}
}

func TestJournalRequiresProtectedAdministratorOwnership(t *testing.T) {
	for _, test := range []struct {
		sddl  string
		valid bool
	}{
		{journalACL, true},
		{"O:BAD:(A;;KA;;;SY)(A;;KA;;;BA)", false},
		{"O:BUD:P(A;;KA;;;SY)(A;;KA;;;BA)", false},
		{"O:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KA;;;BU)", false},
	} {
		descriptor, err := windows.SecurityDescriptorFromString(test.sddl)
		if err != nil {
			t.Fatal(err)
		}
		if err := validateJournalDescriptor(descriptor); (err == nil) != test.valid {
			t.Fatalf("descriptor %q: %v", test.sddl, err)
		}
	}
}
