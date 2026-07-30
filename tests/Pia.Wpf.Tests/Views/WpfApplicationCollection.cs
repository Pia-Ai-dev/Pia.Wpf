using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Serializes every test that needs the process-wide WPF <see cref="System.Windows.Application"/>.
/// <c>DisableParallelization</c> keeps this collection from running concurrently with ANY other
/// collection, so nothing else is executing while the <c>Application</c> is created and while a view
/// is parsed against its thread-owned Wpf.Ui resource dictionaries.
/// <para>
/// It narrows the exposure but does not close it: <c>Application.Current</c> can never be torn down, so
/// every collection scheduled after this one observes a live <c>Application</c> for the rest of the
/// process. Being harmless under that condition is precisely what Batch 12's migration buys, and the
/// full Windows suite run is the only instrument that verifies it.
/// </para>
/// </summary>
[CollectionDefinition("WpfApplicationStatic", DisableParallelization = true)]
public sealed class WpfApplicationCollection;
