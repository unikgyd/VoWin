package main

// NEW HTTP boundary regressions. No listener, PBX, helper or credentials source
// is started; the existing synthetic manager remains a legacy manager.
import (
	"encoding/json"
	"net/http"
	"strings"
	"testing"
	"time"
)

func newAdminV2AuthenticatedLegacy(t *testing.T) (*webServer, *http.Cookie, string, *fakeRunner) {
	t.Helper()
	m, runner := testManager(t)
	s := &webServer{m: m, key: strings.Repeat("V", 43), sessions: map[string]session{}, failures: map[string][]time.Time{}}
	w := request(s, "POST", "/api/login", `{"key":"`+s.key+`"}`, nil, "")
	if w.Code != 200 {
		t.Fatal("synthetic login failed")
	}
	var auth struct {
		CSRF string `json:"csrf"`
	}
	if json.Unmarshal(w.Body.Bytes(), &auth) != nil || auth.CSRF == "" {
		t.Fatal("synthetic session missing")
	}
	return s, w.Result().Cookies()[0], auth.CSRF, runner
}

func TestAdminV2ConfigurationModeNeverInfersVersionFromLegacyCredentials(t *testing.T) {
	s, cookie, csrf, runner := newAdminV2AuthenticatedLegacy(t)
	w := request(s, "GET", "/api/configuration", "", cookie, csrf)
	if w.Code != 200 {
		t.Fatalf("explicit mode discovery must succeed, got HTTP %d", w.Code)
	}
	var mode map[string]any
	if json.Unmarshal(w.Body.Bytes(), &mode) != nil || len(mode) != 2 || mode["schemaVersion"] != float64(1) || mode["canApply"] != true {
		t.Fatal("legacy management must remain explicitly separate from the v2 draft lifecycle")
	}
	if runner.generated != 0 || runner.reloads != 0 {
		t.Fatal("mode discovery caused a mutation")
	}
}

func TestAdminV2RoutesRequireExplicitPrivateStoreAndPreserveAuthBoundary(t *testing.T) {
	s, cookie, csrf, runner := newAdminV2AuthenticatedLegacy(t)
	for _, tc := range []struct{ method, path, body string }{
		{"GET", "/api/v2/extensions", ""},
		{"PUT", "/api/v2/extensions", `{"revision":"synthetic","extensions":[]}`},
		{"POST", "/api/v2/connection", `{"revision":"synthetic","number":"1001"}`},
	} {
		t.Run(tc.method+tc.path, func(t *testing.T) {
			if w := request(s, tc.method, tc.path, tc.body, nil, ""); w.Code != 401 {
				t.Fatal("new route escaped authentication")
			}
			if tc.method != "GET" {
				if w := request(s, tc.method, tc.path, tc.body, cookie, ""); w.Code != 403 {
					t.Fatal("new route escaped CSRF")
				}
			}
			w := request(s, tc.method, tc.path, tc.body, cookie, csrf)
			if w.Code != 404 {
				t.Fatalf("v2 needs an explicitly configured private store, got HTTP %d", w.Code)
			}
			if strings.Contains(w.Body.String(), strings.Repeat("A", 16)) {
				t.Fatal("new route exposed legacy credentials")
			}
		})
	}
	if runner.generated != 0 || runner.reloads != 0 {
		t.Fatal("unconfigured v2 route caused a mutation")
	}
}
