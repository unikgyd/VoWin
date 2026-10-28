package main

// Supplemental offline controller regressions. The lease and durable HTTP job
// ledger are real. Child observations are explicit synthetic journal DTOs; no
// Python child, Docker, PBX installation, or inherited orphan process is tested.
import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func applicationRecoveryBoundedGET(t *testing.T, m *manager, path string, releaseOwner func() error) *httptest.ResponseRecorder {
	t.Helper()
	finished := make(chan *httptest.ResponseRecorder, 1)
	go func() {
		w := httptest.NewRecorder()
		r := httptest.NewRequest("GET", path, nil)
		(&webServer{m: m}).handleV2Application(w, r)
		finished <- w
	}()
	select {
	case w := <-finished:
		return w
	case <-time.After(2 * time.Second):
		// Release the real foreign lease before reporting a blocking regression,
		// so a mistakenly blocking flock does not leave this test's goroutine.
		if releaseOwner != nil {
			_ = releaseOwner()
		}
		select {
		case <-finished:
		case <-time.After(2 * time.Second):
			t.Error("GET did not return after the fixture lease was released")
		}
		t.Fatal("read-only GET blocked behind the native application lease")
		return nil
	}
}

func TestAdminV2ApplicationRecoveryGETAcquiresReleasedLeaseWithoutReplaying(t *testing.T) {
	for _,abandoned := range []string{"queued", "running"} {
		t.Run(abandoned, func(t *testing.T) {
			m, owner := applicationTestController(t)
			requestID, revision, operation := strings.Repeat("R", 43), strings.Repeat("V", 43), strings.Repeat("O", 43)
			applicationTestStage(t, owner, abandoned, "apply", requestID, revision, operation)
			owner.mu.Lock()
			owned, active := owner.owned, owner.active
			owner.mu.Unlock()
			if !owned || active {
				t.Fatal("fixture must hold the real lease without starting a worker")
			}

			// This marker is only an unchanged-file sentinel, not an installed PBX.
			marker := filepath.Join(m.base, "unchanged-pbx-sentinel")
			markerBytes := []byte("synthetic pre-existing PBX sentinel\n")
			if err := os.WriteFile(marker, markerBytes, 0600); err != nil {
				t.Fatal(err)
			}
			markerBefore, err := os.Stat(marker)
			if err != nil {
				t.Fatal(err)
			}
			ledger := filepath.Join(owner.directory, "state.json")
			ledgerBefore, err := os.ReadFile(ledger)
			if err != nil {
				t.Fatal(err)
			}

			observer, err := newV2ApplicationController(m, applicationTestOptions())
			if err != nil {
				t.Fatal("busy second controller could not initialize read-only")
			}
			defer observer.Close()
			if observer.owned {
				t.Fatal("second controller acquired a lease still owned by the first")
			}
			m.application = observer
			var mutations atomic.Int32
			observer.mutate = func([]string, *os.File) ([]byte, error) {
				mutations.Add(1)
				return nil, errors.New("recovery GET must never replay a mutation")
			}
			observer.observe = func(context.Context, []string) ([]byte, error) {
				view := applicationTestOverview(nil)
				view.Busy = true
				view.InstalledState = "unconfirmed"
				return applicationTestEnvelope(view), nil
			}
			path := "/api/v2/application?request=" + requestID
			w := applicationRecoveryBoundedGET(t, m, path, owner.Close)
			value := applicationTestResponse(t, w)
			if w.Code != 200 || value.Overview == nil || !value.Overview.Busy || value.Job == nil || value.Job.State != "uncertain" || value.Job.ErrorCode != "uncertain" {
				t.Fatal("foreign-lease GET did not conservatively expose the abandoned request")
			}
			observer.mu.Lock()
			owned = observer.owned
			observer.mu.Unlock()
			ledgerDuring, err := os.ReadFile(ledger)
			if err != nil || owned || !bytes.Equal(ledgerBefore, ledgerDuring) || mutations.Load() != 0 {
				t.Fatal("busy observation took ownership, rewrote the ledger, or started a worker")
			}

			if err := owner.Close(); err != nil {
				t.Fatal("first controller did not release its native lease")
			}
			observer.observe = func(context.Context, []string) ([]byte, error) {
				view := applicationTestOverview(applicationTestOperation("applied", operation, revision))
				view.Installed = &v2ApplicationInstalled{Operation: operation, Revision: revision}
				view.InstalledState = "known"
				return applicationTestEnvelope(view), nil
			}
			w = applicationRecoveryBoundedGET(t, m, path, nil)
			value = applicationTestResponse(t, w)
			if w.Code != 200 || value.Job == nil || value.Job.State != "succeeded" || value.Job.ErrorCode != "" || value.Job.Result == nil || value.Job.Result.State != "applied" || value.Job.Operation != operation || value.Job.Revision != revision {
				t.Fatal("GET did not reconcile the exact journal result after lease release")
			}
			observer.mu.Lock()
			owned, active = observer.owned, observer.active
			observer.mu.Unlock()
			if !owned || active || mutations.Load() != 0 {
				t.Fatal("GET did not own the released lease, or replayed the abandoned worker")
			}
			persisted, err := os.ReadFile(ledger)
			if err != nil {
				t.Fatal(err)
			}
			stored, err := applicationDecodeStore(persisted)
			if err != nil || len(stored.Jobs) != 1 || stored.Jobs[0].State != "succeeded" || stored.Jobs[0].Result == nil || *stored.Jobs[0].Result.Operation != operation || *stored.Jobs[0].Result.Revision != revision {
				t.Fatal("GET reconciliation was not durably saved for the same request")
			}
			markerAfter, err := os.Stat(marker)
			if err != nil {
				t.Fatal(err)
			}
			actualMarker, err := os.ReadFile(marker)
			if err != nil || !bytes.Equal(markerBytes, actualMarker) || !os.SameFile(markerBefore, markerAfter) || markerBefore.Mode() != markerAfter.Mode() || !markerBefore.ModTime().Equal(markerAfter.ModTime()) {
				t.Fatal("read-only reconciliation changed the pre-existing sentinel")
			}
		})
	}
}

