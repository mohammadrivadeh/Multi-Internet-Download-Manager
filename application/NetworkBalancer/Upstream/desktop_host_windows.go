package main

import (
	"fmt"
	"io"
	"net"
	"os"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"time"

	"golang.org/x/sys/windows"
	"net-merge/internal/engine"
	"net-merge/internal/nics"
	"net-merge/internal/platform"
	"net-merge/internal/tun"
)

const adapterName = "Multi-Internet Balancer"

var pipeName = regexp.MustCompile(`^multi-internet-balancer-[0-9a-f]{32}$`)

func main() {
	if len(os.Args) < 4 || len(os.Args) > 131 || !pipeName.MatchString(os.Args[1]) || !platform.IsAdmin() {
		os.Exit(1)
	}
	parent, err := strconv.ParseUint(os.Args[2], 10, 32)
	if err != nil || parent == 0 {
		os.Exit(1)
	}
	pipe, err := openControlPipe(os.Args[1], uint32(parent))
	if err != nil {
		os.Exit(1)
	}
	defer pipe.Close()
	if err := runCapture(pipe, os.Args[3:]); err != nil {
		message := strings.Join(strings.Fields(err.Error()), " ")
		if len(message) > 1500 {
			message = message[:1500]
		}
		fmt.Fprintf(pipe, "ERROR %s\n", message)
		pipe.Close()
		os.Exit(1)
	}
}

