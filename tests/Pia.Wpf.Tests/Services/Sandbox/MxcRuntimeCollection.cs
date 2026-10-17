using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>MXC_FFI_DIR is process-global and the SDK re-reads it when it binds a native entry point, so the tests
/// that change it must not overlap the ones that spawn a real container.</summary>
[CollectionDefinition("MxcRuntimeEnvironment", DisableParallelization = true)]
public sealed class MxcRuntimeCollection;
