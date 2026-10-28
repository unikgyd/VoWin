namespace VoSharp.Tests;

// Every explicit hardware gate shares the same modem. Never let xUnit open
// its AT port or reset its bearer concurrently from unrelated test classes.
[CollectionDefinition("Windows modem lab", DisableParallelization = true)]
public sealed class WindowsModemLabCollection { }
