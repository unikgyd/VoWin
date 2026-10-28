package main

import (
	"bytes"
	"crypto/md5"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/cookiejar"
	"os"
	"os/exec"
	"path/filepath"
	"reflect"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

// Explicit opt-in only. Creates one temporary internal-only account, tests SIP
// REGISTER (no INVITE/MESSAGE), then removes that account and its credentials.
func TestLiveAdminTemporaryExtensionAndRegistration(t *testing.T) {
	if os.Getenv("PBX_ADMIN_LIVE") != "1" {
		t.Skip("explicit private PBX opt-in required")
	}
	base, url := os.Getenv("PBX_ADMIN_BASE"), os.Getenv("PBX_ADMIN_URL")
	if base == "" || url == "" {
		t.Fatal("explicit PBX base and URL required")
	}
	key, err := os.ReadFile(base + "/admin-access.local.txt")
	if err != nil {
		t.Fatal("private key unavailable")
	}
	jar, _ := cookiejar.New(nil)
	client := &http.Client{Jar: jar, Timeout: 35 * time.Second}
	csrf := ""
	call := func(method, path string, body any, out any) error {
		var b []byte
		if body != nil {
			b, _ = json.Marshal(body)
		}
		req, e := http.NewRequest(method, url+"/api/"+path, bytes.NewReader(b))
		if e != nil {
			return e
		}
		req.Header.Set("Content-Type", "application/json")
		req.Header.Set("X-CSRF-Token", csrf)
		res, e := client.Do(req)
		if e != nil {
			return e
		}
		defer res.Body.Close()
		data, _ := io.ReadAll(io.LimitReader(res.Body, 1<<20))
		if res.StatusCode != 200 {
			var detail map[string]string
			_ = json.Unmarshal(data, &detail)
			return fmt.Errorf("management %s: HTTP %d (%s)", path, res.StatusCode, detail["error"])
		}
		if out != nil {
			return json.Unmarshal(data, out)
		}
		return nil
	}
	var auth struct {
		CSRF string `json:"csrf"`
	}
	if err = call("POST", "login", map[string]string{"key": strings.TrimSpace(string(key))}, &auth); err != nil {
		t.Fatal(err)
	}
	csrf = auth.CSRF
	var before snapshot
	if err = call("GET", "extensions", nil, &before); err != nil {
		t.Fatal(err)
	}
	used := map[string]bool{}
	for _, row := range before.Extensions {
		used[row.Number] = true
	}
	number := ""
	for n := 9900; n < 9999; n++ {
		candidate := strconv.Itoa(n)
		if !used[candidate] && candidate != before.IncomingTarget {
			number = candidate
			break
		}
	}
	if number == "" {
		t.Fatal("no safe temporary number")
	}
	password := randomSecret()
	rows := append([]extension(nil), before.Extensions...)
	rows = append(rows, extension{Number: number, Name: "Temporary verification", Enabled: true, Ring: false, MaxContacts: 1, Password: password})
	var after snapshot
	if err = call("PUT", "extensions", map[string]any{"version": before.Version, "extensions": rows}, &after); err != nil {
		t.Fatal(err)
	}
	defer func() {
		var current snapshot
		if e := call("GET", "extensions", nil, &current); e != nil {
			t.Error("cannot read for cleanup")
			return
		}
		kept := []extension{}
		for _, row := range current.Extensions {
			if row.Number != number {
				kept = append(kept, row)
			}
		}
		if e := call("PUT", "extensions", map[string]any{"version": current.Version, "extensions": kept}, nil); e != nil {
			t.Error("temporary account cleanup failed:", e)
		}
	}()
	if len(after.Extensions) != len(before.Extensions)+1 {
		t.Fatal("temporary account not visible")
	}
	endpoint, err := exec.Command("docker", "exec", "vowin-asterisk", "asterisk", "-rx", "pjsip show endpoint "+number).CombinedOutput()
	if err != nil || !regexp.MustCompile(`(?m)^\s*context\s*:\s*from-internal-phones\s*$`).Match(endpoint) {
		t.Fatal("temporary endpoint did not receive the restricted call context")
	}
	for _, row := range after.Extensions {
		if row.Number == number && (row.AllowOutgoing || row.AllowMessages || row.Ring) {
			t.Fatal("temporary account has external permissions")
		}
	}
	unregister := registerLiveTest(t, number, password, 5060)
	defer unregister()
	// A temporary contact must appear in the admin status even before the first
	// 30-second OPTIONS cycle changes NonQual/Unavail to Avail.
	var s status
	if err = call("GET", "status", nil, &s); err != nil {
		t.Fatal(err)
	}
	seen := false
	for _, c := range s.Contacts {
		if c.Number == number {
			seen = true
		}
	}
	if !seen {
		t.Fatal("REGISTER contact missing from management status")
	}
	// Verify exact secret retrieval without printing either existing or new secrets.
	var connection struct{ Username, Password string }
	if err = call("POST", "connection", map[string]string{"number": number}, &connection); err != nil || connection.Username != number || connection.Password != password {
		t.Fatal("connection metadata mismatch")
	}
	unregister()
	for i := range after.Extensions {
		if after.Extensions[i].Number == number {
			after.Extensions[i].Enabled = false
		}
	}
	if err = call("PUT", "extensions", map[string]any{"version": after.Version, "extensions": after.Extensions}, &after); err != nil {
		t.Fatal("disable failed:", err)
	}
	t.Log("Temporary internal-only account: created, credential retrieval, SIP REGISTER, contact status and disable passed; cleanup follows. No calls or SMS.")
}

func TestLiveFinalDeploymentPreservesOriginalAccountsAndCredentials(t *testing.T) {
	if os.Getenv("PBX_ADMIN_LIVE") != "1" {
		t.Skip("explicit private PBX opt-in required")
	}
	base, backup := os.Getenv("PBX_ADMIN_BASE"), os.Getenv("PBX_ADMIN_ORIGINAL_BACKUP")
	if base == "" || backup == "" {
		t.Fatal("explicit original backup path required")
	}
	decode := func(path string) map[string]any {
		b, e := os.ReadFile(path)
		if e != nil {
			t.Fatal("private state unavailable")
		}
		var v map[string]any
		if json.Unmarshal(b, &v) != nil {
			t.Fatal("invalid private state")
		}
		return v
	}
	original, now := decode(filepath.Join(backup, "credentials.json")), decode(filepath.Join(base, "credentials.json"))
	if !reflect.DeepEqual(original, now) {
		t.Fatal("existing secrets changed or temporary credentials retained")
	}
	oldConfig, newConfig := decode(filepath.Join(backup, "deployment.local.json")), decode(filepath.Join(base, "deployment.local.json"))
	delete(newConfig, "ExtensionOptions")
	if !reflect.DeepEqual(oldConfig, newConfig) {
		t.Fatal("existing addresses, trunk, accounts or dial rules changed")
	}
	for _, name := range []string{"rtp.conf", "modules.conf", "manager.conf", "http.conf", "asterisk.conf"} {
		a, e1 := os.ReadFile(filepath.Join(backup, "config", name))
		b, e2 := os.ReadFile(filepath.Join(base, "config", name))
		if e1 != nil || e2 != nil || !bytes.Equal(a, b) {
			t.Fatal("unrelated PBX config changed:", name)
		}
	}
	t.Log("Original extension list, existing passwords, trunk, routes and unrelated PBX config preserved; temporary account removed.")
}

func registerLiveTest(t *testing.T, user, password string, port int) func() {
	t.Helper()
	conn, err := net.DialUDP("udp", nil, &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: port})
	if err != nil {
		t.Fatal(err)
	}
	local := conn.LocalAddr().String()
	id := randomSecret()
	uri := "sip:127.0.0.1"
	nonce, realm := "", ""
	seq := 1
	hash := func(s string) string { h := md5.Sum([]byte(s)); return hex.EncodeToString(h[:]) }
	send := func(expires int) string {
		auth := ""
		if nonce != "" {
			cnonce := randomSecret()
			response := hash(hash(user+":"+realm+":"+password) + ":" + nonce + ":00000001:" + cnonce + ":auth:" + hash("REGISTER:"+uri))
			auth = fmt.Sprintf("Authorization: Digest username=\"%s\", realm=\"%s\", nonce=\"%s\", uri=\"%s\", response=\"%s\", algorithm=MD5, qop=auth, nc=00000001, cnonce=\"%s\"\r\n", user, realm, nonce, uri, response, cnonce)
		}
		packet := fmt.Sprintf("REGISTER %s SIP/2.0\r\nVia: SIP/2.0/UDP %s;branch=z9hG4bK-%s;rport\r\nFrom: <sip:%s@127.0.0.1>;tag=synthetic\r\nTo: <sip:%s@127.0.0.1>\r\nCall-ID: %s\r\nCSeq: %d REGISTER\r\nMax-Forwards: 70\r\nContact: <sip:%s@%s>\r\nExpires: %d\r\n%sContent-Length: 0\r\n\r\n", uri, local, randomSecret(), user, user, id, seq, user, local, expires, auth)
		seq++
		_, e := conn.Write([]byte(packet))
		if e != nil {
			t.Fatal("REGISTER write failed")
		}
		_ = conn.SetReadDeadline(time.Now().Add(3 * time.Second))
		b := make([]byte, 16384)
		for {
			n, e := conn.Read(b)
			if e != nil {
				t.Fatal("REGISTER response timed out")
			}
			text := string(b[:n])
			if strings.HasPrefix(text, "OPTIONS ") {
				continue
			}
			return text
		}
	}
	challenge := send(120)
	if !strings.HasPrefix(challenge, "SIP/2.0 401") {
		t.Fatal("no REGISTER auth challenge")
	}
	for key, target := range map[string]*string{"nonce": &nonce, "realm": &realm} {
		match := regexp.MustCompile(key + `="([^"]+)"`).FindStringSubmatch(challenge)
		if len(match) != 2 {
			t.Fatal("invalid auth challenge")
		}
		*target = match[1]
	}
	response := send(120)
	if !strings.HasPrefix(response, "SIP/2.0 200") {
		t.Fatal("authenticated REGISTER failed")
	}
	var once sync.Once
	return func() {
		once.Do(func() {
			defer conn.Close()
			response := send(0)
			if !strings.HasPrefix(response, "SIP/2.0 200") {
				t.Error("temporary REGISTER cleanup failed")
			}
		})
	}
}
