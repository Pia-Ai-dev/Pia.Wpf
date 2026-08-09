using Xunit;

namespace Pia.Tests.Views;

/// <summary>Serializes the tests that need the process-wide WPF <see cref="System.Windows.Application"/>; it can
/// never be torn down, so the rest of the suite may still observe a live one.</summary>
[CollectionDefinition("WpfApplicationStatic", DisableParallelization = true)]
public sealed class WpfApplicationCollection;
