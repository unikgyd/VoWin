package main

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"embed"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

// Independent optional PBX administration tool; no VoCat protocol code copied.
//
//go:embed web/*
var assets embed.FS

type extension struct {
	Number        string `json:"number"`
	Name          string `json:"name"`
	Enabled       bool   `json:"enabled"`
	Ring          bool   `json:"ring"`
	MaxContacts   int    `json:"maxContacts"`
	AllowOutgoing bool   `json:"allowOutgoing"`
	AllowMessages bool   `json:"allowMessages"`
	Password      string `json:"password,omitempty"`
	HasPassword   bool   `json:"hasPassword"`
}
type snapshot struct {
	Version         string      `json:"version"`
	Extensions      []extension `json:"extensions"`
	SIPServer       string      `json:"sipServer"`
	SIPPort         int         `json:"sipPort"`
	IncomingTarget  string      `json:"incomingTarget"`
	MessagesEnabled bool        `json:"messagesEnabled"`
}
type contact struct {
	Number  string `json:"number"`
	Address string `json:"address"`
	State   string `json:"state"`
}
type status struct {
	Running  bool      `json:"running"`
	Contacts []contact `json:"contacts"`
	Error    string    `json:"error,omitempty"`
}
type manager struct {
	mu                         sync.Mutex
	base, generator, container string
	v2Store                    string
	application                *v2ApplicationController
	run                        func(context.Context, string, ...string) ([]byte, error)
}
type configState struct {
	deployment map[string]json.RawMessage
	passwords  map[string]string
	view       snapshot
}

