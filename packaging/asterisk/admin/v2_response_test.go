package main

// Actual fixed-output Python helper fixtures, never user-provided executable
// code. All paths, identities and credentials are synthetic and test-owned.
import (
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

const v2ResponseSentinel = "SyntheticHelperDiagnosticNeverReflect"

type v2ResponseCase struct {
	name string
	raw  []byte
}

func v2ResponseJSON(t *testing.T, value any) []byte {
	t.Helper()
	raw, err := json.Marshal(value)
	if err != nil {
		t.Fatal("cannot serialize fixed synthetic helper output")
	}
	return raw
}

func v2ResponseCopy(t *testing.T, value map[string]any) map[string]any {
	t.Helper()
	var result map[string]any
	if json.Unmarshal(v2ResponseJSON(t, value), &result) != nil {
		t.Fatal("cannot copy fixed synthetic helper output")
	}
	return result
}

func v2ResponseSnapshot() map[string]any {
	return map[string]any{
		"schemaVersion": 2, "revision": strings.Repeat("A", 43), "state": "saved-not-applied", "canApply": false,
		"sipServer": "127.78.0.2", "sipPort": 5086,
		"features": map[string]any{"calls": false, "carrier": true, "messages": false, "sms": true},
		"extensions": []any{map[string]any{
			"number": "4201", "sourceNumber": "4201", "name": "Synthetic response account", "enabled": false,
			"ring": true, "maxContacts": 4, "allowOutgoing": true, "allowMessages": false,
			"allowMobileSmsSend": false, "allowMobileSmsReceive": true, "hasPassword": true,
		}},
	}
}

func v2ResponseConnection() map[string]any {
	return map[string]any{
		"revision": strings.Repeat("A", 43), "state": "saved-not-applied", "server": "127.78.0.2",
		"port": 5086, "username": "4201", "password": "SyntheticChosenCredential_123", "transport": "UDP",
	}
}

func v2ResponseEnvelope(t *testing.T, value map[string]any) []byte {
	t.Helper()
	return v2ResponseJSON(t, map[string]any{"ok": true, "value": value})
}

func v2ResponseDuplicate(t *testing.T, raw []byte, field, alias string, value any) []byte {
	t.Helper()
	needle := `"` + field + `":`
	if strings.Count(string(raw), needle) != 1 {
		t.Fatal("synthetic duplicate-key mutation must target exactly one field")
	}
	return []byte(strings.Replace(string(raw), needle, alias+":"+string(v2ResponseJSON(t, value))+","+needle, 1))
}

func v2ResponseFixture(t *testing.T, raw []byte, exitCode int) (*webServer, *http.Cookie, string, *fakeRunner) {
	t.Helper()
	s, cookie, csrf, runner := newAdminV2AuthenticatedLegacy(t)
	directory := t.TempDir()
	// The helper ignores argv/stdin and emits these constant fixture bytes only.
	// Base64 keeps fixture data separate from executable Python source syntax.
	script := "import base64,sys\n" +
		"sys.stdout.buffer.write(base64.b64decode(\"" + base64.StdEncoding.EncodeToString(raw) + "\"))\n" +
		"sys.stdout.buffer.flush()\n" +
		"sys.stderr.write(\"" + v2ResponseSentinel + "\\n\")\n" +
		fmt.Sprintf("raise SystemExit(%d)\n", exitCode)
	if os.WriteFile(filepath.Join(directory, "admin_deployment_v2.py"), []byte(script), 0600) != nil {
		t.Fatal("cannot create fixed synthetic helper fixture")
	}
	s.m.generator = filepath.Join(directory, "configure.py")
	s.m.v2Store = filepath.Join(t.TempDir(), "separate-private-state")
	return s, cookie, csrf, runner
}

func v2ResponseUncertain(t *testing.T, wCode int, body string, runner *fakeRunner) {
	t.Helper()
	if wCode != 503 {
		t.Fatalf("malformed helper output must be uncertain HTTP 503, got %d", wCode)
	}
	var failure map[string]any
	if json.Unmarshal([]byte(body), &failure) != nil || len(failure) != 1 || failure["error"] != "草稿操作结果待确认；请重新读取后再操作" {
		t.Fatal("helper failure must use only the fixed uncertain diagnostic")
	}
	if strings.Contains(body, v2ResponseSentinel) || strings.Contains(body, "SyntheticChosenCredential_123") {
		t.Fatal("helper stdout/stderr diagnostic or credential was reflected")
	}
	if runner.generated != 0 || runner.reloads != 0 {
		t.Fatal("invalid helper output entered the legacy apply path")
	}
}

func TestAdminV2HelperSnapshotResponseRequiresExactCompleteTypedSchema(t *testing.T) {
	base := v2ResponseSnapshot()
	for _, empty := range []bool{false, true} {
		t.Run(fmt.Sprintf("valid-empty-%t", empty), func(t *testing.T) {
			value := v2ResponseCopy(t, base)
			if empty {
				value["extensions"] = []any{}
			}
			s, cookie, csrf, runner := v2ResponseFixture(t, v2ResponseEnvelope(t, value), 0)
			w := request(s, "GET", "/api/v2/extensions", "", cookie, csrf)
			var actual map[string]any
			if w.Code != 200 || json.Unmarshal(w.Body.Bytes(), &actual) != nil ||
				string(v2ResponseJSON(t, actual)) != string(v2ResponseJSON(t, value)) {
				t.Fatal("complete fixed synthetic snapshot must remain accepted, including an empty account list")
			}
			if runner.generated != 0 || runner.reloads != 0 || strings.Contains(w.Body.String(), v2ResponseSentinel) {
				t.Fatal("positive response leaked diagnostics or performed a legacy operation")
			}
		})
	}
	cases := []v2ResponseCase{}
	add := func(name string, value map[string]any) { cases = append(cases, v2ResponseCase{name, v2ResponseEnvelope(t, value)}) }
	keys := []string{"schemaVersion", "revision", "state", "canApply", "sipServer", "sipPort", "features", "extensions"}
	for _, key := range keys {
		missing := v2ResponseCopy(t, base)
		delete(missing, key)
		add("value-missing-"+key, missing)
		null := v2ResponseCopy(t, base)
		null[key] = nil
		add("value-null-"+key, null)
	}
	for _, key := range []string{"calls", "carrier", "messages", "sms"} {
		for _, variant := range []string{"missing", "null", "wrong-type"} {
			value := v2ResponseCopy(t, base)
			features := value["features"].(map[string]any)
			switch variant {
			case "missing": delete(features, key)
			case "null": features[key] = nil
			default: features[key] = 1
			}
			add("feature-"+variant+"-"+key, value)
		}
	}
	rowKeys := []string{"number", "sourceNumber", "name", "enabled", "ring", "maxContacts", "allowOutgoing", "allowMessages", "allowMobileSmsSend", "allowMobileSmsReceive", "hasPassword"}
	for _, key := range rowKeys {
		for _, variant := range []string{"missing", "null"} {
			value := v2ResponseCopy(t, base)
			row := value["extensions"].([]any)[0].(map[string]any)
			if variant == "missing" { delete(row, key) } else { row[key] = nil }
			add("row-"+variant+"-"+key, value)
		}
	}
	for _, tc := range []struct{ key string; value any }{
		{"schemaVersion", 1}, {"revision", ""}, {"revision", strings.Repeat("A", 42)}, {"revision", strings.Repeat("A", 44)},
		{"revision", strings.Repeat("!", 43)}, {"state", "applied"}, {"canApply", true}, {"sipServer", ""},
		{"sipPort", 0}, {"sipPort", -1}, {"sipPort", 65536}, {"sipPort", true}, {"sipPort", 1.5},
		{"extensions", map[string]any{}}, {"features", []any{}},
	} {
		value := v2ResponseCopy(t, base)
		value[tc.key] = tc.value
		add(fmt.Sprintf("value-invalid-%s-%v", tc.key, tc.value), value)
	}
	for _, tc := range []struct{ key string; value any }{
		{"number", "4"}, {"number", "123456789"}, {"number", "+4201"}, {"number", "٤٢٠١"}, {"number", 4201},
		{"sourceNumber", "4202"}, {"sourceNumber", ""}, {"maxContacts", 0}, {"maxContacts", 9},
		{"maxContacts", true}, {"name", 1}, {"enabled", 1}, {"ring", "false"}, {"allowOutgoing", 0},
		{"allowMessages", "true"}, {"allowMobileSmsSend", 1}, {"allowMobileSmsReceive", "false"}, {"hasPassword", 1},
	} {
		value := v2ResponseCopy(t, base)
		value["extensions"].([]any)[0].(map[string]any)[tc.key] = tc.value
		add(fmt.Sprintf("row-invalid-%s-%v", tc.key, tc.value), value)
	}
	for _, location := range []string{"value", "feature", "row"} {
		value := v2ResponseCopy(t, base)
		object := value
		if location == "feature" { object = value["features"].(map[string]any) }
		if location == "row" { object = value["extensions"].([]any)[0].(map[string]any) }
		object[v2ResponseSentinel] = v2ResponseSentinel
		add(location+"-unknown", value)
	}
	complete := v2ResponseEnvelope(t, base)
	for _, tc := range []struct{ field, alias string; value any }{
		{"ok", `"ok"`, true}, {"ok", `"OK"`, true}, {"ok", `"o\u006b"`, true},
		{"revision", `"revision"`, strings.Repeat("A", 43)}, {"revision", `"Revision"`, strings.Repeat("A", 43)}, {"revision", `"revis\u0069on"`, strings.Repeat("A", 43)},
		{"calls", `"calls"`, false}, {"calls", `"Calls"`, false}, {"calls", `"ca\u006cls"`, false},
		{"name", `"name"`, "Synthetic response account"}, {"name", `"Name"`, "Synthetic response account"}, {"name", `"na\u006de"`, "Synthetic response account"},
	} {
		cases = append(cases, v2ResponseCase{"duplicate-" + tc.field + "-" + tc.alias, v2ResponseDuplicate(t, complete, tc.field, tc.alias, tc.value)})
	}
	for _, envelope := range []map[string]any{
		{"value": base}, {"ok": true}, {"ok": nil, "value": base}, {"ok": true, "value": nil},
		{"ok": true, "value": base, "code": "invalid"}, {"ok": true, "value": base, "error": v2ResponseSentinel},
		{"ok": true, "value": base, v2ResponseSentinel: v2ResponseSentinel},
	} {
		cases = append(cases, v2ResponseCase{fmt.Sprintf("envelope-%d", len(cases)), v2ResponseJSON(t, envelope)})
	}
	cases = append(cases, v2ResponseCase{"trailing-document", append(append([]byte{}, complete...), []byte(`{}`)...)},
		v2ResponseCase{"invalid-utf8", []byte{0xff}}, v2ResponseCase{"null-envelope", []byte(`null`)})
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			s, cookie, csrf, runner := v2ResponseFixture(t, tc.raw, 0)
			w := request(s, "GET", "/api/v2/extensions", "", cookie, csrf)
			v2ResponseUncertain(t, w.Code, w.Body.String(), runner)
		})
	}
}

