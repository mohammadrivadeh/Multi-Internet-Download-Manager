package main

import (
	"bytes"
	"fmt"
	"io"
	"net"
	"os"
	"strings"
	"testing"
	"time"

	"golang.org/x/sys/windows"
	"net-merge/internal/nics"
)

func TestCaptureRejectsMissingAuthorization(t *testing.T) {
	for _, input := range []string{"", "S"} {
		err := runCapture(bytes.NewBufferString(input), nil)
		if err == nil || !strings.Contains(err.Error(), "did not authorize") {
			t.Fatalf("input %q returned %v", input, err)
		}
	}
}

func TestCapturedFamiliesRequireAtLeastOneCompatibleSelectedAdapter(t *testing.T) {
	ipv4 := nics.Nic{IP: net.IPv4(192, 0, 2, 5)}
	ipv6 := nics.Nic{IP6: net.ParseIP("2001:db8::5")}
	dual := nics.Nic{IP: ipv4.IP, IP6: ipv6.IP6}
	captured := availableFamilies([]nics.Nic{ipv4, ipv6})
	if captured != (addressFamilies{ipv4: true, ipv6: true}) {
		t.Fatalf("mixed adapter families = %+v", captured)
	}
	for _, check := range []struct {
		active []nics.Nic
		want   bool
	}{
		{[]nics.Nic{ipv4, ipv6}, true},
		{[]nics.Nic{dual}, true},
		{[]nics.Nic{ipv4}, false},
		{[]nics.Nic{ipv6}, false},
		{nil, false},
	} {
		if got := availableFamilies(check.active).covers(captured); got != check.want {
			t.Errorf("active set %+v covers capture = %v, want %v", check.active, got, check.want)
		}
	}
}

func TestParseSelectedAdapters(t *testing.T) {
	selected, err := parseSelections([]string{"3:Wi-Fi 2", "12:USB: Ethernet"})
	if err != nil || len(selected) != 2 || selected[3] != "Wi-Fi 2" || selected[12] != "USB: Ethernet" {
		t.Fatalf("selection = %v, err = %v", selected, err)
	}
	for _, input := range [][]string{nil, {"0:Ethernet"}, {"1:"}, {"3:Wi-Fi", "3:USB"}} {
		if _, err := parseSelections(input); err == nil {
			t.Fatalf("accepted invalid selection %v", input)
		}
	}
}

func TestControlPipeAllowsStatusWhileReadingStop(t *testing.T) {
	name := fmt.Sprintf("multi-internet-balancer-test-%d", os.Getpid())
	path, err := windows.UTF16PtrFromString(`\\.\pipe\` + name)
	if err != nil {
		t.Fatal(err)
	}
	handle, err := windows.CreateNamedPipe(path, windows.PIPE_ACCESS_DUPLEX,
		windows.PIPE_TYPE_BYTE, 1, 1024, 1024, 0, nil)
	if err != nil {
		t.Fatal(err)
	}
	server := os.NewFile(uintptr(handle), name)
	defer server.Close()
	client, err := openControlPipe(name, uint32(os.Getpid()))
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	if err := windows.ConnectNamedPipe(handle, nil); err != nil && err != windows.ERROR_PIPE_CONNECTED {
		t.Fatal(err)
	}
	if err := client.SetDeadline(time.Now().Add(3 * time.Second)); err != nil {
		t.Fatal(err)
	}
	stop := make(chan error, 1)
	go func() {
		var received [1]byte
		_, err := io.ReadFull(client, received[:])
		if err == nil && received[0] != 'S' {
			err = fmt.Errorf("unexpected command %q", received[0])
		}
		stop <- err
	}()
	if err := writeControl(client, "READY\n"); err != nil {
		t.Fatal(err)
	}
	var status [6]byte
	if _, err := io.ReadFull(server, status[:]); err != nil || string(status[:]) != "READY\n" {
		t.Fatalf("status = %q, err = %v", status, err)
	}
	if _, err := server.Write([]byte{'S'}); err != nil {
		t.Fatal(err)
	}
	if err := <-stop; err != nil {
		t.Fatal(err)
	}
}
