package main

// Two actual helper processes compete for one revision through authenticated
// handlers. No listening socket, installed PBX or synthetic helper is involved.
import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

func TestAdminV2ConcurrentRealHelpersHaveExactlyOneWinner(t *testing.T) {
	s, cookie, csrf, runner := realAdminV2Fixture(t)
	view := v2HTTPView(t, s, cookie, csrf)
	oldRevision := view["revision"].(string)
	oldDir := filepath.Join(s.m.v2Store, "revisions", oldRevision)
	oldFiles := map[string][]byte{}
	for _, name := range []string{"deployment.json", "secrets.json", "manifest.json"} {
		body, err := os.ReadFile(filepath.Join(oldDir, name))
		if err != nil {
			t.Fatal("cannot snapshot owned immutable revision")
		}
		oldFiles[name] = body
	}
	base := v2HTTPJSON(t, v2HTTPPayload(t, view))
	type reply struct {
		code int
		name string
		body []byte
	}
	requests := make([]string, 2)
	names := []string{"Concurrent draft alpha", "Concurrent draft beta"}
	for i, name := range names {
		var payload map[string]any
		if json.Unmarshal([]byte(base), &payload) != nil {
			t.Fatal("cannot detach concurrent synthetic requests")
		}
		payload["extensions"].([]any)[0].(map[string]any)["name"] = name
		requests[i] = v2HTTPJSON(t, payload)
	}
	start := make(chan struct{})
	replies := make(chan reply, 2)
	for i, body := range requests {
		go func(body, name string) {
			<-start
			w := request(s, "PUT", "/api/v2/extensions", body, cookie, csrf)
			replies <- reply{w.Code, name, append([]byte(nil), w.Body.Bytes()...)}
		}(body, names[i])
	}
	close(start)
	winner := ""
	counts := map[int]int{}
	for range requests {
		got := <-replies
		counts[got.code]++
		if got.code == 200 {
			winner = got.name
			var saved map[string]any
			if json.Unmarshal(got.body, &saved) != nil || saved["revision"] == oldRevision {
				t.Fatal("winning save did not return a new revision")
			}
		}
	}
	if counts[200] != 1 || counts[409] != 1 || len(counts) != 2 {
		t.Fatalf("concurrent saves must have one commit and one conflict: %v", counts)
	}
	after := v2HTTPView(t, s, cookie, csrf)
	if after["revision"] == oldRevision || after["extensions"].([]any)[0].(map[string]any)["name"] != winner {
		t.Fatal("current head does not identify the one successful writer")
	}
	for name, before := range oldFiles {
		body, err := os.ReadFile(filepath.Join(oldDir, name))
		if err != nil || !bytes.Equal(body, before) {
			t.Fatal("a concurrent save modified an immutable old revision")
		}
	}
	if runner.generated != 0 || runner.reloads != 0 {
		t.Fatal("concurrent draft saves touched installed PBX generation or reload")
	}
}
