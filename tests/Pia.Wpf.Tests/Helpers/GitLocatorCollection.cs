using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// Serializes every test that mutates <see cref="Pia.Helpers.GitLocator"/>'s process-global cache
/// (the locator probe tests and the settings-VM git-absent test). <c>DisableParallelization</c> keeps
/// the collection from running concurrently with any other collection, so the static seam can't race
/// under xunit v3's parallel execution. Members must still reset the static in a <c>finally</c>.
/// </summary>
[CollectionDefinition("GitLocatorStatic", DisableParallelization = true)]
public sealed class GitLocatorCollection;
