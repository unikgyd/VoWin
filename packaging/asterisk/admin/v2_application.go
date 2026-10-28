package main

// Controlled PBX application is a separate, explicitly enrolled Linux-root
// capability. Requests are durable; HTTP cancellation never cancels a mutator.
// This file does not install files, simulate Docker, or replace Python's journal.
import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"sync"
	"syscall"
	"time"
)

type v2ApplicationOptions struct {
	Enabled              bool
	Image, RestartPolicy string
	ConfirmManagement    bool
}

type v2ApplicationOperation struct {
	Operation       *string  `json:"operation"`
	Revision        *string  `json:"revision"`
	State           string   `json:"state"`
	FileCount       int      `json:"fileCount"`
	ChangedFiles    []string `json:"changedFiles"`
	PBXApplied      bool     `json:"pbxApplied"`
	GatewayApplied  bool     `json:"gatewayApplied"`
	FirewallApplied bool     `json:"firewallApplied"`
	StatusSource    string   `json:"statusSource"`
}

type v2ApplicationInstalled struct {
	Operation string `json:"operation"`
	Revision  string `json:"revision"`
}

type v2ApplicationOverview struct {
	SchemaVersion   int                     `json:"schemaVersion"`
	Busy            bool                    `json:"busy"`
	Operation       *v2ApplicationOperation `json:"operation"`
	Installed       *v2ApplicationInstalled `json:"installed"`
	InstalledState  string                  `json:"installedState"`
	StatusSource    string                  `json:"statusSource"`
	GatewayApplied  bool                    `json:"gatewayApplied"`
	FirewallApplied bool                    `json:"firewallApplied"`
}

type v2ApplicationJob struct {
	Request   string                  `json:"request"`
	Action    string                  `json:"action"`
	State     string                  `json:"state"`
	Revision  string                  `json:"revision"`
	Operation string                  `json:"operation"`
	CreatedAt string                  `json:"createdAt"`
	UpdatedAt string                  `json:"updatedAt"`
	Result    *v2ApplicationOperation `json:"result"`
	ErrorCode string                  `json:"errorCode"`
}

type v2ApplicationResponse struct {
	Capability struct {
		Enabled bool `json:"enabled"`
	} `json:"capability"`
	Overview *v2ApplicationOverview `json:"overview"`
	Job      *v2ApplicationJob      `json:"job"`
}

type applicationPayload struct {
	Action    string `json:"action"`
	Revision  string `json:"revision"`
	Operation string `json:"operation"`
	Confirm   bool   `json:"confirm"`
	Binding   string `json:"binding"`
}

type applicationRecord struct {
	Request       string                  `json:"request"`
	Payload       applicationPayload      `json:"payload"`
	PayloadSHA256 string                  `json:"payloadSHA256"`
	State         string                  `json:"state"`
	CreatedAt     string                  `json:"createdAt"`
	UpdatedAt     string                  `json:"updatedAt"`
	Result        *v2ApplicationOperation `json:"result"`
	ErrorCode     string                  `json:"errorCode"`
}

type applicationStore struct {
	Version int                 `json:"version"`
	Latest  string              `json:"latest"`
	Jobs    []applicationRecord `json:"jobs"`
}

type v2ApplicationController struct {
	mu                               sync.Mutex
	m                                *manager
	options                          v2ApplicationOptions
	directory, helper, binding       string
	dir, lease                       *os.File
	state                            applicationStore
	stateSHA                         string
	owned, active, closing, poisoned bool
	done                             chan struct{}
	// Substituted only by offline unit tests. Production uses fixed argv and a
	// real inherited lease descriptor, with bounded stdout and discarded stderr.
	mutate  func([]string, *os.File) ([]byte, error)
	observe func(context.Context, []string) ([]byte, error)
}

var applicationImage = regexp.MustCompile(`^sha256:[a-f0-9]{64}$`)
var applicationDigest = regexp.MustCompile(`^[a-f0-9]{64}$`)
var applicationTemporary = regexp.MustCompile(`^\.write-[A-Za-z0-9_-]{43}$`)
var applicationFiles = map[string]bool{
	"config/asterisk.conf": true, "config/modules.conf": true, "config/logger.conf": true,
	"config/rtp.conf": true, "config/http.conf": true, "config/manager.conf": true,
	"config/pjsip.conf": true, "config/extensions.conf": true, "config/stasis.conf": true,
	"config/udptl.conf": true, "config/acl.conf": true, "config/indications.conf": true,
	"config/features.conf": true, "config/cel.conf": true, "config/cdr.conf": true,
	"config/ccss.conf": true, "config/pjproject.conf": true, "gateway-client.json": true,
	"deployment.normalized.json": true, "sip-guard.nft": true,
}
var errApplication = errors.New("controlled PBX application could not be safely confirmed")

const applicationStoreLimit = 1 << 20
const applicationJobLimit = 256 // No TTL: bindings are never silently forgotten.

