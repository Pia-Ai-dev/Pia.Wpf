using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary>
/// Serializes every test that swings <see cref="Pia.Paths.PiaPaths"/>'s process-global data roots.
/// <c>DisableParallelization</c> keeps the collection from running alongside any other, so a test elsewhere
/// cannot resolve a Pia path while an override is in effect. Members must still restore the override — the
/// <c>OverrideForTests</c> handle does that on dispose.
/// </summary>
[CollectionDefinition("PiaPathsStatic", DisableParallelization = true)]
public sealed class PiaPathsCollection;
