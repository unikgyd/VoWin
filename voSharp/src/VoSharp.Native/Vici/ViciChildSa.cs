namespace VoSharp.Native.Vici;

/// <summary>
/// Data transfer objects for strongSwan VICI SA (Security Association) information.
/// </summary>

/// <summary>
/// Represents a loaded IKE connection configuration.
/// </summary>
public record ViciConnection(
    string Name,
    string RemoteAddr,
    string LocalId,
    string RemoteId,
    string? LocalAuth,
    string? RemoteAuth,
    IReadOnlyList<string> Proposals,
    IReadOnlyList<ViciChildSaConfig> Children
);

/// <summary>
/// Represents a CHILD_SA (IPsec SA) configuration within a connection.
/// </summary>
public record ViciChildSaConfig(
    string Name,
    string? LocalTs,   // local traffic selector, e.g. "dynamic" or "10.0.0.1/32"
    string? RemoteTs,  // remote traffic selector, e.g. "0.0.0.0/0"
    IReadOnlyList<string> EspProposals
);

/// <summary>
/// Represents an established IKE SA as reported by VICI list-sas.
/// </summary>
public record ViciIkeSa(
    string Name,
    string UniqueId,
    string State,             // e.g. "ESTABLISHED"
    string LocalHost,
    string RemoteHost,
    string? LocalId,
    string? RemoteId,
    string? InitiatorSpi,
    string? ResponderSpi,
    string? AssignedIp,       // Virtual IP from Configuration Payload (attribute 1)
    IReadOnlyList<ViciChildSa> ChildSas,
    /// <summary>
    /// P-CSCF address from the Configuration Payload (3GPP TS 24.302 §7.2, attribute 20).
    /// Requires strongSwan to be built with the <c>p-cscf</c> plugin, which is <b>not</b> enabled
    /// in the current build. Null whenever it is unavailable — callers must treat that as a hard
    /// error, not fall back to the ePDG address.
    /// </summary>
    string? PcscfIp = null
);

/// <summary>
/// Represents an established ESP Child SA as reported by VICI list-sas.
/// </summary>
public record ViciChildSa(
    string Name,
    string UniqueId,
    string State,             // e.g. "INSTALLED"
    string Protocol,          // "ESP"
    string Mode,              // "TUNNEL"
    string? SpiIn,
    string? SpiOut,
    string? EncAlg,
    string? IntegAlg,
    long BytesIn,
    long BytesOut,
    long PacketsIn,
    long PacketsOut,
    string? LocalTs,
    string? RemoteTs
);