func applicationSHA(b []byte) string { value := sha256.Sum256(b); return hex.EncodeToString(value[:]) }
func applicationNow() string         { return time.Now().UTC().Format(time.RFC3339Nano) }
func applicationKeys(raw []byte, keys ...string) bool {
	var object map[string]json.RawMessage
	if json.Unmarshal(raw, &object) != nil || len(object) != len(keys) {
		return false
	}
	for _, key := range keys {
		if _, ok := object[key]; !ok {
			return false
		}
	}
	return true
}
func applicationNonNull(raw []byte, nullable ...string) bool {
	var object map[string]json.RawMessage
	if json.Unmarshal(raw, &object) != nil {
		return false
	}
	allowed := map[string]bool{}
	for _, key := range nullable {
		allowed[key] = true
	}
	for key, value := range object {
		if bytes.Equal(bytes.TrimSpace(value), []byte("null")) && !allowed[key] {
			return false
		}
	}
	return true
}
func applicationJSON(raw []byte, value any, limit int) bool {
	if len(raw) == 0 || len(raw) > limit || !v2UnicodeJSON(raw) {
		return false
	}
	d := json.NewDecoder(bytes.NewReader(raw))
	d.UseNumber()
	var walk func(int) bool
	walk = func(depth int) bool {
		if depth > 64 {
			return false
		}
		token, e := d.Token()
		if e != nil {
			return false
		}
		delim, ok := token.(json.Delim)
		if !ok {
			return true
		}
		if delim == '{' {
			seen := map[string]bool{}
			for d.More() {
				key, e := d.Token()
				name, ok := key.(string)
				if e != nil || !ok || seen[strings.ToLower(name)] {
					return false
				}
				seen[strings.ToLower(name)] = true
				if !walk(depth + 1) {
					return false
				}
			}
			end, e := d.Token()
			return e == nil && end == json.Delim('}')
		}
		if delim == '[' {
			for d.More() {
				if !walk(depth + 1) {
					return false
				}
			}
			end, e := d.Token()
			return e == nil && end == json.Delim(']')
		}
		return false
	}
	if !walk(0) {
		return false
	}
	if _, e := d.Token(); e != io.EOF {
		return false
	}
	d = json.NewDecoder(bytes.NewReader(raw))
	d.DisallowUnknownFields()
	return d.Decode(value) == nil && d.Decode(&struct{}{}) == io.EOF
}
func applicationOperationValid(v *v2ApplicationOperation, allowEmpty bool) bool {
	if v == nil || v.ChangedFiles == nil || v.GatewayApplied || v.FirewallApplied || v.StatusSource != "journal" {
		return false
	}
	if v.Operation == nil || v.Revision == nil {
		return allowEmpty && v.Operation == nil && v.Revision == nil && v.State == "uninitialized" && v.FileCount == 0 && len(v.ChangedFiles) == 0 && !v.PBXApplied
	}
	if !v2RevisionPattern.MatchString(*v.Operation) || !v2RevisionPattern.MatchString(*v.Revision) || v.FileCount != 20 {
		return false
	}
	switch v.State {
	case "prepared", "stopping", "installing", "starting", "recovery-required", "applied", "rolled-back":
	default:
		return false
	}
	if v.PBXApplied != (v.State == "applied") {
		return false
	}
	seen := map[string]bool{}
	for _, file := range v.ChangedFiles {
		if !applicationFiles[file] || seen[file] {
			return false
		}
		seen[file] = true
	}
	return true
}
func applicationOperationJSON(raw []byte, v *v2ApplicationOperation, allowEmpty bool) bool {
	return applicationKeys(raw, "operation", "revision", "state", "fileCount", "changedFiles", "pbxApplied", "gatewayApplied", "firewallApplied", "statusSource") &&
		applicationNonNull(raw, "operation", "revision") && applicationJSON(raw, v, 65536) && applicationOperationValid(v, allowEmpty)
}
func applicationOverviewJSON(raw []byte, v *v2ApplicationOverview) bool {
	if !applicationKeys(raw, "schemaVersion", "busy", "operation", "installed", "installedState", "statusSource", "gatewayApplied", "firewallApplied") || !applicationNonNull(raw, "operation", "installed") || !applicationJSON(raw, v, 65536) || v.SchemaVersion != 1 || v.StatusSource != "journal" || v.GatewayApplied || v.FirewallApplied {
		return false
	}
	var object map[string]json.RawMessage
	_ = json.Unmarshal(raw, &object)
	if v.Operation != nil && !applicationOperationJSON(object["operation"], v.Operation, false) {
		return false
	}
	if v.Installed != nil && (!applicationKeys(object["installed"], "operation", "revision") || !applicationNonNull(object["installed"]) || !v2RevisionPattern.MatchString(v.Installed.Operation) || !v2RevisionPattern.MatchString(v.Installed.Revision)) {
		return false
	}
	switch v.InstalledState {
	case "unknown", "known", "unconfirmed", "drifted":
	default:
		return false
	}
	if v.Busy {
		return v.Operation == nil && v.Installed == nil && v.InstalledState == "unconfirmed"
	}
	if v.Operation == nil {
		return v.Installed == nil && v.InstalledState == "unknown"
	}
	if v.InstalledState == "known" && v.Installed == nil {
		return false
	}
	if v.InstalledState == "drifted" && v.Installed == nil {
		return false
	}
	switch v.Operation.State {
	case "stopping", "installing", "starting", "recovery-required":
		if v.InstalledState != "unconfirmed" {
			return false
		}
	}
	if v.InstalledState == "known" && v.Operation.State == "applied" && (v.Installed.Operation != *v.Operation.Operation || v.Installed.Revision != *v.Operation.Revision) {
		return false
	}
	if v.InstalledState == "unknown" && v.Installed != nil {
		return false
	}
	return true
}
func applicationCloneOperation(v *v2ApplicationOperation) *v2ApplicationOperation {
	if v == nil {
		return nil
	}
	result := *v
	result.ChangedFiles = append([]string{}, v.ChangedFiles...)
	if v.Operation != nil {
		x := *v.Operation
		result.Operation = &x
	}
	if v.Revision != nil {
		x := *v.Revision
		result.Revision = &x
	}
	return &result
}
func applicationCloneState(s applicationStore) applicationStore {
	result := s
	result.Jobs = append([]applicationRecord{}, s.Jobs...)
	for i := range result.Jobs {
		result.Jobs[i].Result = applicationCloneOperation(s.Jobs[i].Result)
	}
	return result
}
func applicationPayloadSHA(p applicationPayload) string {
	raw, _ := json.Marshal(p)
	return applicationSHA(raw)
}
func applicationRecordValid(r applicationRecord) bool {
	if !v2RevisionPattern.MatchString(r.Request) || !applicationDigest.MatchString(r.Payload.Binding) || !r.Payload.Confirm || r.PayloadSHA256 != applicationPayloadSHA(r.Payload) {
		return false
	}
	switch r.Payload.Action {
	case "prepare":
		if !v2RevisionPattern.MatchString(r.Payload.Revision) || r.Payload.Operation != "" {
			return false
		}
	case "apply":
		if !v2RevisionPattern.MatchString(r.Payload.Revision) || !v2RevisionPattern.MatchString(r.Payload.Operation) {
			return false
		}
	case "recover":
		if r.Payload.Revision != "" || !v2RevisionPattern.MatchString(r.Payload.Operation) {
			return false
		}
	default:
		return false
	}
	created, e := time.Parse(time.RFC3339Nano, r.CreatedAt)
	if e != nil || created.UTC().Format(time.RFC3339Nano) != r.CreatedAt {
		return false
	}
	updated, e := time.Parse(time.RFC3339Nano, r.UpdatedAt)
	if e != nil || updated.Before(created) || updated.UTC().Format(time.RFC3339Nano) != r.UpdatedAt {
		return false
	}
	if r.Result != nil && (!applicationOperationValid(r.Result, false) || (r.Payload.Operation != "" && *r.Result.Operation != r.Payload.Operation) || (r.Payload.Revision != "" && *r.Result.Revision != r.Payload.Revision)) {
		return false
	}
	switch r.State {
	case "queued", "running":
		return r.Result == nil && r.ErrorCode == ""
	case "uncertain":
		return r.Result == nil && r.ErrorCode == "uncertain"
	case "failed":
		if r.ErrorCode == "rolled-back" {
			return r.Payload.Action == "apply" && r.Result != nil && r.Result.State == "rolled-back"
		}
		return r.Result == nil && (r.ErrorCode == "invalid" || r.ErrorCode == "conflict" || r.ErrorCode == "busy")
	case "succeeded":
		return r.ErrorCode == "" && r.Result != nil && ((r.Payload.Action == "prepare" && r.Result.State == "prepared") || (r.Payload.Action == "apply" && r.Result.State == "applied") || (r.Payload.Action == "recover" && r.Result.State == "rolled-back"))
	}
	return false
}
func applicationDecodeStore(raw []byte) (applicationStore, error) {
	var s applicationStore
	if !applicationKeys(raw, "version", "latest", "jobs") || !applicationNonNull(raw) || !applicationJSON(raw, &s, applicationStoreLimit) || s.Version != 1 || s.Jobs == nil || len(s.Jobs) > applicationJobLimit {
		return s, errApplication
	}
	var shape struct {
		Jobs []json.RawMessage `json:"jobs"`
	}
	_ = json.Unmarshal(raw, &shape)
	seen := map[string]bool{}
	for i, r := range s.Jobs {
		if !applicationKeys(shape.Jobs[i], "request", "payload", "payloadSHA256", "state", "createdAt", "updatedAt", "result", "errorCode") || !applicationNonNull(shape.Jobs[i], "result") || !applicationRecordValid(r) || seen[r.Request] {
			return s, errApplication
		}
		var row map[string]json.RawMessage
		_ = json.Unmarshal(shape.Jobs[i], &row)
		if !applicationKeys(row["payload"], "action", "revision", "operation", "confirm", "binding") || !applicationNonNull(row["payload"]) {
			return s, errApplication
		}
		if r.Result != nil && !applicationOperationJSON(row["result"], r.Result, false) {
			return s, errApplication
		}
		seen[r.Request] = true
	}
	if (len(s.Jobs) == 0 && s.Latest != "") || (len(s.Jobs) > 0 && s.Latest != s.Jobs[len(s.Jobs)-1].Request) {
		return s, errApplication
	}
	return s, nil
}