func openControlPipe(name string, parent uint32) (*os.File, error) {
	path, err := windows.UTF16PtrFromString(`\\.\pipe\` + name)
	if err != nil {
		return nil, err
	}
	handle, err := windows.CreateFile(path, windows.GENERIC_READ|windows.GENERIC_WRITE, 0, nil,
		windows.OPEN_EXISTING, windows.FILE_FLAG_OVERLAPPED|windows.SECURITY_SQOS_PRESENT|windows.SECURITY_IDENTIFICATION, 0)
	if err != nil {
		return nil, err
	}
	var pid uint32
	if err := windows.GetNamedPipeServerProcessId(handle, &pid); err != nil || pid != parent {
		windows.CloseHandle(handle)
		return nil, fmt.Errorf("unexpected control pipe server")
	}
	return os.NewFile(uintptr(handle), name), nil
}

func runCapture(pipe io.ReadWriter, selections []string) error {
	var authorize [1]byte
	if _, err := io.ReadFull(pipe, authorize[:]); err != nil || authorize[0] != 'A' {
		return fmt.Errorf("desktop did not authorize this capture session")
	}
	selected, err := parseSelections(selections)
	if err != nil {
		return err
	}
	stop := make(chan struct{})
	go func() {
		// One command or pipe closure ends this session. Never restart automatically.
		var command [1]byte
		pipe.Read(command[:])
		close(stop)
	}()
	mutexName, _ := windows.UTF16PtrFromString(`Global\MultiInternetNetworkBalancerCapture`)
	mutex, err := windows.CreateMutex(nil, false, mutexName)
	if mutex != 0 {
		defer windows.CloseHandle(mutex)
	}
	if err != nil {
		return fmt.Errorf("could not acquire the capture singleton: %w", err)
	}
	if err := tun.RecoverCaptureRoutes(); err != nil {
		return fmt.Errorf("route recovery is incomplete; capture did not start: %w", err)
	}
	present, err := tun.CaptureAdapterPresent()
	if err != nil {
		return err
	}
	if present {
		return fmt.Errorf("the previous capture adapter still exists; inspect it before starting")
	}
	if err := platform.CheckDriver(); err != nil {
		return err
	}
	interfaces, err := net.Interfaces()
	if err != nil {
		return err
	}
	for _, adapter := range interfaces {
		if strings.EqualFold(adapter.Name, adapterName) {
			return fmt.Errorf("the capture adapter already exists; inspect the previous session before starting")
		}
	}
	adapters, err := eligibleAdapters(selected)
	if err != nil {
		return err
	}
	scheduler := engine.NewScheduler(adapters)
	scheduler.SetAuto(true)
	balancer := engine.NewBalancer(scheduler)
	balancer.SetV6Mode(engine.V6Auto)
	if stopped(stop) {
		_, err := fmt.Fprintln(pipe, "STOPPED")
		return err
	}

	capture := tun.New(balancer)
	if err := capture.On(); err != nil {
		if cleanup := waitAdapterRemoval(); cleanup != nil {
			return fmt.Errorf("could not start capture: %v; %w", err, cleanup)
		}
		return fmt.Errorf("could not start capture: %w", err)
	}
	defer capture.Close()
	if stopped(stop) {
		return stopCapture(capture, pipe)
	}
	if _, err := fmt.Fprintln(pipe, "READY"); err != nil {
		return err
	}
	go scheduler.RunProbes(stop)
	tick := time.NewTicker(time.Second)
	defer tick.Stop()
	pollAdapters := time.NewTicker(5 * time.Second)
	defer pollAdapters.Stop()
	last := time.Now()
	for {
		select {
		case <-stop:
			return stopCapture(capture, pipe)
		case now := <-tick.C:
			scheduler.Tick(now.Sub(last))
			last = now
		case <-pollAdapters.C:
			current, discoveryError := selectedAdapters(selected)
			if discoveryError != nil || len(current) == 0 || checkStrongHost(current) != nil {
				capture.Close()
				if cleanup := waitAdapterRemoval(); cleanup != nil {
					return fmt.Errorf("adapter configuration changed; %w", cleanup)
				}
				return fmt.Errorf("no safe selected adapter remains or Windows could not verify it; capture was stopped")
			}
			scheduler.Update(current)
		}
	}
}

func stopCapture(capture *tun.Tun, pipe io.Writer) error {
	capture.Close()
	if err := waitAdapterRemoval(); err != nil {
		return err
	}
	_, err := fmt.Fprintln(pipe, "STOPPED")
	return err
}

func waitAdapterRemoval() error {
	if err := tun.RecoverCaptureRoutes(); err != nil {
		return fmt.Errorf("capture route cleanup is unconfirmed: %w", err)
	}
	for deadline := time.Now().Add(5 * time.Second); time.Now().Before(deadline); {
		present, err := tun.CaptureAdapterPresent()
		if err != nil {
			return fmt.Errorf("could not verify that the capture adapter was removed: %w", err)
		}
		if !present {
			return nil
		}
		time.Sleep(100 * time.Millisecond)
	}
	return fmt.Errorf("the capture adapter is still present; route cleanup is unconfirmed")
}

func stopped(stop <-chan struct{}) bool {
	select {
	case <-stop:
		return true
	default:
		return false
	}
}

func parseSelections(selections []string) (map[int]string, error) {
	selected := make(map[int]string, len(selections))
	for _, selection := range selections {
		indexText, name, found := strings.Cut(selection, ":")
		index, err := strconv.Atoi(indexText)
		if !found || err != nil || index <= 0 || name == "" || selected[index] != "" {
			return nil, fmt.Errorf("invalid or duplicate adapter selection")
		}
		selected[index] = name
	}
	if len(selected) == 0 {
		return nil, fmt.Errorf("select at least one eligible adapter")
	}
	return selected, nil
}

func eligibleAdapters(selected map[int]string) ([]nics.Nic, error) {
	adapters, err := selectedAdapters(selected)
	if err != nil {
		return nil, err
	}
	if len(adapters) != len(selected) {
		return nil, fmt.Errorf("a selected adapter is no longer eligible; scan again")
	}
	if err := checkStrongHost(adapters); err != nil {
		return nil, err
	}
	return adapters, nil
}

func selectedAdapters(selected map[int]string) ([]nics.Nic, error) {
	adapters, err := nics.DiscoverNics(nil, nil)
	if err != nil {
		return nil, err
	}
	adapters = slices.DeleteFunc(adapters, func(adapter nics.Nic) bool {
		return strings.EqualFold(adapter.Name, adapterName) || selected[adapter.Index] != adapter.Name
	})
	return adapters, nil
}

func checkStrongHost(adapters []nics.Nic) error {
	for _, adapter := range adapters {
		for _, family := range []struct {
			address net.IP
			number  uint16
		}{{adapter.IP, windows.AF_INET}, {adapter.IP6, windows.AF_INET6}} {
			if family.address == nil {
				continue
			}
			row := windows.MibIpInterfaceRow{Family: family.number, InterfaceIndex: uint32(adapter.Index)}
			if err := windows.GetIpInterfaceEntry(&row); err != nil {
				return fmt.Errorf("verify egress for adapter %q: %w", adapter.Name, err)
			}
			if row.WeakHostSend != 0 || row.ForwardingEnabled != 0 {
				return fmt.Errorf("adapter %q has weak-host send or forwarding enabled", adapter.Name)
			}
		}
	}
	return nil
}
