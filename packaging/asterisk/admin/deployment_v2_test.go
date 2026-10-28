package main

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestLegacyAdminRejectsVersionedDeploymentWithoutMutationOrCommands(t *testing.T) {
	for _, key := range []string{"SchemaVersion", "schemaVersion", "SCHEMAVERSION"} {
		t.Run(key, func(t *testing.T) {
			m, runner := testManager(t)
			path := filepath.Join(m.base, "deployment.local.json")
			before, err := os.ReadFile(path)
			if err != nil { t.Fatal(err) }
			var model map[string]any
			if err = json.Unmarshal(before, &model); err != nil { t.Fatal(err) }
			model[key] = 2
			model["Peer"] = map[string]any{"InboundAuth": map[string]any{"SecretRef": "synthetic-separate-role"}}
			if err = writeJSON(path, model); err != nil { t.Fatal(err) }
			expected, _ := os.ReadFile(path)
			state, err := m.load()
			if err == nil || !strings.Contains(err.Error(), "版本 2") || len(state.view.Extensions) != 0 {
				t.Fatal("legacy reader must not admit a v2 deployment")
			}
			if _, err = m.apply(context.Background(), "synthetic-ignored-version", nil); err == nil {
				t.Fatal("legacy apply must fail without interpreting versioned permissions")
			}
			if runner.generated != 0 || runner.reloads != 0 { t.Fatal("unexpected generation/reload") }
			after, _ := os.ReadFile(path)
			if string(after) != string(expected) { t.Fatal("versioned source was changed") }
		})
	}
}
