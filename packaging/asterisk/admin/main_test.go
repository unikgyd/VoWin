package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

type fakeRunner struct {
	m           *manager
	active      string
	failReload  bool
	badEndpoint bool
	generated   int
	reloads     int
}

func (f *fakeRunner) run(_ context.Context, name string, args ...string) ([]byte, error) {
	if name == "python3" {
		f.generated++
		stage := args[len(args)-1]
		b, _ := os.ReadFile(filepath.Join(stage, "deployment.local.json"))
		var d struct {
			Extensions       []string
			ExtensionOptions map[string]struct{ Enabled bool }
		}
		_ = json.Unmarshal(b, &d)
		text := ""
		for _, n := range d.Extensions {
			if d.ExtensionOptions[n].Enabled {
				text += "[" + n + "]\ntype=endpoint\n"
			}
		}
		_ = os.MkdirAll(filepath.Join(stage, "config"), 0700)
		_ = os.WriteFile(filepath.Join(stage, "config", "pjsip.conf"), []byte(text), 0600)
		_ = os.WriteFile(filepath.Join(stage, "config", "extensions.conf"), []byte("[synthetic]\n"), 0600)
		return []byte("generated"), nil
	}
	cmd := args[len(args)-1]
	if cmd == "core show channels count" {
		if f.active != "" {
			return []byte(f.active), nil
		}
		return []byte("0 active channels\n0 active calls\n"), nil
	}
	if cmd == "module reload res_pjsip.so" || cmd == "dialplan reload" {
		f.reloads++
		if f.failReload && f.reloads == 1 {
			return nil, errors.New("synthetic reload failure")
		}
		return []byte("reloaded"), nil
	}
	if strings.HasPrefix(cmd, "pjsip show endpoint ") {
		n := strings.TrimPrefix(cmd, "pjsip show endpoint ")
		b, _ := os.ReadFile(filepath.Join(f.m.base, "config", "pjsip.conf"))
		if strings.Contains(string(b), "["+n+"]") && !f.badEndpoint {
			return []byte(" Endpoint:  " + n + "/Synthetic\n"), nil
		}
		return []byte("Unable to find object"), nil
	}
	if cmd == "pjsip show contacts" {
		return []byte("Contact: 1001/sip:1001@127.0.0.1:25000 abc Avail 12.1\nContact: vowin-aor/sip:gateway@127.0.0.1:5062 def NonQual nan\n"), nil
	}
	return nil, errors.New("unexpected command")
}
func testManager(t *testing.T) (*manager, *fakeRunner) {
	t.Helper()
	base := t.TempDir()
	_ = os.MkdirAll(filepath.Join(base, "config"), 0700)
	d := map[string]any{"Extensions": []string{"1001", "1002"}, "ServerIp": "127.0.0.1", "TrunkUsername": "hostims", "IncomingTarget": "600", "EchoExtension": "700", "MessagesEnabled": true}
	if err := writeJSON(filepath.Join(base, "deployment.local.json"), d); err != nil {
		t.Fatal(err)
	}
	if err := writeJSON(filepath.Join(base, "credentials.json"), map[string]string{"hostims": strings.Repeat("T", 32), "1001": strings.Repeat("A", 32), "1002": strings.Repeat("B", 32)}); err != nil {
		t.Fatal(err)
	}
	_ = os.WriteFile(filepath.Join(base, "config", "pjsip.conf"), []byte("[1001]\ntype=endpoint\n[1001]\ntype=aor\nmax_contacts=2\n[1002]\ntype=endpoint\n[1002]\ntype=aor\nmax_contacts=2\n"), 0600)
	_ = os.WriteFile(filepath.Join(base, "config", "extensions.conf"), []byte("[original]\n"), 0600)
	m := &manager{base: base, generator: filepath.Join(base, "configure.py"), container: "synthetic-asterisk"}
	f := &fakeRunner{m: m}
	m.run = f.run
	return m, f
}
func TestLoadRedactsPasswordsAndImportsExistingContactLimits(t *testing.T) {
	m, _ := testManager(t)
	s, e := m.load()
	if e != nil {
		t.Fatal(e)
	}
	b, _ := json.Marshal(s.view)
	if bytes.Contains(b, []byte(strings.Repeat("A", 16))) || len(s.view.Extensions) != 2 || s.view.Extensions[0].MaxContacts != 2 || !s.view.Extensions[0].AllowOutgoing || !s.view.Extensions[0].AllowMessages {
		t.Fatal("legacy import or redaction failed")
	}
}
func TestApplyCreatesBackupsAndPreservesOtherAccountsAndTrunk(t *testing.T) {
	m, _ := testManager(t)
	s, _ := m.load()
	rows := append(s.view.Extensions, extension{Number: "1003", Name: "Synthetic", Enabled: true, Ring: true, MaxContacts: 2, Password: strings.Repeat("C", 32)})
	v, e := m.apply(context.Background(), s.view.Version, rows)
	if e != nil {
		t.Fatal(e)
	}
	if v.Version == s.view.Version || len(v.Extensions) != 3 {
		t.Fatal("not applied")
	}
	next, _ := m.load()
	if next.passwords["1001"] != s.passwords["1001"] || next.passwords["hostims"] != s.passwords["hostims"] {
		t.Fatal("existing secrets changed")
	}
	paths, _ := filepath.Glob(filepath.Join(m.base, "admin-backup-*", "original", "credentials.json"))
	if len(paths) != 1 {
		t.Fatal("no private backup")
	}
	original, _ := os.ReadFile(paths[0])
	if !bytes.Contains(original, []byte(s.passwords["1001"])) {
		t.Fatal("backup invalid")
	}
}
func TestConflictAndActiveCallsDoNotRenderOrWrite(t *testing.T) {
	for _, active := range []string{"", "1 active channels\n1 active calls\n", "10 active channels\n10 active calls\n"} {
		t.Run(fmt.Sprint(active), func(t *testing.T) {
			m, f := testManager(t)
			s, _ := m.load()
			version := s.view.Version
			if active == "" {
				version = "stale"
			} else {
				f.active = active
			}
			if _, e := m.apply(context.Background(), version, s.view.Extensions); e == nil {
				t.Fatal("unsafe apply accepted")
			}
			if f.generated != 0 {
				t.Fatal("rendered before conflict/busy check")
			}
			after, _ := m.load()
			if after.view.Version != s.view.Version {
				t.Fatal("files changed")
			}
		})
	}
}
func TestReloadAndEndpointFailureRollBackExactFiles(t *testing.T) {
	for _, endpoint := range []bool{false, true} {
		t.Run(fmt.Sprint(endpoint), func(t *testing.T) {
			m, f := testManager(t)
			s, _ := m.load()
			f.failReload = !endpoint
			f.badEndpoint = endpoint
			rows := append([]extension(nil), s.view.Extensions...)
			rows[0].Name = "changed"
			if _, e := m.apply(context.Background(), s.view.Version, rows); e == nil {
				t.Fatal("failure not surfaced")
			}
			after, _ := m.load()
			if after.view.Version != s.view.Version {
				t.Fatal("rollback did not restore exact bytes")
			}
		})
	}
}
func TestDeletePrunesOnlyDeletedCredential(t *testing.T) {
	m, _ := testManager(t)
	s, _ := m.load()
	if _, e := m.apply(context.Background(), s.view.Version, s.view.Extensions[:1]); e != nil {
		t.Fatal(e)
	}
	after, _ := m.load()
	if _, ok := after.passwords["1002"]; ok {
		t.Fatal("deleted credential retained")
	}
	if after.passwords["hostims"] != s.passwords["hostims"] {
		t.Fatal("trunk removed")
	}
}
func TestInvalidAccountInputsFailBeforeGenerator(t *testing.T) {
	for _, mutate := range []func(*extension){func(e *extension) { e.Number = "1001\n[attack]" }, func(e *extension) { e.Number = "600" }, func(e *extension) { e.Name = "x\npassword=y" }, func(e *extension) { e.Number = "1003"; e.Password = "" }, func(e *extension) { e.MaxContacts = 100 }, func(e *extension) { e.Password = "short" }} {
		m, f := testManager(t)
		s, _ := m.load()
		rows := append([]extension(nil), s.view.Extensions...)
		mutate(&rows[0])
		if _, err := m.apply(context.Background(), s.view.Version, rows); err == nil || f.generated != 0 {
			t.Fatal("invalid input reached generator")
		}
	}
}
func TestStatusOnlyReturnsExtensionContacts(t *testing.T) {
	m, _ := testManager(t)
	s := m.runtimeStatus(context.Background())
	if !s.Running || len(s.Contacts) != 1 || s.Contacts[0].Number != "1001" || s.Contacts[0].State != "Avail" {
		t.Fatal(s)
	}
}
func request(s *webServer, method, path, body string, cookie *http.Cookie, csrf string) *httptest.ResponseRecorder {
	r := httptest.NewRequest(method, "http://127.0.0.1:9088"+path, strings.NewReader(body))
	r.RemoteAddr = "127.0.0.1:12345"
	r.Header.Set("Content-Type", "application/json")
	r.Header.Set("X-CSRF-Token", csrf)
	if cookie != nil {
		r.AddCookie(cookie)
	}
	w := httptest.NewRecorder()
	s.handler(w, r)
	return w
}
func TestHTTPAuthCSRFAndCredentialRedaction(t *testing.T) {
	m, _ := testManager(t)
	s := &webServer{m: m, key: strings.Repeat("K", 43), sessions: map[string]session{}, failures: map[string][]time.Time{}}
	if w := request(s, "GET", "/api/extensions", "", nil, ""); w.Code != 401 {
		t.Fatal(w.Code)
	}
	w := request(s, "POST", "/api/login", `{"key":"`+s.key+`"}`, nil, "")
	if w.Code != 200 {
		t.Fatal(w.Code)
	}
	cookie := w.Result().Cookies()[0]
	var auth map[string]string
	_ = json.Unmarshal(w.Body.Bytes(), &auth)
	if !cookie.HttpOnly || cookie.SameSite != http.SameSiteStrictMode {
		t.Fatal("unsafe cookie")
	}
	if w = request(s, "POST", "/api/connection", `{"number":"1001"}`, cookie, ""); w.Code != 403 {
		t.Fatal("CSRF missing")
	}
	if w = request(s, "GET", "/api/extensions", "", cookie, ""); w.Code != 200 || strings.Contains(w.Body.String(), strings.Repeat("A", 16)) {
		t.Fatal("list leaks secrets")
	}
	if w = request(s, "POST", "/api/connection", `{"number":"1001"}`, cookie, auth["csrf"]); w.Code != 200 || !strings.Contains(w.Body.String(), strings.Repeat("A", 32)) {
		t.Fatal("explicit connection retrieval failed")
	}
	r := httptest.NewRequest("POST", "http://127.0.0.1:9088/api/logout", strings.NewReader(`{}`))
	r.RemoteAddr = "127.0.0.1:12345"
	r.AddCookie(cookie)
	r.Header.Set("Content-Type", "application/json")
	r.Header.Set("X-CSRF-Token", auth["csrf"])
	r.Header.Set("Origin", "https://evil.invalid")
	bad := httptest.NewRecorder()
	s.handler(bad, r)
	if bad.Code != 403 {
		t.Fatal("cross-origin mutation accepted")
	}
	if w = request(s, "POST", "/api/logout", `{}`, cookie, auth["csrf"]); w.Code != 200 {
		t.Fatal(w.Code)
	}
	if w = request(s, "GET", "/api/extensions", "", cookie, ""); w.Code != 401 {
		t.Fatal("logout retained session")
	}
}
func TestHTTPRejectsPublicPeerAndUnsafeStaticPaths(t *testing.T) {
	m, _ := testManager(t)
	s := &webServer{m: m}
	r := httptest.NewRequest("GET", "http://127.0.0.1/", nil)
	r.RemoteAddr = "8.8.8.8:1234"
	w := httptest.NewRecorder()
	s.handler(w, r)
	if w.Code != 403 {
		t.Fatal("public peer admitted")
	}
	w = request(s, "GET", "/credentials.json", "", nil, "")
	if w.Code != 404 {
		t.Fatal("private path exposed")
	}
	w = request(s, "GET", "/", "", nil, "")
	if w.Code != 200 || !strings.Contains(w.Header().Get("Content-Security-Policy"), "script-src 'self'") {
		t.Fatal("missing UI or CSP")
	}
}
