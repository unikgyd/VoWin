package main

// Actual Python helper + Go authenticated HTTP integration, entirely within
// Go-owned temporary directories. Docker is a synthetic runner, never invoked.
import (
	"bytes"
	"encoding/json"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

func realAdminV2Fixture(t *testing.T) (*webServer, *http.Cookie, string, *fakeRunner) {
	t.Helper()
	s, cookie, csrf, runner := newAdminV2AuthenticatedLegacy(t)
	generator, err := filepath.Abs("../configure.py")
	if err != nil {
		t.Fatal("cannot locate synthetic helper source")
	}
	s.m.generator = generator
	s.m.v2Store = filepath.Join(t.TempDir(), "private-state")
	input := t.TempDir()
	model := map[string]any{
		"SchemaVersion": 2, "GatewayPlatform": "vowin-windows",
		"ServerIp": "127.77.0.2", "BindAddress": "127.77.0.2", "SipPort": 5076,
		"MessageDomain": "identity.example.invalid", "PbxRealm": "pbx.synthetic", "PbxAlgorithm": "SHA-256",
		"GatewayIp": "127.77.0.3", "GatewayBindAddress": "127.77.0.3", "GatewayPort": 5078,
		"GatewayRealm": "gateway.synthetic", "GatewayAlgorithm": "SHA-256",
		"Extensions": []string{"4201", "4202"}, "ExtensionOptions": map[string]any{
			"4201": map[string]any{"SecretRef": "pbx-first", "Enabled": false, "AllowMessages": true},
			"4202": map[string]any{"SecretRef": "pbx-second", "Enabled": true, "AllowMobileSmsReceive": true},
		},
	}
	private := map[string]string{"pbx-first": "SyntheticFirstPassword_123", "pbx-second": "SyntheticSecondPassword_456"}
	for name, value := range map[string]any{"public.json": model, "private.json": private} {
		body, err := json.Marshal(value)
		if err != nil || os.WriteFile(filepath.Join(input, name), body, 0600) != nil {
			t.Fatal("cannot create owned synthetic source")
		}
	}
	helper := filepath.Join(filepath.Dir(generator), "admin_deployment_v2.py")
	cmd := exec.Command("python3", "-E", "-s", helper, "--action", "init", "--store", s.m.v2Store,
		"--public", filepath.Join(input, "public.json"), "--secrets", filepath.Join(input, "private.json"))
	output, err := cmd.CombinedOutput()
	if err != nil {
		t.Fatal("explicit synthetic v2 initialization failed")
	}
	var result struct {
		OK bool `json:"ok"`
	}
	if json.Unmarshal(output, &result) != nil || !result.OK {
		t.Fatal("initialization did not return a redacted success")
	}
	for _, secret := range private {
		if bytes.Contains(output, []byte(secret)) {
			t.Fatal("initialization exposed a credential")
		}
	}
	return s, cookie, csrf, runner
}

func v2HTTPView(t *testing.T, s *webServer, cookie *http.Cookie, csrf string) map[string]any {
	t.Helper()
	w := request(s, "GET", "/api/v2/extensions", "", cookie, csrf)
	if w.Code != 200 {
		t.Fatalf("reading the actual v2 draft returned HTTP %d", w.Code)
	}
	var view map[string]any
	if json.Unmarshal(w.Body.Bytes(), &view) != nil {
		t.Fatal("invalid public draft JSON")
	}
	for _, secret := range []string{"SyntheticFirstPassword_123", "SyntheticSecondPassword_456", "pbx-first", "pbx-second"} {
		if strings.Contains(w.Body.String(), secret) {
			t.Fatal("public draft leaked a secret or reference")
		}
	}
	return view
}
func v2HTTPPayload(t *testing.T, view map[string]any) map[string]any {
	t.Helper()
	rows := view["extensions"].([]any)
	for _, raw := range rows {
		row := raw.(map[string]any)
		delete(row, "hasPassword")
		row["passwordAction"] = "keep"
	}
	return map[string]any{"revision": view["revision"], "extensions": rows}
}
func v2HTTPJSON(t *testing.T, value any) string {
	t.Helper()
	b, err := json.Marshal(value)
	if err != nil {
		t.Fatal("cannot serialize synthetic request")
	}
	return string(b)
}

func TestAdminV2RealHelperReadSaveConnectionAndIndependentPermissions(t *testing.T) {
	s, cookie, csrf, runner := realAdminV2Fixture(t)
	view := v2HTTPView(t, s, cookie, csrf)
	oldRevision := view["revision"]
	payload := v2HTTPPayload(t, view)
	first := payload["extensions"].([]any)[0].(map[string]any)
	first["allowOutgoing"], first["allowMessages"], first["allowMobileSmsSend"], first["allowMobileSmsReceive"] = true, false, false, true
	w := request(s, "PUT", "/api/v2/extensions", v2HTTPJSON(t, payload), cookie, csrf)
	if w.Code != 200 {
		t.Fatalf("draft save returned HTTP %d", w.Code)
	}
	after := v2HTTPView(t, s, cookie, csrf)
	if after["revision"] == oldRevision || after["state"] != "saved-not-applied" || after["canApply"] != false {
		t.Fatal("saving did not commit a separate unapplied revision")
	}
	row := after["extensions"].([]any)[0].(map[string]any)
	if row["allowOutgoing"] != true || row["allowMessages"] != false || row["allowMobileSmsSend"] != false || row["allowMobileSmsReceive"] != true {
		t.Fatal("independent permissions were merged")
	}
	features := after["features"].(map[string]any)
	if features["calls"] != false || features["carrier"] != false || features["messages"] != false || features["sms"] != false {
		t.Fatal("draft editing enabled a global feature")
	}
	w = request(s, "POST", "/api/v2/connection", v2HTTPJSON(t, map[string]any{"revision": after["revision"], "number": "4201"}), cookie, csrf)
	var connection map[string]any
	if w.Code != 200 || json.Unmarshal(w.Body.Bytes(), &connection) != nil || connection["password"] != "SyntheticFirstPassword_123" || connection["server"] != "127.77.0.2" || connection["state"] != "saved-not-applied" {
		t.Fatal("explicit draft connection information is incorrect")
	}
	if strings.Contains(w.Body.String(), "SyntheticSecondPassword_456") {
		t.Fatal("connection returned another role's secret")
	}
	if runner.generated != 0 || runner.reloads != 0 {
		t.Fatal("draft save generated or reloaded an installed PBX")
	}
}

func TestAdminV2StrictRequestConflictAndLegacyBypassNeverTouchInstalledFiles(t *testing.T) {
	s, cookie, csrf, runner := realAdminV2Fixture(t)
	view := v2HTTPView(t, s, cookie, csrf)
	payload := v2HTTPPayload(t, view)
	body := v2HTTPJSON(t, payload)
	for _, raw := range []string{body + `{}`, strings.Replace(body, `"revision":`, `"Revision":"duplicate","revision":`, 1), strings.Replace(body, `"name":`, `"name":"duplicate","name":`, 1), `{"revision":null,"extensions":[]}`, strings.Repeat(" ", 65537)} {
		if w := request(s, "PUT", "/api/v2/extensions", raw, cookie, csrf); w.Code != 400 {
			t.Fatalf("invalid v2 JSON returned HTTP %d", w.Code)
		}
		if v2HTTPView(t, s, cookie, csrf)["revision"] != view["revision"] {
			t.Fatal("invalid request committed a draft")
		}
	}
	if w := request(s, "PUT", "/api/v2/extensions", body, cookie, csrf); w.Code != 200 {
		t.Fatal("valid control save failed")
	}
	if w := request(s, "PUT", "/api/v2/extensions", body, cookie, csrf); w.Code != 409 {
		t.Fatal("stale save was replayed")
	}
	if w := request(s, "POST", "/api/v2/connection", v2HTTPJSON(t, map[string]any{"revision": view["revision"], "number": "4201"}), cookie, csrf); w.Code != 409 {
		t.Fatal("stale connection metadata was disclosed")
	}
	for _, tc := range []struct{ method, path string }{{"GET", "/api/extensions"}, {"PUT", "/api/extensions"}, {"POST", "/api/connection"}} {
		if w := request(s, tc.method, tc.path, `{}`, cookie, csrf); w.Code != 409 {
			t.Fatal("v2 mode still permits legacy editing or credential lookup")
		}
	}
	if runner.generated != 0 || runner.reloads != 0 {
		t.Fatal("v2 request entered the legacy apply path")
	}
}

func TestAdminV2EmptyDraftAndHelperFailureAreExplicitNotAutomaticFallback(t *testing.T) {
	s, cookie, csrf, runner := realAdminV2Fixture(t)
	view := v2HTTPView(t, s, cookie, csrf)
	if w := request(s, "PUT", "/api/v2/extensions", v2HTTPJSON(t, map[string]any{"revision": view["revision"], "extensions": []any{}}), cookie, csrf); w.Code != 200 {
		t.Fatal("an empty PBX draft must remain legal")
	}
	if len(v2HTTPView(t, s, cookie, csrf)["extensions"].([]any)) != 0 {
		t.Fatal("an account was invented")
	}
	s.m.generator = filepath.Join(t.TempDir(), "missing-configure.py")
	if w := request(s, "GET", "/api/v2/extensions", "", cookie, csrf); w.Code != 503 {
		t.Fatal("unavailable helper must fail without downgrading")
	}
	if runner.generated != 0 || runner.reloads != 0 {
		t.Fatal("helper failure invoked legacy commands")
	}
}

func TestAdminV2SeparateStoreAndStaticModuleBoundaries(t *testing.T) {
	base := filepath.Join(t.TempDir(), "installed")
	for _, store := range []string{base, filepath.Join(base, "state"), filepath.Dir(base), "relative-state"} {
		if separateV2Store(base, store) {
			t.Fatal("a v2 state store overlaps the installed bundle")
		}
	}
	if !separateV2Store(base, filepath.Join(t.TempDir(), "state")) || !separateV2Store(base, "") {
		t.Fatal("separate or legacy mode was rejected")
	}
	s, _, _, _ := newAdminV2AuthenticatedLegacy(t)
	for _, name := range []string{"v2-model.mjs", "v2-app.mjs"} {
		w := request(s, "GET", "/"+name, "", nil, "")
		if w.Code != 200 || !strings.HasPrefix(w.Header().Get("Content-Type"), "text/javascript") {
			t.Fatal("reviewed v2 module is not served with its actual script MIME")
		}
	}
	for _, name := range []string{"v2.go", "state.json", "revisions/synthetic/secrets.json", "../admin_deployment_v2.py"} {
		if w := request(s, "GET", "/"+name, "", nil, ""); w.Code != 404 {
			t.Fatal("private source escaped the static asset allowlist")
		}
	}
}
