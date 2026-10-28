using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

public class ImsSmsRelayDecodeTests
{
    [Fact]
    public void OptionalReportTpduCannotMasqueradeAsAnIncomingText()
    {
        // A report carrying a full, syntactically valid SMS-DELIVER TPDU is
        // still a report, not a new incoming SMS. Direction is part of parsing.
        byte[] tpdu = [0x04,0x0b,0x91,0x68,0x31,0x08,0x10,0x05,0xf0,0,0,
            0x42,0x10,0x10,0x21,0x43,0x65,0,0x05,0xe8,0x32,0x9b,0xfd,0x06];
        byte[] delivery = [1,42,0,0,(byte)tpdu.Length,..tpdu];
        Assert.NotNull(ImsSmsHandler.DecodeImsSms(delivery));
        byte[] ack = [3,42,0x41,(byte)tpdu.Length,..tpdu];
        Assert.NotNull(ImsSmsHandler.ParseRpdu(ack).Tpdu);
        Assert.Null(ImsSmsHandler.DecodeImsSms(ack));
    }
}
