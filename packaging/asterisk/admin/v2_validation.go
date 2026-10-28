package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/netip"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"unicode"
	"unicode/utf8"
)

var v2RevisionPattern = regexp.MustCompile(`^[A-Za-z0-9_-]{43}$`)
var v2CredentialPattern = regexp.MustCompile(`^[A-Za-z0-9_-]{12,128}$`)

// encoding/json otherwise accepts an unpaired escaped UTF-16 surrogate by
// substituting U+FFFD. Reject malformed helper Unicode instead of repairing it.
func v2UnicodeJSON(raw []byte) bool {
	if !utf8.Valid(raw) {
		return false
	}
	inString := false
	for i := 0; i < len(raw); i++ {
		if raw[i] == '"' {
			inString = !inString
			continue
		}
		if !inString || raw[i] != '\\' {
			continue
		}
		i++
		if i >= len(raw) {
			return false
		}
		if raw[i] != 'u' {
			continue
		}
		if i+4 >= len(raw) {
			return false
		}
		value, err := strconv.ParseUint(string(raw[i+1:i+5]), 16, 16)
		if err != nil {
			return false
		}
		i += 4
		if value >= 0xDC00 && value <= 0xDFFF {
			return false
		}
		if value < 0xD800 || value > 0xDBFF {
			continue
		}
		if i+6 >= len(raw) || raw[i+1] != '\\' || raw[i+2] != 'u' {
			return false
		}
		low, err := strconv.ParseUint(string(raw[i+3:i+7]), 16, 16)
		if err != nil || low < 0xDC00 || low > 0xDFFF {
			return false
		}
		i += 6
	}
	return !inString
}
func strictV2JSON(raw []byte) bool {
	if len(raw) == 0 || len(raw) > 65536 || !v2UnicodeJSON(raw) {
		return false
	}
	d := json.NewDecoder(bytes.NewReader(raw))
	d.UseNumber()
	var walk func(int) bool
	walk = func(depth int) bool {
		if depth > 64 {
			return false
		}
		token, err := d.Token()
		if err != nil {
			return false
		}
		delim, container := token.(json.Delim)
		if !container {
			return true
		}
		switch delim {
		case '{':
			seen := map[string]bool{}
			for d.More() {
				key, err := d.Token()
				name, ok := key.(string)
				if err != nil || !ok {
					return false
				}
				folded := strings.ToLower(name)
				if seen[folded] {
					return false
				}
				seen[folded] = true
				if !walk(depth + 1) {
					return false
				}
			}
			end, err := d.Token()
			return err == nil && end == json.Delim('}')
		case '[':
			for d.More() {
				if !walk(depth + 1) {
					return false
				}
			}
			end, err := d.Token()
			return err == nil && end == json.Delim(']')
		default:
			return false
		}
	}
	if !walk(0) {
		return false
	}
	_, err := d.Token()
	return err == io.EOF
}
func v2Object(raw []byte, keys ...string) (map[string]json.RawMessage, bool) {
	var result map[string]json.RawMessage
	if json.Unmarshal(raw, &result) != nil || len(result) != len(keys) {
		return nil, false
	}
	for _, key := range keys {
		value, found := result[key]
		if !found || bytes.Equal(bytes.TrimSpace(value), []byte("null")) {
			return nil, false
		}
	}
	return result, true
}
func v2Envelope(raw []byte) bool {
	if !strictV2JSON(raw) {
		return false
	}
	var header struct {
		OK *bool `json:"ok"`
	}
	if json.Unmarshal(raw, &header) != nil || header.OK == nil {
		return false
	}
	if *header.OK {
		_, ok := v2Object(raw, "ok", "value")
		return ok
	}
	object, ok := v2Object(raw, "ok", "code", "error")
	if !ok {
		return false
	}
	var code, message string
	return json.Unmarshal(object["code"], &code) == nil && json.Unmarshal(object["error"], &message) == nil && len(message) <= 1024
}
func v2PrivateEndpoint(value string) bool {
	address, err := netip.ParseAddr(value)
	return err == nil && address.Zone() == "" && !address.Is4In6() && address.String() == value && (address.IsPrivate() || address.IsLoopback())
}
func v2ValidSnapshot(raw []byte, result *v2Snapshot) bool {
	object, ok := v2Object(raw, "schemaVersion", "revision", "state", "canApply", "sipServer", "sipPort", "features", "extensions")
	if !ok || decodeV2Result(raw, result) != nil {
		return false
	}
	if _, ok := v2Object(object["features"], "calls", "carrier", "messages", "sms"); !ok {
		return false
	}
	if result.SchemaVersion != 2 || !v2RevisionPattern.MatchString(result.Revision) || result.State != "saved-not-applied" || result.CanApply ||
		!v2PrivateEndpoint(result.SIPServer) || result.SIPPort < 1 || result.SIPPort > 65535 || result.Extensions == nil || len(result.Extensions) > 64 {
		return false
	}
	var rows []json.RawMessage
	if json.Unmarshal(object["extensions"], &rows) != nil || len(rows) != len(result.Extensions) {
		return false
	}
	seen := map[string]bool{}
	for index, row := range result.Extensions {
		if _, ok := v2Object(rows[index], "number", "sourceNumber", "name", "enabled", "ring", "maxContacts", "allowOutgoing", "allowMessages", "allowMobileSmsSend", "allowMobileSmsReceive", "hasPassword"); !ok {
			return false
		}
		if !numberPattern.MatchString(row.Number) || row.SourceNumber != row.Number || seen[row.Number] || row.MaxContacts < 1 || row.MaxContacts > 8 ||
			len(row.Name) > 80 || strings.ContainsAny(row.Name, ";<>\"\\$") || strings.ContainsFunc(row.Name, unicode.IsControl) {
			return false
		}
		seen[row.Number] = true
	}
	return true
}
func v2ValidConnection(raw, query []byte, result *v2Connection) bool {
	if !strictV2JSON(query) {
		return false
	}
	if _, ok := v2Object(raw, "revision", "state", "server", "port", "username", "password", "transport"); !ok {
		return false
	}
	if _, ok := v2Object(query, "revision", "number"); !ok {
		return false
	}
	var chosen struct {
		Revision string `json:"revision"`
		Number   string `json:"number"`
	}
	if decodeV2Result(query, &chosen) != nil || decodeV2Result(raw, result) != nil {
		return false
	}
	return v2RevisionPattern.MatchString(chosen.Revision) && numberPattern.MatchString(chosen.Number) &&
		result.Revision == chosen.Revision && result.Username == chosen.Number && result.State == "saved-not-applied" &&
		v2PrivateEndpoint(result.Server) && result.Port >= 1 && result.Port <= 65535 && v2CredentialPattern.MatchString(result.Password) && result.Transport == "UDP"
}
func v2PathWithoutAliases(path string) bool {
	if !filepath.IsAbs(path) {
		return false
	}
	for _, part := range strings.Split(filepath.ToSlash(path), "/") {
		if part == "." || part == ".." {
			return false
		}
	}
	for current := filepath.Clean(path); ; current = filepath.Dir(current) {
		info, err := os.Lstat(current)
		if err != nil && !errors.Is(err, os.ErrNotExist) {
			return false
		}
		if err == nil && (info.Mode()&os.ModeSymlink != 0 || !info.IsDir()) {
			return false
		}
		if current == filepath.Dir(current) {
			return true
		}
	}
}
