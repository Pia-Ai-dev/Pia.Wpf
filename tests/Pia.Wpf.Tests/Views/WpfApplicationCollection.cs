using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Serializes every test that needs the process-wide WPF <see cref="System.Windows.Application"/>.
/// <c>DisableParallelization</c> keeps this collection from running concurrently with ANY other
/// collection, so nothing else is executing while the <c>Application</c> is created and while a view
/// is parsed against its thread-owned Wpf.Ui resource dictionaries.
/// <para>
/// It narrows the exposure but does not close it, and it does not decide WHEN: <c>DisableParallelization</c>
/// only puts this collection in xunit's serial group — xunit, not this file, chooses whether that group
/// runs before or after the parallel group. <c>Application.Current</c> can never be torn down, so if the
/// serial group runs FIRST, a live <c>Application</c> is observed by the entire remainder of the suite,
/// which is the same total exposure the design rejected <c>[assembly: AssemblyFixture]</c> to avoid. Triage
/// the first Windows run accordingly: a failure here is about a live <c>Application</c>, not about
/// concurrency. Being harmless under that condition is precisely what Batch 12's migration buys, and the
/// full Windows suite run is the only instrument that verifies it.
/// </para>
/// </summary>
[CollectionDefinition("WpfApplicationStatic", DisableParallelization = true)]
public sealed class WpfApplicationCollection;