func TestAdminV2HelperConnectionResponseIsStrictAndBoundToValidatedChoice(t *testing.T) {
	base := v2ResponseConnection()
	query := string(v2ResponseJSON(t, map[string]any{"revision": strings.Repeat("A", 43), "number": "4201"}))
	s, cookie, csrf, runner := v2ResponseFixture(t, v2ResponseEnvelope(t, base), 0)
	w := request(s, "POST", "/api/v2/connection", query, cookie, csrf)
	var actual map[string]any
	if w.Code != 200 || json.Unmarshal(w.Body.Bytes(), &actual) != nil || string(v2ResponseJSON(t, actual)) != string(v2ResponseJSON(t, base)) {
		t.Fatal("complete exact chosen synthetic connection must remain accepted")
	}
	if runner.generated != 0 || runner.reloads != 0 || strings.Contains(w.Body.String(), v2ResponseSentinel) {
		t.Fatal("connection used legacy operations or reflected stderr")
	}
	cases := []v2ResponseCase{}
	keys := []string{"revision", "state", "server", "port", "username", "password", "transport"}
	for _, key := range keys {
		for _, variant := range []string{"missing", "null"} {
			value := v2ResponseCopy(t, base)
			if variant == "missing" { delete(value, key) } else { value[key] = nil }
			cases = append(cases, v2ResponseCase{variant + "-" + key, v2ResponseEnvelope(t, value)})
		}
	}
	for _, tc := range []struct{ key string; value any }{
		{"revision", strings.Repeat("B", 43)}, {"revision", strings.Repeat("A", 42)}, {"state", "applied"},
		{"server", ""}, {"port", 0}, {"port", 65536}, {"port", true}, {"username", "4202"},
		{"username", "4"}, {"username", "123456789"}, {"username", "+4201"}, {"username", "٤٢٠١"},
		{"password", ""}, {"password", strings.Repeat("a", 11)}, {"password", strings.Repeat("a", 129)},
		{"password", "not a safe credential"}, {"password", 123}, {"transport", "TCP"},
	} {
		value := v2ResponseCopy(t, base)
		value[tc.key] = tc.value
		cases = append(cases, v2ResponseCase{fmt.Sprintf("invalid-%s-%v", tc.key, tc.value), v2ResponseEnvelope(t, value)})
	}
	unknown := v2ResponseCopy(t, base)
	unknown[v2ResponseSentinel] = v2ResponseSentinel
	cases = append(cases, v2ResponseCase{"unknown-field", v2ResponseEnvelope(t, unknown)})
	complete := v2ResponseEnvelope(t, base)
	for _, alias := range []string{`"username"`, `"Username"`, `"user\u006eame"`} {
		cases = append(cases, v2ResponseCase{"duplicate-" + alias, v2ResponseDuplicate(t, complete, "username", alias, "4201")})
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			s, cookie, csrf, runner := v2ResponseFixture(t, tc.raw, 0)
			w := request(s, "POST", "/api/v2/connection", query, cookie, csrf)
			v2ResponseUncertain(t, w.Code, w.Body.String(), runner)
		})
	}
	// Even a diagnostic-bearing failure is never echoed. This is not an apply.
	t.Run("explicit-uncertain-error-is-fixed", func(t *testing.T) {
		raw := v2ResponseJSON(t, map[string]any{"ok": false, "code": "uncertain", "error": v2ResponseSentinel})
		s, cookie, csrf, runner := v2ResponseFixture(t, raw, 1)
		w := request(s, "POST", "/api/v2/connection", query, cookie, csrf)
		v2ResponseUncertain(t, w.Code, w.Body.String(), runner)
	})
}

