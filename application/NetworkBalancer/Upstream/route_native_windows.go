package tun

import (
	"crypto/rand"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

const (
	journalKey   = `SOFTWARE\MultiInternetNetworkBalancer`
	journalValue = "CaptureRouteJournal"
	journalACL   = "O:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)"
	netMgmt      = 3
)

var (
	ipHelper    = windows.NewLazySystemDLL("iphlpapi.dll")
	initialize  = ipHelper.NewProc("InitializeIpForwardEntry")
	getRoute    = ipHelper.NewProc("GetIpForwardEntry2")
	createRoute = ipHelper.NewProc("CreateIpForwardEntry2")
	deleteRoute = ipHelper.NewProc("DeleteIpForwardEntry2")
	advapi      = windows.NewLazySystemDLL("advapi32.dll")
	createKey   = advapi.NewProc("RegCreateKeyExW")
	flushKey    = advapi.NewProc("RegFlushKey")
)

type machineRouteJournal struct{}

func (machineRouteJournal) load() (routeRecord, error) {
	empty := routeRecord{Version: 1, Adapter: captureGUID}
	key, err := registry.OpenKey(registry.LOCAL_MACHINE, journalKey,
		registry.QUERY_VALUE|registry.WOW64_64KEY|windows.READ_CONTROL)
	if errors.Is(err, registry.ErrNotExist) {
		return empty, nil
	}
	if err != nil {
		return routeRecord{}, err
	}
	defer key.Close()
	if err := verifyJournalSecurity(key); err != nil {
		return routeRecord{}, err
	}
	data, _, err := key.GetBinaryValue(journalValue)
	if errors.Is(err, registry.ErrNotExist) {
		return empty, nil
	}
	if err != nil {
		return routeRecord{}, err
	}
	var record routeRecord
	if len(data) > 4096 || json.Unmarshal(data, &record) != nil {
		return routeRecord{}, errors.New("capture route journal is unreadable")
	}
	return record, nil
}

func (machineRouteJournal) save(record routeRecord) error {
	if err := validateRecord(record); err != nil {
		return err
	}
	data, err := json.Marshal(record)
	if err != nil {
		return err
	}
	// Nonvolatile machine key with a protected DACL for administrators and SYSTEM.
	sd, err := windows.SecurityDescriptorFromString(journalACL)
	if err != nil {
		return err
	}
	attributes := windows.SecurityAttributes{
		Length: uint32(unsafe.Sizeof(windows.SecurityAttributes{})), SecurityDescriptor: sd,
	}
	name, err := windows.UTF16PtrFromString(journalKey)
	if err != nil {
		return err
	}
	var key registry.Key
	result, _, _ := createKey.Call(uintptr(registry.LOCAL_MACHINE), uintptr(unsafe.Pointer(name)),
		0, 0, 0, registry.SET_VALUE|registry.WOW64_64KEY|windows.READ_CONTROL, uintptr(unsafe.Pointer(&attributes)),
		uintptr(unsafe.Pointer(&key)), 0)
	if result != 0 {
		return syscall.Errno(result)
	}
	defer key.Close()
	if err := verifyJournalSecurity(key); err != nil {
		return err
	}
	if err := key.SetBinaryValue(journalValue, data); err != nil {
		return err
	}
	result, _, _ = flushKey.Call(uintptr(key))
	if result != 0 {
		return fmt.Errorf("flush capture route journal: %w", syscall.Errno(result))
	}
	return nil
}

func verifyJournalSecurity(key registry.Key) error {
	const flags = windows.OWNER_SECURITY_INFORMATION | windows.DACL_SECURITY_INFORMATION
	actual, err := windows.GetSecurityInfo(windows.Handle(key), windows.SE_REGISTRY_KEY, flags)
	if err != nil {
		return err
	}
	return validateJournalDescriptor(actual)
}

func validateJournalDescriptor(actual *windows.SECURITY_DESCRIPTOR) error {
	expected, err := windows.SecurityDescriptorFromString(journalACL)
	if err != nil {
		return err
	}
	owner, _, err := actual.Owner()
	if err != nil {
		return err
	}
	control, _, err := actual.Control()
	if err != nil {
		return err
	}
	// Windows may include auto-inheritance flags in the SDDL header. Compare the ACEs
	// separately, and require a protected DACL and an administrators-group owner.
	actualACEs, expectedACEs := actual.String(), expected.String()
	_, actualACEs, actualOK := strings.Cut(actualACEs, "(")
	_, expectedACEs, expectedOK := strings.Cut(expectedACEs, "(")
	if owner == nil || !owner.IsWellKnown(windows.WinBuiltinAdministratorsSid) ||
		control&windows.SE_DACL_PROTECTED == 0 || !actualOK || !expectedOK || actualACEs != expectedACEs {
		return errors.New("capture route journal has an unexpected registry ACL")
	}
	return nil
}

// Windows amd64 layout of SOCKADDR_INET, IP_ADDRESS_PREFIX and MIB_IPFORWARD_ROW2.
type routeSocketAddress struct {
	Family uint16
	Port   uint16
	Data   [24]byte
}

type routePrefix struct {
	Address routeSocketAddress
	Length  uint8
	_       [3]byte
}

type nativeRoute struct {
	LUID                 uint64
	InterfaceIndex       uint32
	Destination          routePrefix
	NextHop              routeSocketAddress
	SitePrefixLength     uint8
	_                    [3]byte
	ValidLifetime        uint32
	PreferredLifetime    uint32
	Metric               uint32
	Protocol             uint32
	Loopback             uint8
	AutoconfigureAddress uint8
	Publish              uint8
	Immortal             uint8
	Age                  uint32
	Origin               uint32
}

type nativeRouteTable struct{ luid uint64 }

func (table nativeRouteTable) key(intent routeIntent) nativeRoute {
	row := nativeRoute{LUID: table.luid}
	row.Destination.Length = uint8(intent.Prefix.Bits())
	address := intent.Prefix.Addr()
	if address.Is4() {
		row.Destination.Address.Family = windows.AF_INET
		bytes := address.As4()
		copy(row.Destination.Address.Data[:], bytes[:])
	} else {
		row.Destination.Address.Family = windows.AF_INET6
		bytes := address.As16()
		copy(row.Destination.Address.Data[4:], bytes[:])
	}
	row.NextHop.Family = row.Destination.Address.Family
	return row
}

func (table nativeRouteTable) inspect(intent routeIntent) (routeOwnership, error) {
	present, err := table.adapterPresent()
	if err != nil {
		return routeConflict, err
	}
	if !present {
		return routeMissing, nil
	}
	row := table.key(intent)
	result, _, _ := getRoute.Call(uintptr(unsafe.Pointer(&row)))
	switch syscall.Errno(result) {
	case windows.ERROR_NOT_FOUND:
		return routeMissing, nil
	case 0:
		key := table.key(intent)
		if row.LUID == key.LUID && row.Destination == key.Destination && row.NextHop == key.NextHop &&
			row.Metric == intent.Metric && row.Protocol == netMgmt {
			return routeOwned, nil
		}
		return routeConflict, nil
	default:
		return routeConflict, syscall.Errno(result)
	}
}

func (table nativeRouteTable) adapterPresent() (bool, error) {
	current, _, err := captureAdapter()
	if err != nil || current == 0 {
		return false, err
	}
	if current != table.luid {
		return false, errors.New("capture adapter identity changed during route recovery")
	}
	return true, nil
}

func (table nativeRouteTable) add(intent routeIntent) error {
	present, err := table.adapterPresent()
	if err != nil {
		return err
	}
	if !present {
		return errors.New("capture adapter disappeared before route creation")
	}
	row := nativeRoute{}
	initialize.Call(uintptr(unsafe.Pointer(&row)))
	key := table.key(intent)
	row.LUID, row.Destination, row.NextHop = key.LUID, key.Destination, key.NextHop
	row.SitePrefixLength, row.Metric, row.Protocol = 0, intent.Metric, netMgmt
	row.Loopback, row.AutoconfigureAddress, row.Publish, row.Immortal = 0, 0, 0, 1
	result, _, _ := createRoute.Call(uintptr(unsafe.Pointer(&row)))
	if syscall.Errno(result) == windows.ERROR_OBJECT_ALREADY_EXISTS {
		return errRouteAlreadyExists
	}
	if result != 0 {
		return syscall.Errno(result)
	}
	return nil
}

func (table nativeRouteTable) remove(intent routeIntent) error {
	state, err := table.inspect(intent)
	if err != nil || state == routeMissing {
		return err
	}
	if state != routeOwned {
		return errors.New("route ownership changed before deletion")
	}
	row := table.key(intent)
	result, _, _ := deleteRoute.Call(uintptr(unsafe.Pointer(&row)))
	if result != 0 && syscall.Errno(result) != windows.ERROR_NOT_FOUND {
		return syscall.Errno(result)
	}
	return nil
}

func captureAdapter() (luid uint64, index uint32, err error) {
	size := uint32(16 * 1024)
	for range 4 {
		buffer := make([]byte, size)
		first := (*windows.IpAdapterAddresses)(unsafe.Pointer(&buffer[0]))
		err = windows.GetAdaptersAddresses(windows.AF_UNSPEC, windows.GAA_FLAG_INCLUDE_ALL_INTERFACES, 0, first, &size)
		if errors.Is(err, windows.ERROR_BUFFER_OVERFLOW) {
			continue
		}
		if errors.Is(err, windows.ERROR_NO_DATA) {
			return 0, 0, nil
		}
		if err != nil {
			return 0, 0, err
		}
		for row := first; row != nil; row = row.Next {
			if strings.EqualFold(windows.BytePtrToString(row.AdapterName), captureGUID) {
				if row.Luid == 0 {
					return 0, 0, errors.New("capture adapter has no LUID")
				}
				return row.Luid, row.IfIndex, nil
			}
		}
		return 0, 0, nil
	}
	return 0, 0, err
}

func captureAdapterAtIndex(index int) (uint64, error) {
	luid, current, err := captureAdapter()
	if err != nil {
		return 0, err
	}
	if luid == 0 || int(current) != index {
		return 0, errors.New("capture adapter identity did not match the requested Wintun GUID")
	}
	return luid, nil
}

func addOwnedCaptureRoutes(index int) error {
	luid, err := captureAdapterAtIndex(index)
	if err != nil {
		return err
	}
	var token [2]byte
	if _, err := rand.Read(token[:]); err != nil {
		return err
	}
	metric := 1024 + uint32(binary.LittleEndian.Uint16(token[:]))%64512
	return installCaptureRoutes(machineRouteJournal{}, nativeRouteTable{luid: luid}, metric)
}

// RecoverCaptureRoutes reconciles only the desktop adapter's previously journaled routes.
func RecoverCaptureRoutes() error {
	luid, _, err := captureAdapter()
	if err != nil {
		return err
	}
	return reconcileCaptureRoutes(machineRouteJournal{}, nativeRouteTable{luid: luid})
}

// CaptureAdapterPresent uses the same identity as the recovery journal even if the adapter was renamed.
func CaptureAdapterPresent() (bool, error) {
	luid, _, err := captureAdapter()
	return luid != 0, err
}
