using System.Text.Json;
using qmiSharp.Core;
using qmiSharp.Generated;

namespace qmiSharp.Tests;

public sealed class LibqmiConformanceTests
{
    [Fact]
    public void Qrtr16BitService_VectorFromLibqmi_RoundTripsExactly()
    {
        byte[] libqmi =
        [
            0x02, 0x21, 0x00, 0x90, 0x01, 0x03,
            0x04, 0x01, 0x00, 0x21, 0x00, 0x15, 0x00,
            0x01, 0x08, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x02, 0x07, 0x00, 0x05, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04
        ];

        QmiPacket packet = QmiPacket.Unmarshal(libqmi);
        Assert.Equal((ushort)QmiServiceType.SSC, packet.ServiceType);
        Assert.Equal(3, packet.ClientId);
        Assert.True(packet.IsIndication);
        Assert.Equal(1, packet.TransactionId);
        Assert.Equal(0x21, packet.MessageId);
        Assert.Equal(libqmi, packet.Marshal());
    }

    [Fact]
    public void QmuxResponse_VectorFromLibqmi_RoundTripsExactly()
    {
        byte[] libqmi =
        [
            0x01, 0x1E, 0x00, 0x80, 0x02, 0x05,
            0x02, 0x01, 0x00, 0x22, 0x00, 0x12, 0x00,
            0x02, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x08, 0x00, 0x45, 0x4D, 0x31, 0x32, 0x2D, 0x41, 0x57, 0x09
        ];

        QmiPacket packet = QmiPacket.Unmarshal(libqmi);
        packet.CheckResult();
        Assert.True(packet.IsResponse);
        Assert.Equal(QmiServiceType.DMS, packet.KnownServiceType);
        Assert.Equal("EM12-AW\t", packet.GetTlv(0x01)!.AsString());
        Assert.Equal(libqmi, packet.Marshal());
    }

    [Fact]
    public void CheckResult_RejectsMissingOrTruncatedResultTlv()
    {
        var missing = new QmiPacket(QmiServiceType.DMS, 1, 1, 0x22) { IsResponse = true };
        Assert.Throws<QmiException>(() => missing.CheckResult());

        missing.TLVs.Add(new QmiTlv(0x02, new byte[] { 0, 0, 0 }));
        Assert.Throws<QmiException>(() => missing.CheckResult());
    }

    [Fact]
    public void Unmarshal_RejectsTrailingAndTruncatedFrames()
    {
        var packet = new QmiPacket(QmiServiceType.DMS, 1, 7, 0x22);
        byte[] valid = packet.Marshal();
        Assert.Throws<ArgumentException>(() => QmiPacket.Unmarshal(valid.Concat(new byte[] { 0 }).ToArray()));
        Assert.Throws<ArgumentException>(() => QmiPacket.Unmarshal(valid[..^1]));
    }

    [Fact]
    public void GeneratedCatalog_MatchesEveryLibqmiMessageIdentity()
    {
        string dataDirectory = FindLibqmiDataDirectory();
        var expected = new HashSet<(ushort Service, ushort Id, bool Indication, string Name)>();
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        foreach (string file in Directory.EnumerateFiles(dataDirectory, "qmi-service-*.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file), options);
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out JsonElement typeElement) ||
                    !item.TryGetProperty("service", out JsonElement serviceElement) ||
                    !item.TryGetProperty("id", out JsonElement idElement) ||
                    !item.TryGetProperty("name", out JsonElement nameElement)) continue;

                string? type = typeElement.GetString();
                if (type is not ("Message" or "Indication")) continue;
                if (!Enum.TryParse(serviceElement.GetString(), true, out QmiServiceType service)) continue;
                ushort id = Convert.ToUInt16(idElement.GetString()![2..], 16);
                expected.Add(((ushort)service, id, type == "Indication", nameElement.GetString()!));
            }
        }

        var actual = QmiProtocolCatalog.AllMessages
            .Select(x => ((ushort)x.Service, x.MessageId, x.IsIndication, x.Name))
            .ToHashSet();
        Assert.Equal(expected.Count, actual.Count);
        Assert.Empty(expected.Except(actual));
        Assert.Empty(actual.Except(expected));
    }

    private static string FindLibqmiDataDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "qmi", "references", "libqmi", "data");
            if (Directory.Exists(candidate)) return candidate;
            candidate = Path.Combine(directory.FullName, "references", "libqmi", "data");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate qmi/references/libqmi/data");
    }
}