func TestAdminV2StoreSeparationRejectsPhysicalOverlapThroughBaseAndAncestorAliases(t *testing.T) {
	owner := t.TempDir()
	installed := filepath.Join(owner, "installed")
	inside := filepath.Join(installed, "private-draft")
	separate := filepath.Join(owner, "separate-private-draft")
	for _, path := range []string{inside, separate} {
		if os.MkdirAll(path, 0700) != nil { t.Fatal("cannot create owned separation fixture") }
	}
	baseAlias := filepath.Join(owner, "installed-alias")
	ancestorAlias := filepath.Join(owner, "ancestor-alias")
	if os.Symlink(installed, baseAlias) != nil || os.Symlink(owner, ancestorAlias) != nil {
		t.Fatal("cannot create owned physical alias fixtures")
	}
	cases := map[string][2]string{
		"base-alias": {baseAlias, inside},
		"base-ancestor-alias": {filepath.Join(ancestorAlias, "installed"), inside},
		"store-ancestor-alias": {installed, filepath.Join(baseAlias, "private-draft")},
	}
	names := make([]string, 0, len(cases))
	for name := range cases { names = append(names, name) }
	sort.Strings(names)
	for _, name := range names {
		t.Run(name, func(t *testing.T) {
			pair := cases[name]
			if separateV2Store(pair[0], pair[1]) { t.Fatal("physical installed/draft overlap must not be admitted through a path alias") }
		})
	}
	if !separateV2Store(installed, separate) || !separateV2Store(installed, "") {
		t.Fatal("a real separate private store or explicit legacy mode must remain allowed")
	}
}
