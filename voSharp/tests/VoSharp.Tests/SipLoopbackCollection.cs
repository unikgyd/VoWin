namespace VoSharp.Tests;

// These fixtures probe a free UDP port before handing it to SipGateway.
// Serialize them with respect to the other xUnit collections so an unrelated
// RTP allocation cannot claim the just-probed port before StartAsync binds it.
// Product exclusive binding and all port-release assertions remain unchanged.
[CollectionDefinition("SIP loopback sockets", DisableParallelization = true)]
public sealed class SipLoopbackCollection { }