func TestAdminV2ApplicationRecoveryOverviewCrossFieldContract(t *testing.T) {
	a, ar := strings.Repeat("A", 43), strings.Repeat("C", 43)
	b, br := strings.Repeat("B", 43), strings.Repeat("D", 43)
	installedA := &v2ApplicationInstalled{Operation: a, Revision: ar}
	installedB := &v2ApplicationInstalled{Operation: b, Revision: br}
	view := func(state, installedState string, installed *v2ApplicationInstalled) v2ApplicationOverview {
		result := applicationTestOverview(applicationTestOperation(state, b, br))
		result.Installed, result.InstalledState = installed, installedState
		return result
	}
	busy := applicationTestOverview(nil)
	busy.Busy, busy.InstalledState = true, "unconfirmed"
	busyOperation := busy
	busyOperation.Operation = applicationTestOperation("starting", b, br)
	busyInstalled := busy
	busyInstalled.Installed = installedA
	busyUnknown := busy
	busyUnknown.InstalledState = "unknown"
	nullWithInstalled := applicationTestOverview(nil)
	nullWithInstalled.Installed, nullWithInstalled.InstalledState = installedA, "known"

	cases := []struct {
		name string
		value v2ApplicationOverview
		valid bool
	}{
		{"uninitialized-read-only", applicationTestOverview(nil), true},
		{"busy-conservative", busy, true},
		{"legacy-applied-unknown", view("applied", "unknown", nil), true},
		{"prepared-known-prior-installation", view("prepared", "known", installedA), true},
		{"prepared-no-known-installation", view("prepared", "unknown", nil), true},
		{"pending-unconfirmed-prior-installation", view("installing", "unconfirmed", installedA), true},
		{"pending-unconfirmed-without-index", view("starting", "unconfirmed", nil), true},
		{"applied-known-same-operation", view("applied", "known", installedB), true},
		{"rolled-back-known-prior-installation", view("rolled-back", "known", installedA), true},
		{"drifted-existing-index", view("applied", "drifted", installedB), true},
		{"drifted-without-index", view("applied", "drifted", nil), false},
		{"stopping-cannot-be-known", view("stopping", "known", installedA), false},
		{"installing-cannot-be-known", view("installing", "known", installedA), false},
		{"starting-cannot-be-known", view("starting", "known", installedA), false},
		{"recovery-required-cannot-be-known", view("recovery-required", "known", installedA), false},
		{"pending-cannot-be-unknown", view("installing", "unknown", nil), false},
		{"pending-cannot-be-drifted", view("starting", "drifted", installedA), false},
		{"known-applied-wrong-operation", view("applied", "known", installedA), false},
		{"known-applied-wrong-revision", view("applied", "known", &v2ApplicationInstalled{Operation: b, Revision: ar}), false},
		{"null-operation-with-installation", nullWithInstalled, false},
		{"known-without-index", view("prepared", "known", nil), false},
		{"unknown-with-index", view("prepared", "unknown", installedA), false},
		{"busy-cannot-expose-operation", busyOperation, false},
		{"busy-cannot-expose-index", busyInstalled, false},
		{"busy-cannot-be-unknown", busyUnknown, false},
	}
	for _,tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			raw, err := json.Marshal(tc.value)
			if err != nil {
				t.Fatal(err)
			}
			var decoded v2ApplicationOverview
			if got := applicationOverviewJSON(raw, &decoded); got != tc.valid {
				t.Fatalf("overview cross-field acceptance = %v, want %v", got, tc.valid)
			}
			if tc.valid && !reflect.DeepEqual(decoded, tc.value) {
				t.Fatal("accepted overview did not preserve its exact public DTO")
			}
		})
	}
}

