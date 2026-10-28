using VoSharp.Kernel.SipGateway;

namespace VoWin.Tests;

public sealed partial class MtSmsForwardWireGuardTests
{
    // New interface delegation is deliberately separate from the three frozen
    // RED behavior files. Source validation is the actual SQLite implementation.
    private sealed partial class WireStore
    {
        public Task<bool> IsClaimSourceCurrentAsync(SipSmsForwardClaim claim, CancellationToken ct) =>
            ((ISipSmsForwardStore)_database).IsClaimSourceCurrentAsync(claim, ct);
    }
}