var numberPattern = regexp.MustCompile(`^[0-9]{2,8}$`)
var passwordPattern = regexp.MustCompile(`^[A-Za-z0-9_-]{16,128}$`)
var containerPattern = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$`)
var errConflict = errors.New("configuration changed; refresh before saving")
var errBusy = errors.New("end active calls before applying configuration")

func randomSecret() string {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return base64.RawURLEncoding.EncodeToString(b)
}
func command(ctx context.Context, name string, args ...string) ([]byte, error) {
	return exec.CommandContext(ctx, name, args...).CombinedOutput()
}
func readPrivate(path string) ([]byte, error) {
	info, err := os.Lstat(path)
	if err != nil {
		return nil, err
	}
	if !info.Mode().IsRegular() || info.Size() > 1<<20 {
		return nil, errors.New("invalid configuration file")
	}
	return os.ReadFile(path)
}
func (m *manager) load() (configState, error) {
	d, p := map[string]json.RawMessage{}, map[string]string{}
	db, err := readPrivate(filepath.Join(m.base, "deployment.local.json"))
	if err != nil {
		return configState{}, err
	}
	if json.Unmarshal(db, &d) != nil {
		return configState{}, errors.New("invalid private deployment")
	}
	for key := range d {
		if strings.EqualFold(key, "SchemaVersion") {
			// This legacy DTO reconstructs ExtensionOptions and cannot preserve
			// v2 role refs or independent SMS permissions. Fail before reading
			// legacy credentials, generating files, or reloading anything.
			return configState{}, errors.New("版本 2 配置需使用版本 2 管理入口；旧版分机编辑未修改任何配置")
		}
	}
	pb, err := readPrivate(filepath.Join(m.base, "credentials.json"))
	if err != nil {
		return configState{}, err
	}
	if json.Unmarshal(db, &d) != nil || json.Unmarshal(pb, &p) != nil {
		return configState{}, errors.New("invalid private deployment")
	}
	var numbers []string
	var options map[string]struct {
		Name          string
		Enabled       *bool
		Ring          *bool
		MaxContacts   int
		AllowOutgoing *bool
		AllowMessages *bool
	}
	if json.Unmarshal(d["Extensions"], &numbers) != nil {
		return configState{}, errors.New("missing extension list")
	}
	if len(d["ExtensionOptions"]) > 0 && json.Unmarshal(d["ExtensionOptions"], &options) != nil {
		return configState{}, errors.New("invalid extension options")
	}
	v := snapshot{Extensions: []extension{}}
	for _, n := range numbers {
		o := options[n]
		on := func(b *bool) bool { return b == nil || *b }
		max := o.MaxContacts
		if max == 0 {
			max = 4
		}
		// Import the original per-account contact limit, not a guessed default.
		if o.MaxContacts == 0 {
			if b, e := readPrivate(filepath.Join(m.base, "config", "pjsip.conf")); e == nil {
				max = importMaxContacts(string(b), n, max)
			}
		}
		v.Extensions = append(v.Extensions, extension{Number: n, Name: o.Name, Enabled: on(o.Enabled), Ring: on(o.Ring), MaxContacts: max, AllowOutgoing: on(o.AllowOutgoing), AllowMessages: on(o.AllowMessages), HasPassword: p[n] != ""})
	}
	_ = json.Unmarshal(d["MessageDomain"], &v.SIPServer)
	if v.SIPServer == "" {
		_ = json.Unmarshal(d["ServerIp"], &v.SIPServer)
	}
	_ = json.Unmarshal(d["SipPort"], &v.SIPPort)
	if v.SIPPort == 0 {
		v.SIPPort = 5060
	}
	_ = json.Unmarshal(d["IncomingTarget"], &v.IncomingTarget)
	_ = json.Unmarshal(d["MessagesEnabled"], &v.MessagesEnabled)
	h := sha256.New()
	for _, b := range [][]byte{db, pb} {
		_, _ = h.Write(b)
		_, _ = h.Write([]byte{0})
	}
	for _, name := range []string{"pjsip.conf", "extensions.conf"} {
		b, e := readPrivate(filepath.Join(m.base, "config", name))
		if e != nil {
			return configState{}, e
		}
		_, _ = h.Write(b)
		_, _ = h.Write([]byte{0})
	}
	v.Version = hex.EncodeToString(h.Sum(nil))
	return configState{d, p, v}, nil
}
func importMaxContacts(text, number string, fallback int) int {
	section := ""
	for _, line := range strings.Split(text, "\n") {
		line = strings.TrimSpace(line)
		if strings.HasPrefix(line, "[") {
			section = line
			continue
		}
		if section == "["+number+"]" && strings.HasPrefix(line, "max_contacts=") {
			if n, e := strconv.Atoi(strings.TrimSpace(strings.TrimPrefix(line, "max_contacts="))); e == nil && n >= 1 && n <= 8 {
				return n
			}
		}
	}
	return fallback
}
func (m *manager) cli(ctx context.Context, cmd string) ([]byte, error) {
	return m.run(ctx, "docker", "exec", m.container, "asterisk", "-rx", cmd)
}
func (m *manager) active(ctx context.Context) error {
	b, err := m.cli(ctx, "core show channels count")
	if err != nil {
		return errors.New("Asterisk is unavailable; nothing changed")
	}
	if !regexp.MustCompile(`(?m)^0 active channels\r?$`).Match(b) || !regexp.MustCompile(`(?m)^0 active calls\r?$`).Match(b) {
		return errBusy
	}
	return nil
}
func (m *manager) runtimeStatus(ctx context.Context) status {
	b, err := m.cli(ctx, "pjsip show contacts")
	if err != nil || strings.Contains(strings.ToLower(string(b)), "unable to") || strings.Contains(string(b), "No such command") {
		return status{Contacts: []contact{}, Error: "Asterisk 状态暂不可用"}
	}
	s := status{Running: true, Contacts: []contact{}}
	for _, line := range strings.Split(string(b), "\n") {
		fields := strings.Fields(line)
		if len(fields) < 4 || fields[0] != "Contact:" {
			continue
		}
		parts := strings.SplitN(fields[1], "/", 2)
		if len(parts) != 2 || !numberPattern.MatchString(parts[0]) {
			continue
		}
		s.Contacts = append(s.Contacts, contact{parts[0], parts[1], fields[3]})
	}
	sort.Slice(s.Contacts, func(i, j int) bool { return s.Contacts[i].Number < s.Contacts[j].Number })
	return s
}
func validateExtensions(rows []extension, old configState) error {
	if len(rows) < 1 || len(rows) > 64 {
		return errors.New("需要保留 1–64 个分机")
	}
	var trunk, echo string
	_ = json.Unmarshal(old.deployment["TrunkUsername"], &trunk)
	_ = json.Unmarshal(old.deployment["EchoExtension"], &echo)
	seen := map[string]bool{}
	for _, row := range rows {
		if !numberPattern.MatchString(row.Number) || seen[row.Number] || row.Number == trunk || row.Number == echo || row.Number == old.view.IncomingTarget {
			return errors.New("分机号须为不重复的 2–8 位数字，且不能占用服务号码")
		}
		seen[row.Number] = true
		if len([]byte(row.Name)) > 80 || strings.ContainsAny(row.Name, ";<>\"\\$\r\n") || strings.ContainsFunc(row.Name, func(c rune) bool { return c < 32 }) {
			return errors.New("显示名称包含不支持的字符")
		}
		if row.MaxContacts < 1 || row.MaxContacts > 8 {
			return errors.New("每个分机允许 1–8 个终端")
		}
		pass := row.Password
		existing := false
		for _, saved := range old.view.Extensions {
			if saved.Number == row.Number {
				existing = true
				break
			}
		}
		if pass == "" && existing {
			pass = old.passwords[row.Number]
		}
		if !passwordPattern.MatchString(pass) {
			return errors.New("新增／重置密码须为 16–128 位字母、数字、下划线或短横线")
		}
	}
	return nil
}
func secureWrite(path string, b []byte, mode os.FileMode) error {
	f, err := os.CreateTemp(filepath.Dir(path), ".pbx-write-")
	if err != nil {
		return err
	}
	name := f.Name()
	defer os.Remove(name)
	if err = f.Chmod(mode); err == nil {
		_, err = f.Write(b)
	}
	if err == nil {
		err = f.Sync()
	}
	closeErr := f.Close()
	if err != nil {
		return err
	}
	if closeErr != nil {
		return closeErr
	}
	return os.Rename(name, path)
}
func writeJSON(path string, v any) error {
	b, e := json.MarshalIndent(v, "", "  ")
	if e != nil {
		return e
	}
	return secureWrite(path, append(b, '\n'), 0600)
}
func (m *manager) reload(ctx context.Context) error {
	// "pjsip reload" is an optional CLI alias, absent in the minimal image.
	for _, cmd := range []string{"module reload res_pjsip.so", "dialplan reload"} {
		b, e := m.cli(ctx, cmd)
		if e != nil || strings.Contains(string(b), "No such command") || strings.Contains(string(b), "Unable to") {
			return errors.New("Asterisk reload failed")
		}
	}
	return nil
}
func (m *manager) apply(ctx context.Context, version string, rows []extension) (snapshot, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	lock := filepath.Join(m.base, ".admin-operation-lock")
	if os.Mkdir(lock, 0700) != nil {
		return snapshot{}, errors.New("另一个配置操作正在进行")
	}
	defer os.Remove(lock)
	old, err := m.load()
	if err != nil {
		return snapshot{}, errors.New("无法读取当前配置，未修改")
	}
	if old.view.Version != version {
		return snapshot{}, errConflict
	}
	if err = validateExtensions(rows, old); err != nil {
		return snapshot{}, err
	}
	if err = m.active(ctx); err != nil {
		return snapshot{}, err
	}
	stage, err := os.MkdirTemp(m.base, "admin-backup-")
	if err != nil {
		return snapshot{}, err
	}
	// Keep this private operation directory for recovery. It contains originals,
	// generated candidates and an outcome marker; never recursively delete it.
	originals := map[string][]byte{}
	for _, name := range []string{"deployment.local.json", "credentials.json", "config/pjsip.conf", "config/extensions.conf"} {
		b, e := readPrivate(filepath.Join(m.base, name))
		if e != nil {
			return snapshot{}, e
		}
		originals[name] = b
		if err = os.MkdirAll(filepath.Join(stage, "original", filepath.Dir(name)), 0700); err != nil {
			return snapshot{}, err
		}
		if err = secureWrite(filepath.Join(stage, "original", name), b, 0600); err != nil {
			return snapshot{}, err
		}
	}
	numbers := []string{}
	options := map[string]any{}
	for _, row := range rows {
		numbers = append(numbers, row.Number)
		options[row.Number] = map[string]any{"Name": row.Name, "Enabled": row.Enabled, "Ring": row.Ring, "MaxContacts": row.MaxContacts, "AllowOutgoing": row.AllowOutgoing, "AllowMessages": row.AllowMessages}
		if row.Password != "" {
			old.passwords[row.Number] = row.Password
		}
	}
	for _, saved := range old.view.Extensions {
		kept := false
		for _, row := range rows {
			if row.Number == saved.Number {
				kept = true
			}
		}
		if !kept {
			delete(old.passwords, saved.Number)
		}
	}
	old.deployment["Extensions"], _ = json.Marshal(numbers)
	old.deployment["ExtensionOptions"], _ = json.Marshal(options)
	if err = writeJSON(filepath.Join(stage, "deployment.local.json"), old.deployment); err != nil {
		return snapshot{}, err
	}
	if err = writeJSON(filepath.Join(stage, "credentials.json"), old.passwords); err != nil {
		return snapshot{}, err
	}
	if _, err = m.run(ctx, "python3", m.generator, "--config", filepath.Join(stage, "deployment.local.json"), "--output", stage); err != nil {
		return snapshot{}, errors.New("候选配置生成失败，原配置未变")
	}
	// Check for a manual edit while rendering, before replacing any live file.
	current, e := m.load()
	if e != nil || current.view.Version != version {
		return snapshot{}, errConflict
	}
	rollback := func() bool {
		ok := true
		for name, b := range originals {
			if m.install(name, b) != nil {
				ok = false
			}
		}
		recovery, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		if m.reload(recovery) != nil {
			ok = false
		}
		_ = secureWrite(filepath.Join(stage, "outcome.txt"), []byte("rollback attempted; original files retained\n"), 0600)
		return ok
	}
	abort := func(message string) (snapshot, error) {
		if rollback() {
			return snapshot{}, errors.New(message + "，已回滚并保留备份")
		}
		return snapshot{}, errors.New(message + "；回滚未完成，请检查服务器备份目录 " + filepath.Base(stage))
	}
	for _, name := range []string{"config/pjsip.conf", "config/extensions.conf", "credentials.json", "deployment.local.json"} {
		b, e := readPrivate(filepath.Join(stage, name))
		if e != nil {
			return abort("无法读取候选配置")
		}
		if e = m.install(name, b); e != nil {
			return abort("配置写入失败")
		}
	}
	if err = m.reload(ctx); err != nil {
		return abort("热加载失败")
	}
	// Verify actual endpoint objects, not merely the reload command's exit code.
	checks := map[string]bool{}
	for _, saved := range old.view.Extensions {
		checks[saved.Number] = false
	}
	for _, row := range rows {
		checks[row.Number] = row.Enabled
	}
	for number, wanted := range checks {
		b, e := m.cli(ctx, "pjsip show endpoint "+number)
		present := strings.Contains(string(b), "Endpoint:  "+number+"/") || strings.Contains(string(b), "Endpoint:  "+number+" ")
		if e != nil || present != wanted {
			return abort("分机加载校验失败")
		}
	}
	if err = secureWrite(filepath.Join(stage, "outcome.txt"), []byte("applied; original files retained\n"), 0600); err != nil {
		return snapshot{}, errors.New("已应用，但备份标记写入失败；请刷新")
	}
	next, err := m.load()
	return next.view, err
}
func (m *manager) install(name string, b []byte) error {
	mode := os.FileMode(0600)
	path := filepath.Join(m.base, name)
	if strings.HasPrefix(name, "config/") {
		mode = 0640
	}
	if err := secureWrite(path, b, mode); err != nil {
		return err
	}
	if strings.HasPrefix(name, "config/") && os.Geteuid() == 0 {
		return os.Chown(path, 10001, 10001)
	}
	return nil
}

type session struct {
	csrf  string
	until time.Time
}
type webServer struct {
	m        *manager
	key      string
	mu       sync.Mutex
	sessions map[string]session
	failures map[string][]time.Time
}

func (s *webServer) json(w http.ResponseWriter, code int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(value)
}
func (s *webServer) fail(w http.ResponseWriter, code int, text string) {
	s.json(w, code, map[string]string{"error": text})
}
func privatePeer(remote string) bool {
	host, _, err := net.SplitHostPort(remote)
	if err != nil {
		return false
	}
	ip := net.ParseIP(host)
	return ip != nil && (ip.IsLoopback() || ip.IsPrivate())
}
func (s *webServer) handler(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.Header().Set("Referrer-Policy", "no-referrer")
	w.Header().Set("X-Frame-Options", "DENY")
	w.Header().Set("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'")
	if !privatePeer(r.RemoteAddr) {
		s.fail(w, 403, "仅允许可信内网或 VPN")
		return
	}
	if r.Method != "GET" {
		if r.Header.Get("Content-Type") != "application/json" {
			s.fail(w, 415, "JSON required")
			return
		}
		if origin := r.Header.Get("Origin"); origin != "" && origin != "http://"+r.Host && origin != "https://"+r.Host {
			s.fail(w, 403, "Origin rejected")
			return
		}
	}
	decode := func(v any) bool {
		r.Body = http.MaxBytesReader(w, r.Body, 65536)
		d := json.NewDecoder(r.Body)
		d.DisallowUnknownFields()
		if d.Decode(v) != nil || d.Decode(&struct{}{}) != io.EOF {
			s.fail(w, 400, "请求格式错误")
			return false
		}
		return true
	}
	if r.URL.Path == "/api/login" && r.Method == "POST" {
		s.mu.Lock()
		now := time.Now()
		for peer, attempts := range s.failures {
			if len(attempts) == 0 || now.Sub(attempts[len(attempts)-1]) >= time.Minute {
				delete(s.failures, peer)
			}
		}
		host, _, _ := net.SplitHostPort(r.RemoteAddr)
		if _, known := s.failures[host]; !known && len(s.failures) >= 512 {
			s.mu.Unlock()
			s.fail(w, 429, "尝试过多，请稍后重试")
			return
		}
		kept := []time.Time{}
		for _, t := range s.failures[host] {
			if now.Sub(t) < time.Minute {
				kept = append(kept, t)
			}
		}
		s.failures[host] = kept
		if len(kept) >= 8 {
			s.mu.Unlock()
			s.fail(w, 429, "尝试过多，请稍后重试")
			return
		}
		s.mu.Unlock()
		var body struct {
			Key string `json:"key"`
		}
		if !decode(&body) {
			return
		}
		if subtle.ConstantTimeCompare([]byte(body.Key), []byte(s.key)) != 1 {
			s.mu.Lock()
			s.failures[host] = append(s.failures[host], now)
			s.mu.Unlock()
			s.fail(w, 401, "管理密钥不正确")
			return
		}
		token, csrf := randomSecret(), randomSecret()
		s.mu.Lock()
		for token, v := range s.sessions {
			if now.After(v.until) {
				delete(s.sessions, token)
			}
		}
		if len(s.sessions) >= 64 {
			s.mu.Unlock()
			s.fail(w, 429, "会话过多")
			return
		}
		s.sessions[token] = session{csrf, now.Add(8 * time.Hour)}
		s.mu.Unlock()
		http.SetCookie(w, &http.Cookie{Name: "pbx_admin", Value: token, Path: "/", HttpOnly: true, SameSite: http.SameSiteStrictMode, Secure: r.TLS != nil, MaxAge: 28800})
		s.json(w, 200, map[string]string{"csrf": csrf})
		return
	}
	if strings.HasPrefix(r.URL.Path, "/api/") {
		if r.URL.Path == "/api/configuration" && r.Method == "GET" {
			version := 1
			if s.m.v2Store != "" {
				version = 2
			}
			// Mode metadata only. No configuration, credential or state is read.
			s.json(w, 200, map[string]any{"schemaVersion": version, "canApply": version == 1})
			return
		}
		cookie, err := r.Cookie("pbx_admin")
		var auth session
		s.mu.Lock()
		if err == nil {
			auth = s.sessions[cookie.Value]
		}
		s.mu.Unlock()
		if auth.csrf == "" || time.Now().After(auth.until) {
			s.fail(w, 401, "请先登录")
			return
		}
		if r.Method != "GET" && subtle.ConstantTimeCompare([]byte(r.Header.Get("X-CSRF-Token")), []byte(auth.csrf)) != 1 {
			s.fail(w, 403, "请刷新会话后重试")
			return
		}
		// Application workers own their durable request/lease independently of
		// an HTTP connection; a browser timeout must never kill or replay them.
		if s.handleV2Application(w, r) {
			return
		}
		ctx, cancel := context.WithTimeout(r.Context(), 30*time.Second)
		defer cancel()
		if s.m.v2Store != "" && (r.URL.Path == "/api/extensions" || r.URL.Path == "/api/connection") {
			s.fail(w, 409, "当前使用版本 2 草稿；旧编辑与应用入口已关闭")
			return
		}
		if s.handleV2(w, r, ctx) {
			return
		}
		switch {
		case r.URL.Path == "/api/session" && r.Method == "GET":
			s.json(w, 200, map[string]string{"csrf": auth.csrf})
		case r.URL.Path == "/api/logout" && r.Method == "POST":
			s.mu.Lock()
			delete(s.sessions, cookie.Value)
			s.mu.Unlock()
			http.SetCookie(w, &http.Cookie{Name: "pbx_admin", Path: "/", MaxAge: -1, HttpOnly: true, SameSite: http.SameSiteStrictMode})
			s.json(w, 200, map[string]bool{"ok": true})
		case r.URL.Path == "/api/extensions" && r.Method == "GET":
			s.m.mu.Lock()
			state, e := s.m.load()
			s.m.mu.Unlock()
			if e != nil {
				s.fail(w, 503, "配置暂不可读")
				return
			}
			s.json(w, 200, state.view)
		case r.URL.Path == "/api/status" && r.Method == "GET":
			s.json(w, 200, s.m.runtimeStatus(ctx))
		case r.URL.Path == "/api/extensions" && r.Method == "PUT":
			var body struct {
				Version    string      `json:"version"`
				Extensions []extension `json:"extensions"`
			}
			if !decode(&body) {
				return
			}
			v, e := s.m.apply(ctx, body.Version, body.Extensions)
			if e != nil {
				code := 400
				if errors.Is(e, errConflict) || errors.Is(e, errBusy) {
					code = 409
				}
				s.fail(w, code, e.Error())
				return
			}
			s.json(w, 200, v)
		case r.URL.Path == "/api/connection" && r.Method == "POST":
			var body struct {
				Number string `json:"number"`
			}
			if !decode(&body) {
				return
			}
			s.m.mu.Lock()
			state, e := s.m.load()
			s.m.mu.Unlock()
			if e != nil {
				s.fail(w, 503, "配置暂不可读")
				return
			}
			for _, ext := range state.view.Extensions {
				if ext.Number == body.Number {
					s.json(w, 200, map[string]any{"server": state.view.SIPServer, "port": state.view.SIPPort, "username": ext.Number, "password": state.passwords[ext.Number], "transport": "UDP"})
					return
				}
			}
			s.fail(w, 404, "分机不存在")
		default:
			s.fail(w, 405, "不支持的操作")
		}
		return
	}
	if r.Method != "GET" {
		s.fail(w, 405, "Method not allowed")
		return
	}
	name := strings.TrimPrefix(r.URL.Path, "/")
	if name == "" {
		name = "index.html"
	}
	if name != "index.html" && name != "app.js" && name != "style.css" && name != "v2-model.mjs" && name != "v2-app.mjs" && name != "v2-application-model.mjs" && name != "v2-application.mjs" {
		http.NotFound(w, r)
		return
	}
	b, err := assets.ReadFile("web/" + name)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	types := map[string]string{"index.html": "text/html; charset=utf-8", "app.js": "text/javascript; charset=utf-8", "style.css": "text/css; charset=utf-8", "v2-model.mjs": "text/javascript; charset=utf-8", "v2-app.mjs": "text/javascript; charset=utf-8", "v2-application-model.mjs": "text/javascript; charset=utf-8", "v2-application.mjs": "text/javascript; charset=utf-8"}
	w.Header().Set("Content-Type", types[name])
	_, _ = w.Write(b)
}
func main() {
	base := flag.String("base", "", "Existing generated PBX deployment directory (required)")
	gen := flag.String("generator", "", "Absolute configure.py path (required)")
	container := flag.String("container", "vowin-asterisk", "Docker container name")
	listen := flag.String("listen", "127.0.0.1:9088", "Private bind address")
	v2Store := flag.String("v2-store", "", "Explicit, separately initialized private v2 draft root")
	v2Application := flag.Bool("v2-application", false, "Explicitly enable controlled v2 PBX application jobs")
	v2Image := flag.String("v2-image", "", "Reviewed, already-local immutable sha256 image ID for application")
	v2RestartPolicy := flag.String("v2-restart-policy", "unless-stopped", "Declared PBX restart policy: no or unless-stopped")
	confirmManagement := flag.Bool("confirm-v2-management", false, "Approve permanent v2-only PBX writer discipline")
	flag.Parse()
	if *base == "" || *gen == "" || !filepath.IsAbs(*base) || !filepath.IsAbs(*gen) || !containerPattern.MatchString(*container) {
		log.Fatal("absolute base/generator paths and a valid container are required")
	}
	if !separateV2Store(*base, *v2Store) {
		log.Fatal("v2 store must be an absolute directory separate from the installed deployment")
	}
	host, _, e := net.SplitHostPort(*listen)
	ip := net.ParseIP(host)
	if e != nil || ip == nil || (!ip.IsPrivate() && !ip.IsLoopback()) {
		log.Fatal("bind to a specific private or loopback IP, not a wildcard/public address")
	}
	keyFile := filepath.Join(*base, "admin-access.local.txt")
	key, err := readPrivate(keyFile)
	if os.IsNotExist(err) {
		key = []byte(randomSecret() + "\n")
		f, e := os.OpenFile(keyFile, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
		if e != nil {
			log.Fatal("cannot create private management key")
		}
		_, e = f.Write(key)
		_ = f.Close()
		if e != nil {
			log.Fatal("cannot save management key")
		}
	} else if err != nil {
		log.Fatal("cannot read private management key")
	}
	if len(strings.TrimSpace(string(key))) < 32 {
		log.Fatal("management key is invalid")
	}
	m := &manager{base: *base, generator: *gen, container: *container, run: command, v2Store: *v2Store}
	if *v2Application {
		m.application, e = newV2ApplicationController(m, v2ApplicationOptions{
			Enabled: true, Image: *v2Image, RestartPolicy: *v2RestartPolicy, ConfirmManagement: *confirmManagement,
		})
		if e != nil {
			log.Fatal("cannot initialize explicitly enabled v2 application controller")
		}
		defer m.application.Close()
	}
	if *v2Store != "" {
		if m.application == nil || m.application.owned {
			check, cancel := context.WithTimeout(context.Background(), 10*time.Second)
			var value json.RawMessage
			value, e = m.v2Call(check, "read", nil)
			var draft v2Snapshot
			if e == nil && !v2ValidSnapshot(value, &draft) {
				e = v2Failure{"uncertain"}
			}
			cancel()
		} else {
			// A still-running inherited child can hold the draft's lock. Serve
			// authenticated read-only job status; do not fake a loaded draft,
			// downgrade to legacy mode, or replay its application.
			e = nil
			log.Print("application worker lease busy; startup draft read deferred")
		}
	} else {
		_, e = m.load()
	}
	if e != nil {
		log.Fatal("cannot load existing deployment")
	}
	s := &webServer{m: m, key: strings.TrimSpace(string(key)), sessions: map[string]session{}, failures: map[string][]time.Time{}}
	h := &http.Server{Addr: *listen, Handler: http.HandlerFunc(s.handler), ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 10 * time.Second, WriteTimeout: 40 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 16 << 10}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	go func() {
		<-ctx.Done()
		c, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = h.Shutdown(c)
	}()
	log.Printf("PBX management listening on %s; key saved in private admin-access.local.txt; no automatic reload", *listen)
	if err = h.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Fatal("management HTTP server stopped")
	}
	if m.application != nil {
		// Waiting may expire, but never cancel a committing child or call it
		// failed. Its inherited lease and durable request survive this parent.
		wait, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		_ = m.application.Shutdown(wait)
		cancel()
		_ = m.application.Close()
	}
}