func TestAdminV2ApplicationRecoveryUnconfirmedIndexDoesNotCompleteExactAppliedJob(t *testing.T) {
	m, controller := applicationTestController(t)
	requestID, revision, operation := strings.Repeat("R", 43), strings.Repeat("V", 43), strings.Repeat("O", 43)
	applicationTestStage(t, controller, "uncertain", "apply", requestID, revision, operation)
	var mutations atomic.Int32
	controller.mutate = func([]string, *os.File) ([]byte, error) {
		mutations.Add(1)
		return nil, errors.New("journal reconciliation must not replay apply")
	}
	controller.observe = func(context.Context, []string) ([]byte, error) {
		view := applicationTestOverview(applicationTestOperation("applied", operation, revision))
		view.Installed = &v2ApplicationInstalled{Operation: strings.Repeat("A", 43), Revision: strings.Repeat("B", 43)}
		view.InstalledState = "unconfirmed"
		return applicationTestEnvelope(view), nil
	}
	ledger := filepath.Join(controller.directory, "state.json")
	before, err := os.ReadFile(ledger)
	if err != nil {
		t.Fatal(err)
	}
	path := "/api/v2/application?request=" + requestID
	w := applicationTestHTTP(t, m, "GET", path, "")
	value := applicationTestResponse(t, w)
	if w.Code != 200 || value.Overview == nil || value.Overview.InstalledState != "unconfirmed" || value.Job == nil || value.Job.State != "uncertain" || value.Job.ErrorCode != "uncertain" || value.Job.Result != nil {
		t.Fatal("terminal journal without its confirmed installed index falsely completed the job")
	}
	after, err := os.ReadFile(ledger)
	if err != nil || !bytes.Equal(before, after) || mutations.Load() != 0 {
		t.Fatal("unconfirmed observation rewrote the ledger or invoked a mutator")
	}
	controller.observe = func(context.Context, []string) ([]byte, error) {
		view := applicationTestOverview(applicationTestOperation("applied", operation, revision))
		view.Installed = &v2ApplicationInstalled{Operation: operation, Revision: revision}
		view.InstalledState = "known"
		return applicationTestEnvelope(view), nil
	}
	w = applicationTestHTTP(t, m, "GET", path, "")
	value = applicationTestResponse(t, w)
	if w.Code != 200 || value.Job == nil || value.Job.State != "succeeded" || value.Job.ErrorCode != "" || value.Job.Result == nil || value.Job.Result.State != "applied" || value.Job.Operation != operation || value.Job.Revision != revision || mutations.Load() != 0 {
		t.Fatal("same-operation known installation did not allow read-only reconciliation")
	}
}

