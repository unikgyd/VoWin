using System.Reflection;
using System.Runtime.Versioning;
using VoSharp.HostIms;

namespace VoSharp.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsInterfacePolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeInitializedPolicyRequestChangesOnlyIdentityAndDefaultRouteBit(bool disabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        var observed = new IpInterfaceRow
        {
            Family = 23, Index = 72, Luid = 999, Metric = 9999, NlMtu = 1432,
            SitePrefixLength = 64, RouterDiscoveryBehavior = 1, LinkLocalAddressBehavior = 2,
            ManagedAddressConfigurationSupported = 1, Zone2 = 72
        };
        var expected = new IpInterfaceRow();
        WindowsInterfacePolicy.InitializeIpInterfaceEntry(ref expected);
        expected.Family = observed.Family; expected.Index = observed.Index; expected.Luid = observed.Luid;
        expected.DisableDefaultRoutes = disabled ? (byte)1 : (byte)0;
        var request = WindowsInterfacePolicy.CreateDefaultRoutePolicyUpdate(observed, disabled);
        Assert.Equal(expected, request);
        Assert.Equal(0u, request.NlMtu);
        Assert.Equal(uint.MaxValue, request.Metric);
        Assert.Equal(uint.MaxValue, request.Zone2);
        Assert.Equal(byte.MaxValue, request.ManagedAddressConfigurationSupported);
        Assert.Equal(uint.MaxValue, request.RouterDiscoveryBehavior);
        Assert.Equal(uint.MaxValue, request.LinkLocalAddressBehavior);
        Assert.Equal(1432u, observed.NlMtu); // No mutation of the observed snapshot.
    }

    [Fact]
    public void PolicyRequestRejectsAnAbsentOrWrongFamilyInterfaceIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<ArgumentException>(() => WindowsInterfacePolicy.CreateDefaultRoutePolicyUpdate(new(), true));
        Assert.Throws<ArgumentException>(() => WindowsInterfacePolicy.CreateDefaultRoutePolicyUpdate(new() { Family = 2, Index = 72, Luid = 9 }, true));
        Assert.Throws<ArgumentException>(() => WindowsInterfacePolicy.CreateDefaultRoutePolicyUpdate(new() { Family = 23, Luid = 9 }, true));
        Assert.Throws<ArgumentException>(() => WindowsInterfacePolicy.CreateDefaultRoutePolicyUpdate(new() { Family = 23, Index = 72 }, true));
    }

    [Fact]
    public void ReadbackComparisonCoversEveryWritablePolicyFieldAndIgnoresOnlyNativeReadOnlyData()
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(IpInterfaceRow.MaxReassemblySize), nameof(IpInterfaceRow.MinRouterAdvertisementInterval),
            nameof(IpInterfaceRow.MaxRouterAdvertisementInterval), nameof(IpInterfaceRow.Connected),
            nameof(IpInterfaceRow.SupportsWakeUpPatterns), nameof(IpInterfaceRow.SupportsNeighborDiscovery),
            nameof(IpInterfaceRow.SupportsRouterDiscovery), nameof(IpInterfaceRow.ReachableTime),
            nameof(IpInterfaceRow.TransmitOffload), nameof(IpInterfaceRow.ReceiveOffload),
            nameof(IpInterfaceRow.DisableDefaultRoutes)
        };
        foreach (var field in typeof(IpInterfaceRow).GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            var original = new IpInterfaceRow();
            object changed = original;
            field.SetValue(changed, Convert.ChangeType(1, field.FieldType));
            Assert.Equal(ignored.Contains(field.Name), WindowsInterfacePolicy.OtherWritablePolicyMatches(original, (IpInterfaceRow)changed));
        }
    }
}
