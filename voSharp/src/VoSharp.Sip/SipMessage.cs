using System.Text;

namespace VoSharp.Sip;

public class SipMessage
{
    public bool IsRequest { get; set; }
    public string Method { get; set; } = string.Empty;
    public string RequestUri { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ReasonPhrase { get; set; } = string.Empty;
    public string SipVersion { get; set; } = "SIP/2.0";

    public Dictionary<string, List<string>> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Body { get; set; } = string.Empty;
    public byte[]? RawBody { get; set; }
    // Receive-path context. Never serialized; copied onto responses to this request.
    internal Func<SipMessage, CancellationToken, Task>? ResponseSender { get; set; }

    public static string CanonicalHeaderName(string name) => name.Trim().ToLowerInvariant() switch
    {
        "v" => "Via",
        "f" => "From",
        "t" => "To",
        "i" => "Call-ID",
        "l" => "Content-Length",
        "c" => "Content-Type",
        "m" => "Contact",
        "e" => "Content-Encoding",
        "s" => "Subject",
        "k" => "Supported",
        _ => name.Trim()
    };

    public string? GetHeader(string name)
    {
        return Headers.TryGetValue(CanonicalHeaderName(name), out var list) && list.Count > 0 ? list[0] : null;
    }

    public void SetHeader(string name, string value)
    {
        Headers[CanonicalHeaderName(name)] = [value];
    }

    public void AddHeader(string name, string value)
    {
        name = CanonicalHeaderName(name);
        if (!Headers.TryGetValue(name, out var list))
        {
            list = new List<string>();
            Headers[name] = list;
        }
        list.Add(value);
    }

    public SipMessage CreateResponse(int statusCode, string reasonPhrase)
    {
        var resp = new SipMessage
        {
            IsRequest = false,
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
            SipVersion = SipVersion,
            ResponseSender = ResponseSender
        };

        if (Headers.TryGetValue("Via", out var viaList))
        {
            foreach (var v in viaList) resp.AddHeader("Via", v);
        }

        resp.SetHeader("From", GetHeader("From") ?? string.Empty);
        resp.SetHeader("To", GetHeader("To") ?? string.Empty);
        resp.SetHeader("Call-ID", GetHeader("Call-ID") ?? string.Empty);
        resp.SetHeader("CSeq", GetHeader("CSeq") ?? string.Empty);
        resp.SetHeader("Content-Length", "0");
        return resp;
    }

    public static SipMessage Parse(byte[] rawBytes)
    {
        int delimIdx = -1;
        int delimLen = 4;
        for (int j = 0; j < rawBytes.Length - 3; j++)
        {
            if (rawBytes[j] == 0x0D && rawBytes[j + 1] == 0x0A && rawBytes[j + 2] == 0x0D && rawBytes[j + 3] == 0x0A)
            {
                delimIdx = j;
                delimLen = 4;
                break;
            }
        }

        if (delimIdx == -1)
        {
            for (int j = 0; j < rawBytes.Length - 1; j++)
            {
                if (rawBytes[j] == 0x0A && rawBytes[j + 1] == 0x0A)
                {
                    delimIdx = j;
                    delimLen = 2;
                    break;
                }
            }
        }

        if (delimIdx == -1)
        {
            return ParseHeaderSection(Encoding.UTF8.GetString(rawBytes));
        }

        var headerText = Encoding.UTF8.GetString(rawBytes, 0, delimIdx);
        var msg = ParseHeaderSection(headerText);

        int bodyStart = delimIdx + delimLen;
        msg.ReadBody(rawBytes.AsSpan(bodyStart));

        return msg;
    }

    public static SipMessage Parse(string rawText)
    {
        int delim = rawText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        int delimLen = 4;
        if (delim < 0)
        {
            delim = rawText.IndexOf("\n\n", StringComparison.Ordinal);
            delimLen = 2;
        }

        if (delim < 0)
        {
            return ParseHeaderSection(rawText);
        }

        var headerText = rawText[..delim];
        var msg = ParseHeaderSection(headerText);
        msg.ReadBody(Encoding.Latin1.GetBytes(rawText[(delim + delimLen)..]));
        return msg;
    }

    private void ReadBody(ReadOnlySpan<byte> available)
    {
        var length = available.Length;
        if (Headers.TryGetValue("Content-Length", out var values))
        {
            if (values.Count != 1 || !int.TryParse(values[0], out length) || length < 0 || length > available.Length)
                throw new FormatException("Invalid or truncated SIP Content-Length.");
        }
        RawBody = available[..length].ToArray();
        Body = Encoding.Latin1.GetString(RawBody);
    }

    private static SipMessage ParseHeaderSection(string headerText)
    {
        var msg = new SipMessage();
        var lines = headerText.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0) return msg;

        var firstLine = lines[0].Trim();
        if (firstLine.StartsWith("SIP/2.0", StringComparison.OrdinalIgnoreCase))
        {
            msg.IsRequest = false;
            var parts = firstLine.Split(' ', 3);
            msg.SipVersion = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], out int sc))
                msg.StatusCode = sc;
            if (parts.Length > 2)
                msg.ReasonPhrase = parts[2];
        }
        else
        {
            msg.IsRequest = true;
            var parts = firstLine.Split(' ', 3);
            if (parts.Length > 0) msg.Method = parts[0];
            if (parts.Length > 1) msg.RequestUri = parts[1];
            if (parts.Length > 2) msg.SipVersion = parts[2];
        }

        string? previousName = null;
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) break;

            if (line[0] is ' ' or '\t')
            {
                if (previousName == null) throw new FormatException("SIP header continuation has no preceding field.");
                var values = msg.Headers[previousName];
                values[^1] += " " + line.Trim();
                continue;
            }

            int colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                var hName = CanonicalHeaderName(line[..colonIdx]);
                var hVal = line[(colonIdx + 1)..].Trim();
                msg.AddHeader(hName, hVal);
                previousName = hName;
            }
        }

        return msg;
    }

    public byte[] ToBytes()
    {
        var bodyBytes = RawBody ?? Encoding.Latin1.GetBytes(Body);
        SetHeader("Content-Length", bodyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var sb = new StringBuilder();
        if (IsRequest)
        {
            sb.Append($"{Method} {RequestUri} {SipVersion}\r\n");
        }
        else
        {
            sb.Append($"{SipVersion} {StatusCode} {ReasonPhrase}\r\n");
        }

        foreach (var (k, values) in Headers)
        {
            foreach (var v in values)
            {
                sb.Append($"{k}: {v}\r\n");
            }
        }

        sb.Append("\r\n");
        var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());

        if (bodyBytes.Length == 0) return headerBytes;

        var fullBytes = new byte[headerBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, fullBytes, 0, headerBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, fullBytes, headerBytes.Length, bodyBytes.Length);
        return fullBytes;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (IsRequest)
        {
            sb.Append($"{Method} {RequestUri} {SipVersion}\r\n");
        }
        else
        {
            sb.Append($"{SipVersion} {StatusCode} {ReasonPhrase}\r\n");
        }

        foreach (var (k, values) in Headers)
        {
            foreach (var v in values)
            {
                sb.Append($"{k}: {v}\r\n");
            }
        }

        sb.Append("\r\n");
        if (!string.IsNullOrEmpty(Body))
        {
            sb.Append(Body);
        }

        return sb.ToString();
    }
}