func TestAdminV2ApplicationRecoveryFullHandlerGuardsAllMutatingActions(t *testing.T) {
	for _,action := range []string{"prepare", "apply", "recover"} {
		t.Run(action, func(t *testing.T) {
			m, controller := applicationTestController(t)
			requestID, revision, operation := strings.Repeat("R", 43), strings.Repeat("V", 43), strings.Repeat("O", 43)
			body := applicationTestBody(action, requestID, revision, operation)
			token, expiredToken, csrf := strings.Repeat("S", 43), strings.Repeat("E", 43), strings.Repeat("C", 43)
			server := &webServer{
				m: m,
				sessions: map[string]session{
					token: {csrf: csrf, until: time.Now().Add(time.Hour)},
					expiredToken: {csrf: csrf, until: time.Now().Add(-time.Hour)},
				},
				failures: map[string][]time.Time{},
			}
			var mutations atomic.Int32
			expectedState := map[string]string{"prepare": "prepared", "apply": "applied", "recover": "rolled-back"}[action]
			controller.mutate = func(args []string, lease *os.File) ([]byte, error) {
				mutations.Add(1)
				if lease == nil || !reflect.DeepEqual(args, controller.cliArgs(action, applicationPayload{Revision: revision, Operation: operation})) {
					t.Error("authorized worker did not use its fixed server-owned action and lease")
				}
				return applicationTestEnvelope(applicationTestOperation(expectedState, operation, revision)), nil
			}
			ledger := filepath.Join(controller.directory, "state.json")
			before, err := os.ReadFile(ledger)
			if err != nil {
				t.Fatal(err)
			}
			beforeInfo, err := os.Stat(ledger)
			if err != nil {
				t.Fatal(err)
			}
			makeRequest := func() *http.Request {
				r := httptest.NewRequest("POST", "http://admin.example.invalid/api/v2/application/"+action, strings.NewReader(body))
				r.RemoteAddr = "127.0.0.1:47000"
				r.Header.Set("Content-Type", "application/json")
				r.Header.Set("Origin", "http://admin.example.invalid")
				r.Header.Set("X-CSRF-Token", csrf)
				r.AddCookie(&http.Cookie{Name: "pbx_admin", Value: token})
				return r
			}
			guards := []struct {
				name string
				status int
				change func(*http.Request)
			}{
				{"missing-session", 401, func(r *http.Request) { r.Header.Del("Cookie") }},
				{"expired-session", 401, func(r *http.Request) { r.Header.Set("Cookie", "pbx_admin="+expiredToken) }},
				{"nonprivate-peer", 403, func(r *http.Request) { r.RemoteAddr = "203.0.113.8:47000" }},
				{"foreign-origin", 403, func(r *http.Request) { r.Header.Set("Origin", "https://foreign.example.invalid") }},
				{"missing-csrf", 403, func(r *http.Request) { r.Header.Del("X-CSRF-Token") }},
				{"incorrect-csrf", 403, func(r *http.Request) { r.Header.Set("X-CSRF-Token", strings.Repeat("X", 43)) }},
			}
			for _,guard := range guards {
				t.Run(guard.name, func(t *testing.T) {
					r := makeRequest()
					guard.change(r)
					w := httptest.NewRecorder()
					server.handler(w, r)
					if w.Code != guard.status {
						t.Fatalf("full handler returned %d, want %d", w.Code, guard.status)
					}
					after, err := os.ReadFile(ledger)
					if err != nil {
						t.Fatal(err)
					}
					afterInfo, err := os.Stat(ledger)
					if err != nil || !bytes.Equal(before, after) || !os.SameFile(beforeInfo, afterInfo) || !beforeInfo.ModTime().Equal(afterInfo.ModTime()) || mutations.Load() != 0 {
						t.Fatal("rejected HTTP request wrote the job ledger or reached the mutator")
					}
				})
			}
			w := httptest.NewRecorder()
			server.handler(w, makeRequest())
			value := applicationTestResponse(t, w)
			if w.Code != 202 || value.Job == nil || value.Job.Request != requestID || value.Job.Action != action {
				t.Fatal("matching authenticated Origin and CSRF did not queue the single action")
			}
			applicationTestWait(t, controller)
			controller.mu.Lock()
			job, found := controller.selected(requestID)
			controller.mu.Unlock()
			if !found || job == nil || job.State != "succeeded" || job.Result == nil || job.Result.State != expectedState || mutations.Load() != 1 {
				t.Fatal("authorized full-handler request did not finish exactly one synthetic transport")
			}
		})
	}
}
