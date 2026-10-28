package main

// The v2 editor owns a private draft, not installed Asterisk files. Validation
// and atomic revision storage share the strict Python deployment model rather
// than duplicating its policy or trying to reverse credentials from PJSIP.
import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
)

type v2Features struct {
	Calls    bool `json:"calls"`
	Carrier  bool `json:"carrier"`
	Messages bool `json:"messages"`
	SMS      bool `json:"sms"`
}
type v2Extension struct {
	Number                string `json:"number"`
	SourceNumber          string `json:"sourceNumber"`
	Name                  string `json:"name"`
	Enabled               bool   `json:"enabled"`
	Ring                  bool   `json:"ring"`
	MaxContacts           int    `json:"maxContacts"`
	AllowOutgoing         bool   `json:"allowOutgoing"`
	AllowMessages         bool   `json:"allowMessages"`
	AllowMobileSMSSend    bool   `json:"allowMobileSmsSend"`
	AllowMobileSMSReceive bool   `json:"allowMobileSmsReceive"`
	HasPassword           bool   `json:"hasPassword"`
}
type v2Snapshot struct {
	SchemaVersion int           `json:"schemaVersion"`
	Revision      string        `json:"revision"`
	State         string        `json:"state"`
	CanApply      bool          `json:"canApply"`
	SIPServer     string        `json:"sipServer"`
	SIPPort       int           `json:"sipPort"`
	Features      v2Features    `json:"features"`
	Extensions    []v2Extension `json:"extensions"`
}
type v2Connection struct {
	Revision  string `json:"revision"`
	State     string `json:"state"`
	Server    string `json:"server"`
	Port      int    `json:"port"`
	Username  string `json:"username"`
	Password  string `json:"password"`
	Transport string `json:"transport"`
}
type v2Failure struct{ code string }

func (e v2Failure) Error() string { return "v2 draft operation could not be confirmed" }

type v2LimitedOutput struct{ bytes.Buffer }

func (w *v2LimitedOutput) Write(p []byte) (int, error) {
	if len(p) > 65536-w.Len() {
		return 0, errors.New("v2 helper response exceeds bound")
	}
	return w.Buffer.Write(p)
}
func decodeV2Result(raw []byte, value any) error {
	if !strictV2JSON(raw) {
		return errors.New("invalid helper result")
	}
	d := json.NewDecoder(bytes.NewReader(raw))
	d.DisallowUnknownFields()
	if d.Decode(value) != nil || d.Decode(&struct{}{}) != io.EOF {
		return errors.New("invalid helper result")
	}
	return nil
}
func (m *manager) v2Call(ctx context.Context, action string, input []byte) (json.RawMessage, error) {
	if m.v2Store == "" || !filepath.IsAbs(m.v2Store) {
		return nil, v2Failure{"uninitialized"}
	}
	if !separateV2Store(m.base, m.v2Store) {
		return nil, v2Failure{"uncertain"}
	}
	if action != "read" && action != "save" && action != "connection" {
		return nil, v2Failure{"invalid"}
	}
	helper := filepath.Join(filepath.Dir(m.generator), "admin_deployment_v2.py")
	info, err := os.Lstat(helper)
	if err != nil || !info.Mode().IsRegular() {
		return nil, v2Failure{"uncertain"}
	}
	// Neither a password nor a public request enters argv, a shell or a log.
	cmd := exec.CommandContext(ctx, "python3", "-E", "-s", helper, "--action", action, "--store", m.v2Store)
	cmd.Stdin = bytes.NewReader(input)
	var output v2LimitedOutput
	cmd.Stdout = &output
	cmd.Stderr = io.Discard
	execErr := cmd.Run()
	var result struct {
		OK    bool            `json:"ok"`
		Value json.RawMessage `json:"value"`
		Code  string          `json:"code"`
		Error string          `json:"error"`
	}
	if !v2Envelope(output.Bytes()) || decodeV2Result(output.Bytes(), &result) != nil {
		return nil, v2Failure{"uncertain"}
	}
	if !result.OK {
		if execErr == nil {
			return nil, v2Failure{"uncertain"}
		}
		switch result.Code {
		case "invalid", "conflict", "uninitialized", "uncertain":
			return nil, v2Failure{result.Code}
		default:
			return nil, v2Failure{"uncertain"}
		}
	}
	if execErr != nil || len(result.Value) == 0 {
		return nil, v2Failure{"uncertain"}
	}
	return result.Value, nil
}
func (s *webServer) v2Error(w http.ResponseWriter, err error) {
	var failure v2Failure
	if errors.As(err, &failure) {
		switch failure.code {
		case "invalid":
			s.fail(w, 400, "草稿或请求无效，未确认保存；请检查配置")
			return
		case "conflict":
			s.fail(w, 409, "草稿已被其他操作更新；请保留本地修改并重新读取")
			return
		case "uninitialized":
			s.fail(w, 503, "版本 2 私密草稿尚未显式初始化")
			return
		}
	}
	// A killed helper may already have committed. Never promise no mutation or
	// automatically replay a request after a timeout or an uncertain fsync.
	s.fail(w, 503, "草稿操作结果待确认；请重新读取后再操作")
}
func (s *webServer) handleV2(w http.ResponseWriter, r *http.Request, ctx context.Context) bool {
	path := r.URL.Path
	if path != "/api/v2/extensions" && path != "/api/v2/connection" {
		return false
	}
	if s.m.v2Store == "" {
		s.fail(w, 404, "未配置版本 2 私密草稿入口")
		return true
	}
	action := ""
	switch {
	case path == "/api/v2/extensions" && r.Method == "GET":
		action = "read"
	case path == "/api/v2/extensions" && r.Method == "PUT":
		action = "save"
	case path == "/api/v2/connection" && r.Method == "POST":
		action = "connection"
	default:
		s.fail(w, 405, "不支持的版本 2 操作")
		return true
	}
	var input []byte
	if r.Method != "GET" {
		var err error
		input, err = io.ReadAll(http.MaxBytesReader(w, r.Body, 65536))
		if err != nil {
			s.fail(w, 400, "请求须为最多 65536 字节的 JSON")
			return true
		}
	}
	value, err := s.m.v2Call(ctx, action, input)
	if err != nil {
		s.v2Error(w, err)
		return true
	}
	if action == "connection" {
		var result v2Connection
		if !v2ValidConnection(value, input, &result) {
			s.v2Error(w, v2Failure{"uncertain"})
			return true
		}
		s.json(w, 200, result)
	} else {
		var result v2Snapshot
		if !v2ValidSnapshot(value, &result) {
			s.v2Error(w, v2Failure{"uncertain"})
			return true
		}
		s.json(w, 200, result)
	}
	return true
}

func separateV2Store(base, store string) bool {
	if store == "" {
		return true
	}
	if !v2PathWithoutAliases(base) || !v2PathWithoutAliases(store) {
		return false
	}
	for _, pair := range [][2]string{{base, store}, {store, base}} {
		rel, err := filepath.Rel(filepath.Clean(pair[0]), filepath.Clean(pair[1]))
		if err != nil || rel == "." || rel != ".." && !bytes.HasPrefix([]byte(rel), []byte(".."+string(filepath.Separator))) {
			return false
		}
	}
	return true
}
