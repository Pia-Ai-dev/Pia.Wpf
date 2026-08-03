using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using NetArchTest.Rules;
using Xunit;
using static Pia.Tests.Architecture.ArchitectureTestBase;

namespace Pia.Tests.Architecture;

public class NamingConventionTests
{
    [Fact]
    public void ViewModels_MustEndWith_ViewModel()
    {
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .And().DoNotResideInNamespace(ViewModelModelsNamespace)
            .And().Inherit(typeof(ObservableObject))
            .Should().HaveNameEndingWith("ViewModel")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"ObservableObject subclasses in ViewModels must end with 'ViewModel', but these don't: {FormatFailingTypes(result)}");
    }

    [Fact]
    public void ServiceClasses_MustFollowNamingConvention()
    {
        // Agent nouns are first-class here, not exceptions. A service named for what it DOES
        // (Planner/Orchestrator/Verifier/Launcher/Executor) is as conventional as one suffixed "Service";
        // "Context" covers the runtime state carriers (RunContext). Provisioner/Session/Resampler were
        // previously carried as exempt NAMES, which made three ordinary domain nouns look like debt.
        var allowedSuffixes = new[]
        {
            "Service", "Handler", "Mapper", "Parser", "Detector", "Factory", "Client", "Engine",
            "Calculator", "Resolver", "Surface", "Buffer", "Builder", "Composer", "Store", "Runner",
            "Indexer", "Watcher", "Renderer", "Clusterer", "Extractor",
            "Planner", "Orchestrator", "Verifier", "Launcher", "Executor", "Context",
            // "Reconciler" joins the list for the same reason Provisioner/Session/Resampler did: it is an
            // ordinary domain noun for a service named after what it DOES (ScheduledFiringReconciler — book the
            // firings the job rows and the run rows disagree about), not an exemption for a misnamed class.
            // "Pool" is the same kind of noun one level down: a named concurrency primitive (RunSlotPool — hand
            // out the run slots), which is what "Buffer" and "Store" above already are.
            "Provisioner", "Session", "Resampler", "Reconciler", "Pool",
        };

        var serviceTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ServicesNamespace)
            .And().DoNotResideInNamespace(ServiceInterfacesNamespace)
            .And().DoNotResideInNamespace(ServiceExceptionsNamespace)
            .And().DoNotResideInNamespace(E2EENamespace)
            .And().AreClasses()
            .And().AreNotAbstract()
            .GetTypes();

        var violations = serviceTypes
            // The convention is about service *classes*: enums and Win32 interop structs (value types,
            // e.g. AUDIOCLIENT_ACTIVATION_PARAMS / WAVEFORMATEX, which must mirror the native API names,
            // and the ambient context record struct TaskContext) are data carriers, not services, and
            // are excluded. This only ever narrows scrutiny; a misnamed reference-type service is still caught.
            .Where(t => !t.IsValueType && !t.GetCustomAttributes<System.Runtime.CompilerServices.CompilerGeneratedAttribute>().Any())
            // NESTED types (not just nested-private) are named by their CONTAINING type — AgentPlanner.PlanStepArg
            // and BackgroundAssistantTurnRunner.ExchangeResult read correctly at every use site, and the outer
            // type already carries the naming burden this rule enforces.
            .Where(t => !t.IsNested)
            // RECORDS are data carriers by definition, so a service-naming rule has nothing to say about them.
            // Every name this test used to carry as an exemption was a record (ChromiumDownloadProgress,
            // BrowserLaunchSpec, ClusterResult, IngestStateEntry) — the exemption list was really an
            // "is-not-a-service" list maintained by hand. Where records may LIVE is a separate, structural
            // question, asserted by RecordTypes_MustNotLiveInTheServicesRootNamespace below.
            .Where(t => !IsRecord(t))
            .Where(t => !allowedSuffixes.Any(suffix => t.Name.EndsWith(suffix)))
            .Select(t => t.Name)
            .ToList();

        Assert.True(violations.Count == 0,
            $"service classes must end with one of [{string.Join(", ", allowedSuffixes)}], but these don't: {string.Join(", ", violations)}");
    }

    /// <summary>
    /// Structural companion to the naming rule: the ROOT <c>Pia.Services</c> namespace is for services, so a
    /// record declared there is misfiled. Contracts belong with the interface they serve
    /// (<c>Pia.Services.Interfaces</c> — where <c>ModelDownloadProgress</c>, <c>StepTurnSpec</c> and friends
    /// already live); feature-local DTOs belong in their feature sub-namespace (<c>Pia.Services.Wiki</c>,
    /// <c>Pia.Services.MeetingAttendee</c>, …), which is why sub-namespaces are deliberately out of scope here.
    /// <para>
    /// This is the rule that keeps the naming test honest. Every hand-maintained exemption it used to carry
    /// existed because a DTO had been dropped into a service namespace, and the only repair the old design
    /// offered was another name in a list — so the list grew and the signal decayed.
    /// </para>
    /// </summary>
    [Fact]
    public void RecordTypes_MustNotLiveInTheServicesRootNamespace()
    {
        var misfiled = PiaAssembly.GetTypes()
            .Where(t => t.Namespace == ServicesNamespace)
            .Where(t => !t.IsNested && !t.IsValueType && IsRecord(t))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.True(misfiled.Count == 0,
            "records in the Pia.Services root namespace are misfiled — move a contract to Pia.Services.Interfaces "
            + $"or a feature DTO to its feature sub-namespace: {string.Join(", ", misfiled)}");
    }

    /// <summary>
    /// Records are identified by the compiler-synthesised <c>&lt;Clone&gt;$</c> member, which is the only
    /// reliable IL-level marker (there is no <c>Type.IsRecord</c>).
    /// </summary>
    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    [Fact]
    public void ServiceInterfaces_MustStartWith_I()
    {
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ServiceInterfacesNamespace)
            .And().AreInterfaces()
            .Should().HaveNameStartingWith("I")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"service interfaces must start with 'I', but these don't: {FormatFailingTypes(result)}");
    }

    [Fact]
    public void Converters_MustEndWith_Converter()
    {
        // The naming convention targets actual converter classes. Enums and
        // compiler-generated helpers in the namespace (e.g. converter-config
        // enums nested or alongside their converter) are not converters and
        // must not be forced to end in 'Converter'.
        var converterTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ConvertersNamespace)
            .And().AreClasses()
            .GetTypes();

        var violations = converterTypes
            .Where(t => !t.IsEnum)
            .Where(t => !t.GetCustomAttributes<System.Runtime.CompilerServices.CompilerGeneratedAttribute>().Any())
            .Where(t => !t.Name.EndsWith("Converter"))
            .Select(t => t.Name)
            .ToList();

        Assert.True(violations.Count == 0,
            $"converter classes must end with 'Converter', but these don't: {string.Join(", ", violations)}");
    }
}