func applicationStat(f *os.File, directory, private bool) error {
	var info syscall.Stat_t
	if syscall.Fstat(int(f.Fd()), &info) != nil || info.Uid != 0 {
		return errApplication
	}
	mode := info.Mode & 07777
	if directory {
		if info.Mode&syscall.S_IFMT != syscall.S_IFDIR || info.Nlink < 1 {
			return errApplication
		}
		if private && mode != 0700 {
			return errApplication
		}
		if !private && mode&0022 != 0 && mode&01000 == 0 {
			return errApplication
		}
	} else if info.Mode&syscall.S_IFMT != syscall.S_IFREG || mode != 0600 || info.Nlink != 1 {
		return errApplication
	}
	return nil
}
func applicationOpenDirectory(path string, private bool) (*os.File, error) {
	if !filepath.IsAbs(path) || filepath.Clean(path) != path || path == "/" || strings.ContainsAny(path, "\x00\\") {
		return nil, errApplication
	}
	fd, e := syscall.Open("/", syscall.O_RDONLY|syscall.O_DIRECTORY|syscall.O_NOFOLLOW|syscall.O_CLOEXEC, 0)
	if e != nil {
		return nil, errApplication
	}
	current := os.NewFile(uintptr(fd), "/")
	walked := "/"
	parts := strings.Split(strings.TrimPrefix(path, "/"), "/")
	for i, part := range parts {
		if part == "" || part == "." || part == ".." {
			current.Close()
			return nil, errApplication
		}
		next, e := syscall.Openat(int(current.Fd()), part, syscall.O_RDONLY|syscall.O_DIRECTORY|syscall.O_NOFOLLOW|syscall.O_CLOEXEC, 0)
		current.Close()
		if e != nil {
			return nil, errApplication
		}
		walked = filepath.Join(walked, part)
		current = os.NewFile(uintptr(next), walked)
		if applicationStat(current, true, private && i == len(parts)-1) != nil {
			current.Close()
			return nil, errApplication
		}
	}
	return current, nil
}
func applicationSameFile(a, b *os.File) bool {
	var x, y syscall.Stat_t
	return syscall.Fstat(int(a.Fd()), &x) == nil && syscall.Fstat(int(b.Fd()), &y) == nil && x.Dev == y.Dev && x.Ino == y.Ino
}
func applicationOpenFile(dir *os.File, name string, flags int) (*os.File, error) {
	fd, e := syscall.Openat(int(dir.Fd()), name, flags|syscall.O_NOFOLLOW|syscall.O_CLOEXEC|syscall.O_NONBLOCK, 0600)
	if e != nil {
		return nil, e
	}
	f := os.NewFile(uintptr(fd), name)
	if applicationStat(f, false, true) != nil {
		f.Close()
		return nil, errApplication
	}
	return f, nil
}
func applicationReadFile(dir *os.File, name string) ([]byte, error) {
	f, e := applicationOpenFile(dir, name, syscall.O_RDONLY)
	if e != nil {
		return nil, e
	}
	defer f.Close()
	before, e := f.Stat()
	if e != nil || before.Size() < 0 || before.Size() > applicationStoreLimit {
		return nil, errApplication
	}
	raw, e := io.ReadAll(io.LimitReader(f, applicationStoreLimit+1))
	after, statErr := f.Stat()
	if e != nil || statErr != nil || len(raw) > applicationStoreLimit || int64(len(raw)) != before.Size() || after.Size() != before.Size() || !after.ModTime().Equal(before.ModTime()) || applicationStat(f, false, true) != nil {
		return nil, errApplication
	}
	return raw, nil
}
func (c *v2ApplicationController) checkRoot() error {
	d, e := applicationOpenDirectory(c.directory, true)
	if e != nil {
		return errApplication
	}
	defer d.Close()
	if !applicationSameFile(d, c.dir) {
		return errApplication
	}
	l, e := applicationOpenFile(d, "lease", syscall.O_RDWR)
	if e != nil {
		return errApplication
	}
	defer l.Close()
	info, e := l.Stat()
	if e != nil || info.Size() != 0 || !applicationSameFile(l, c.lease) {
		return errApplication
	}
	return nil
}
func (c *v2ApplicationController) writeState(next applicationStore, initial bool) error {
	if c.checkRoot() != nil {
		return errApplication
	}
	raw, e := json.Marshal(next)
	if e != nil || len(raw) > applicationStoreLimit {
		return errApplication
	}
	raw = append(raw, '\n')
	if _, e = applicationDecodeStore(raw); e != nil {
		return errApplication
	}
	if !initial {
		current, e := applicationReadFile(c.dir, "state.json")
		if e != nil || applicationSHA(current) != c.stateSHA {
			return errApplication
		}
	}
	random := make([]byte, 32)
	if _, e = rand.Read(random); e != nil {
		return errApplication
	}
	name := ".write-" + base64.RawURLEncoding.EncodeToString(random)
	f, e := applicationOpenFile(c.dir, name, syscall.O_WRONLY|syscall.O_CREAT|syscall.O_EXCL)
	if e != nil {
		return errApplication
	}
	published := false
	defer func() {
		f.Close()
		if !published {
			_ = syscall.Unlinkat(int(c.dir.Fd()), name)
		}
	}()
	if _, e = f.Write(raw); e != nil {
		return errApplication
	}
	if f.Sync() != nil || f.Close() != nil || c.checkRoot() != nil {
		return errApplication
	}
	if !initial {
		current, e := applicationReadFile(c.dir, "state.json")
		if e != nil || applicationSHA(current) != c.stateSHA {
			return errApplication
		}
	} else {
		f, e := applicationOpenFile(c.dir, "state.json", syscall.O_RDONLY)
		if e == nil {
			f.Close()
			return errApplication
		}
		if !errors.Is(e, syscall.ENOENT) {
			return errApplication
		}
	}
	if syscall.Renameat(int(c.dir.Fd()), name, int(c.dir.Fd()), "state.json") != nil {
		return errApplication
	}
	published = true
	if c.dir.Sync() != nil {
		return errApplication
	}
	c.state = applicationCloneState(next)
	c.stateSHA = applicationSHA(raw)
	return nil
}
func (c *v2ApplicationController) loadState() error {
	if c.checkRoot() != nil {
		return errApplication
	}
	directory, e := applicationOpenDirectory(c.directory, true)
	if e != nil {
		return errApplication
	}
	defer directory.Close()
	if !applicationSameFile(directory, c.dir) {
		return errApplication
	}
	entries, e := directory.ReadDir(-1)
	if e != nil {
		return errApplication
	}
	for _, entry := range entries {
		if entry.Name() != "state.json" && entry.Name() != "lease" && !applicationTemporary.MatchString(entry.Name()) {
			return errApplication
		}
		if applicationTemporary.MatchString(entry.Name()) {
			f, e := applicationOpenFile(c.dir, entry.Name(), syscall.O_RDONLY)
			if e != nil {
				return errApplication
			}
			f.Close()
		}
	}
	raw, e := applicationReadFile(c.dir, "state.json")
	if e != nil {
		return errApplication
	}
	state, e := applicationDecodeStore(raw)
	if e != nil {
		return errApplication
	}
	c.state = state
	c.stateSHA = applicationSHA(raw)
	return nil
}
func (c *v2ApplicationController) acquire() error {
	if c.owned {
		return nil
	}
	if c.checkRoot() != nil {
		return errApplication
	}
	if e := syscall.Flock(int(c.lease.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); e != nil {
		return errBusy
	}
	c.owned = true
	if c.loadState() != nil {
		c.poisoned = true
		return errApplication
	}
	next := applicationCloneState(c.state)
	changed := false
	for i := range next.Jobs {
		if next.Jobs[i].State == "queued" || next.Jobs[i].State == "running" {
			next.Jobs[i].State = "uncertain"
			next.Jobs[i].ErrorCode = "uncertain"
			next.Jobs[i].UpdatedAt = applicationNow()
			changed = true
		}
	}
	if changed && c.writeState(next, false) != nil {
		c.poisoned = true
		return errApplication
	}
	return nil
}

func newV2ApplicationController(m *manager, options v2ApplicationOptions) (*v2ApplicationController, error) {
	if !options.Enabled {
		return nil, nil
	}
	if runtime.GOOS != "linux" || os.Geteuid() != 0 || !options.ConfirmManagement || !applicationImage.MatchString(options.Image) || (options.RestartPolicy != "no" && options.RestartPolicy != "unless-stopped") || m == nil || m.v2Store == "" || !containerPattern.MatchString(m.container) || !separateV2Store(m.base, m.v2Store) {
		return nil, errApplication
	}
	base, e := applicationOpenDirectory(m.base, true)
	if e != nil {
		return nil, errApplication
	}
	defer base.Close()
	store, e := applicationOpenDirectory(m.v2Store, true)
	if e != nil {
		return nil, errApplication
	}
	defer store.Close()
	helperDir, e := applicationOpenDirectory(filepath.Dir(m.generator), false)
	if e != nil {
		return nil, errApplication
	}
	defer helperDir.Close()
	helper := filepath.Join(filepath.Dir(m.generator), "admin_apply_v2.py")
	info, e := os.Lstat(helper)
	if e != nil || !info.Mode().IsRegular() || info.Mode().Perm()&0022 != 0 {
		return nil, errApplication
	}
	stat, ok := info.Sys().(*syscall.Stat_t)
	if !ok || stat.Uid != 0 || stat.Nlink != 1 {
		return nil, errApplication
	}
	created := false
	if e = syscall.Mkdirat(int(base.Fd()), ".v2-http-jobs", 0700); e == nil {
		created = true
		if base.Sync() != nil {
			return nil, errApplication
		}
	} else if !errors.Is(e, syscall.EEXIST) {
		return nil, errApplication
	}
	directory := filepath.Join(m.base, ".v2-http-jobs")
	dir, e := applicationOpenDirectory(directory, true)
	if e != nil {
		return nil, errApplication
	}
	lease, e := applicationOpenFile(dir, "lease", syscall.O_RDWR|syscall.O_CREAT)
	if e != nil {
		dir.Close()
		return nil, errApplication
	}
	leaseInfo, e := lease.Stat()
	if e != nil || leaseInfo.Size() != 0 {
		lease.Close()
		dir.Close()
		return nil, errApplication
	}
	bindingRaw, _ := json.Marshal(struct{ Base, Store, Helper, Container, Image, Restart string }{m.base, m.v2Store, helper, m.container, options.Image, options.RestartPolicy})
	c := &v2ApplicationController{m: m, options: options, directory: directory, helper: helper, binding: applicationSHA(bindingRaw), dir: dir, lease: lease, state: applicationStore{Version: 1, Jobs: []applicationRecord{}}}
	fail := func() (*v2ApplicationController, error) { lease.Close(); dir.Close(); return nil, errApplication }
	if created {
		if syscall.Flock(int(lease.Fd()), syscall.LOCK_EX|syscall.LOCK_NB) != nil {
			return fail()
		}
		c.owned = true
		if c.writeState(c.state, true) != nil {
			return fail()
		}
	} else if c.loadState() != nil {
		return fail()
	}
	if e = c.acquire(); e != nil && !errors.Is(e, errBusy) {
		return fail()
	}
	return c, nil
}

func (c *v2ApplicationController) Close() error {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.closing = true
	if c.active {
		return errBusy
	}
	if c.lease != nil {
		c.lease.Close()
		c.lease = nil
	}
	if c.dir != nil {
		c.dir.Close()
		c.dir = nil
	}
	return nil
}
func (c *v2ApplicationController) Shutdown(ctx context.Context) error {
	c.mu.Lock()
	c.closing = true
	if !c.active {
		c.mu.Unlock()
		return nil
	}
	done := c.done
	c.mu.Unlock()
	select {
	case <-done:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}
func (c *v2ApplicationController) cliArgs(action string, p applicationPayload) []string {
	args := []string{"-E", "-s", c.helper, "--action", action, "--base", c.m.base}
	if action == "overview" {
		return args
	}
	args = append(args, "--store", c.m.v2Store, "--container", c.m.container, "--image", c.options.Image, "--restart-policy", c.options.RestartPolicy)
	if action == "prepare" || action == "apply" {
		args = append(args, "--revision", p.Revision)
	}
	if action == "prepare" {
		args = append(args, "--confirm-v2-management")
	} else {
		args = append(args, "--operation", p.Operation, "--confirm-restart")
	}
	return args
}
func applicationEnvironment() []string {
	result := []string{}
	for _, item := range os.Environ() {
		name, _, _ := strings.Cut(item, "=")
		upper := strings.ToUpper(name)
		if strings.HasPrefix(upper, "VOCAT_") || strings.HasPrefix(upper, "VOWIN_") || strings.HasPrefix(upper, "PBX_ADMIN_") || strings.HasPrefix(upper, "DOCKER_") || strings.HasPrefix(upper, "PYTHON") || upper == "LC_ALL" {
			continue
		}
		result = append(result, item)
	}
	return append(result, "LC_ALL=C")
}
func applicationMutate(args []string, lease *os.File) ([]byte, error) {
	cmd := exec.Command("python3", args...)
	cmd.Env = applicationEnvironment()
	cmd.ExtraFiles = []*os.File{lease}
	cmd.Stdin = bytes.NewReader(nil)
	var output v2LimitedOutput
	cmd.Stdout = &output
	cmd.Stderr = io.Discard
	// No CommandContext: cancellation/timeout must not kill a committing Python
	// process. Product Docker commands themselves have bounded deadlines.
	err := cmd.Run()
	return output.Bytes(), err
}
func applicationObserve(ctx context.Context, args []string) ([]byte, error) {
	cmd := exec.CommandContext(ctx, "python3", args...)
	cmd.Env = applicationEnvironment()
	cmd.Stdin = bytes.NewReader(nil)
	var output v2LimitedOutput
	cmd.Stdout = &output
	cmd.Stderr = io.Discard
	cmd.WaitDelay = 500 * time.Millisecond
	err := cmd.Run()
	return output.Bytes(), err
}
func applicationEnvelope(raw []byte, execution error) (json.RawMessage, string, bool) {
	var envelope struct {
		OK    bool            `json:"ok"`
		Value json.RawMessage `json:"value"`
		Code  string          `json:"code"`
		Error string          `json:"error"`
	}
	if !v2Envelope(raw) || !applicationJSON(raw, &envelope, 65536) {
		return nil, "uncertain", false
	}
	if envelope.OK {
		if execution != nil {
			return nil, "uncertain", false
		}
		return envelope.Value, "", true
	}
	if execution == nil {
		return nil, "uncertain", false
	}
	switch envelope.Code {
	case "invalid", "conflict", "busy":
		return nil, envelope.Code, false
	}
	return nil, "uncertain", false
}
func (c *v2ApplicationController) run(request string) {
	c.mu.Lock()
	next := applicationCloneState(c.state)
	index := -1
	for i := range next.Jobs {
		if next.Jobs[i].Request == request {
			index = i
			break
		}
	}
	if index < 0 {
		c.active = false
		close(c.done)
		c.mu.Unlock()
		return
	}
	next.Jobs[index].State = "running"
	next.Jobs[index].UpdatedAt = applicationNow()
	p := next.Jobs[index].Payload
	if c.writeState(next, false) != nil {
		c.poisoned = true
		c.state.Jobs[index].State = "uncertain"
		c.state.Jobs[index].ErrorCode = "uncertain"
		c.active = false
		close(c.done)
		c.mu.Unlock()
		return
	}
	runner := c.mutate
	if runner == nil {
		runner = applicationMutate
	}
	args := c.cliArgs(p.Action, p)
	lease := c.lease
	c.mu.Unlock()
	raw, execution := runner(args, lease)
	value, code, success := applicationEnvelope(raw, execution)
	var result v2ApplicationOperation
	if success && (!applicationOperationJSON(value, &result, false) || result.Operation == nil || result.Revision == nil || (p.Action != "prepare" && *result.Operation != p.Operation) || (p.Revision != "" && *result.Revision != p.Revision)) {
		success = false
		code = "uncertain"
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	next = applicationCloneState(c.state)
	record := &next.Jobs[index]
	record.UpdatedAt = applicationNow()
	record.Result = nil
	if success {
		record.Result = applicationCloneOperation(&result)
		record.ErrorCode = ""
		record.State = "succeeded"
		if p.Action == "apply" && result.State == "rolled-back" {
			record.State = "failed"
			record.ErrorCode = "rolled-back"
		} else if (p.Action == "prepare" && result.State != "prepared") || (p.Action == "apply" && result.State != "applied") || (p.Action == "recover" && result.State != "rolled-back") {
			record.State = "uncertain"
			record.ErrorCode = "uncertain"
			record.Result = nil
		}
	} else if code == "invalid" || code == "conflict" || code == "busy" {
		record.State = "failed"
		record.ErrorCode = code
	} else {
		record.State = "uncertain"
		record.ErrorCode = "uncertain"
	}
	if c.writeState(next, false) != nil {
		c.poisoned = true
		c.state.Jobs[index].State = "uncertain"
		c.state.Jobs[index].ErrorCode = "uncertain"
		c.state.Jobs[index].Result = nil
		c.state.Jobs[index].UpdatedAt = applicationNow()
	}
	c.active = false
	close(c.done)
}
func applicationPublic(record applicationRecord, interrupted bool) *v2ApplicationJob {
	state, errorCode := record.State, record.ErrorCode
	if interrupted && (state == "queued" || state == "running") {
		state = "uncertain"
		errorCode = "uncertain"
	}
	result := applicationCloneOperation(record.Result)
	operation, revision := record.Payload.Operation, record.Payload.Revision
	if result != nil {
		operation = *result.Operation
		revision = *result.Revision
	}
	return &v2ApplicationJob{Request: record.Request, Action: record.Payload.Action, State: state, Revision: revision, Operation: operation, CreatedAt: record.CreatedAt, UpdatedAt: record.UpdatedAt, Result: result, ErrorCode: errorCode}
}
func (c *v2ApplicationController) selected(request string) (*v2ApplicationJob, bool) {
	if request == "" {
		request = c.state.Latest
	}
	if request == "" {
		return nil, true
	}
	for _, r := range c.state.Jobs {
		if r.Request == request {
			return applicationPublic(r, !c.owned), true
		}
	}
	return nil, false
}
func (c *v2ApplicationController) overview(ctx context.Context) (*v2ApplicationOverview, error) {
	ctx, cancel := context.WithTimeout(ctx, 2*time.Second)
	defer cancel()
	runner := c.observe
	if runner == nil {
		runner = applicationObserve
	}
	raw, execution := runner(ctx, c.cliArgs("overview", applicationPayload{}))
	value, _, success := applicationEnvelope(raw, execution)
	var result v2ApplicationOverview
	if !success || !applicationOverviewJSON(value, &result) {
		return nil, errApplication
	}
	return &result, nil
}
func (c *v2ApplicationController) reconcile(overview *v2ApplicationOverview) error {
	if !c.owned || c.active || c.poisoned || overview == nil || overview.Busy || overview.Operation == nil || overview.InstalledState == "unconfirmed" {
		return nil
	}
	op := overview.Operation
	if !applicationOperationValid(op, false) {
		return nil
	}
	next := applicationCloneState(c.state)
	changed := false
	for i := range next.Jobs {
		r := &next.Jobs[i]
		p := r.Payload
		if r.State != "uncertain" || p.Binding != c.binding || p.Operation == "" || p.Operation != *op.Operation || (p.Revision != "" && p.Revision != *op.Revision) {
			continue
		}
		if (p.Action == "apply" && op.State == "applied") || (p.Action == "recover" && op.State == "rolled-back") {
			r.State = "succeeded"
			r.ErrorCode = ""
		} else if p.Action == "apply" && op.State == "rolled-back" {
			r.State = "failed"
			r.ErrorCode = "rolled-back"
		} else {
			continue
		}
		r.Result = applicationCloneOperation(op)
		r.UpdatedAt = applicationNow()
		changed = true
	}
	if changed && c.writeState(next, false) != nil {
		c.poisoned = true
		return errApplication
	}
	return nil
}
func applicationRequest(raw []byte, action, binding string) (string, applicationPayload, bool) {
	p := applicationPayload{Action: action, Binding: binding}
	var input struct {
		Request   string `json:"request"`
		Revision  string `json:"revision"`
		Operation string `json:"operation"`
		Confirm   bool   `json:"confirm"`
	}
	keys := []string{"request", "revision", "confirm"}
	if action == "apply" {
		keys = append(keys, "operation")
	}
	if action == "recover" {
		keys = []string{"request", "operation", "confirm"}
	}
	if !applicationKeys(raw, keys...) || !applicationNonNull(raw) || !applicationJSON(raw, &input, 65536) || !v2RevisionPattern.MatchString(input.Request) || !input.Confirm {
		return "", p, false
	}
	p.Revision = input.Revision
	p.Operation = input.Operation
	p.Confirm = true
	if (action == "prepare" && (!v2RevisionPattern.MatchString(p.Revision) || p.Operation != "")) || (action == "apply" && (!v2RevisionPattern.MatchString(p.Revision) || !v2RevisionPattern.MatchString(p.Operation))) || (action == "recover" && (p.Revision != "" || !v2RevisionPattern.MatchString(p.Operation))) {
		return "", p, false
	}
	return input.Request, p, true
}
func (s *webServer) applicationError(w http.ResponseWriter, status int, code string) {
	s.json(w, status, map[string]string{"code": code, "error": "PBX 应用操作需重新核对状态；请勿自动重试"})
}
func (s *webServer) handleV2Application(w http.ResponseWriter, r *http.Request) bool {
	path := r.URL.Path
	if path != "/api/v2/application" && !strings.HasPrefix(path, "/api/v2/application/") {
		return false
	}
	controller := s.m.application
	if path == "/api/v2/application" && r.Method != "GET" {
		s.applicationError(w, 405, "method")
		return true
	}
	if path == "/api/v2/application" && r.Method == "GET" {
		query, e := url.ParseQuery(r.URL.RawQuery)
		if e != nil || len(query) > 1 || (len(query) == 1 && (len(query["request"]) != 1 || !v2RevisionPattern.MatchString(query.Get("request")))) {
			s.applicationError(w, 400, "invalid")
			return true
		}
		response := v2ApplicationResponse{}
		if controller == nil {
			s.json(w, 200, response)
			return true
		}
		response.Capability.Enabled = true
		controller.mu.Lock()
		if !controller.owned {
			e = controller.acquire()
			if errors.Is(e, errBusy) {
				e = controller.loadState()
			}
			if e != nil {
				controller.mu.Unlock()
				s.applicationError(w, 503, "uncertain")
				return true
			}
		}
		job, found := controller.selected(query.Get("request"))
		controller.mu.Unlock()
		if !found {
			s.applicationError(w, 404, "not-found")
			return true
		}
		overview, e := controller.overview(r.Context())
		if e != nil {
			s.applicationError(w, 503, "uncertain")
			return true
		}
		controller.mu.Lock()
		e = controller.reconcile(overview)
		if e == nil {
			job, _ = controller.selected(query.Get("request"))
		}
		controller.mu.Unlock()
		if e != nil {
			s.applicationError(w, 503, "uncertain")
			return true
		}
		response.Overview = overview
		response.Job = job
		s.json(w, 200, response)
		return true
	}
	action := strings.TrimPrefix(path, "/api/v2/application/")
	if action != "prepare" && action != "apply" && action != "recover" {
		s.applicationError(w, 404, "not-found")
		return true
	}
	if r.Method != "POST" {
		s.applicationError(w, 405, "method")
		return true
	}
	if controller == nil {
		s.applicationError(w, 403, "disabled")
		return true
	}
	if r.URL.RawQuery != "" {
		s.applicationError(w, 400, "invalid")
		return true
	}
	raw, e := io.ReadAll(http.MaxBytesReader(w, r.Body, 65536))
	if e != nil {
		s.applicationError(w, 400, "invalid")
		return true
	}
	request, payload, ok := applicationRequest(raw, action, controller.binding)
	if !ok {
		s.applicationError(w, 400, "invalid")
		return true
	}
	controller.mu.Lock()
	errorReply := func(status int, code string) bool {
		controller.mu.Unlock()
		s.applicationError(w, status, code)
		return true
	}
	replyExisting := func() bool {
		for _, record := range controller.state.Jobs {
			if record.Request == request {
				if record.PayloadSHA256 != applicationPayloadSHA(payload) {
					return errorReply(409, "conflict")
				}
				job := applicationPublic(record, !controller.owned)
				response := v2ApplicationResponse{Job: job}
				response.Capability.Enabled = true
				status := 200
				if job.State == "queued" || job.State == "running" {
					status = 202
				}
				controller.mu.Unlock()
				s.json(w, status, response)
				return true
			}
		}
		return false
	}
	if !controller.owned {
		if controller.loadState() != nil {
			return errorReply(503, "uncertain")
		}
		if replyExisting() {
			return true
		}
		if e = controller.acquire(); e != nil {
			status := 503
			code := "uncertain"
			if errors.Is(e, errBusy) {
				status = 409
				code = "busy"
			}
			return errorReply(status, code)
		}
	}
	if controller.poisoned {
		return errorReply(503, "uncertain")
	}
	if replyExisting() {
		return true
	}
	if controller.active || controller.closing {
		return errorReply(409, "busy")
	}
	if len(controller.state.Jobs) >= applicationJobLimit {
		return errorReply(503, "capacity")
	}
	next := applicationCloneState(controller.state)
	now := applicationNow()
	record := applicationRecord{Request: request, Payload: payload, PayloadSHA256: applicationPayloadSHA(payload), State: "queued", CreatedAt: now, UpdatedAt: now}
	next.Jobs = append(next.Jobs, record)
	next.Latest = request
	if controller.writeState(next, false) != nil {
		controller.poisoned = true
		return errorReply(503, "uncertain")
	}
	controller.active = true
	controller.done = make(chan struct{})
	go controller.run(request)
	response := v2ApplicationResponse{Job: applicationPublic(record, false)}
	response.Capability.Enabled = true
	controller.mu.Unlock()
	s.json(w, 202, response)
	return true
}
