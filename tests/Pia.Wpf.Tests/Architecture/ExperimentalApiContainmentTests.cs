using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;
using static Pia.Tests.Architecture.ArchitectureTestBase;

namespace Pia.Tests.Architecture;

/// <summary>
/// Pins the containment premise stated at the top of
/// <c>src/Pia.Wpf/Services/AgentContextCompactor.cs</c>: every public type in
/// <c>Microsoft.Agents.AI.Compaction</c> is <c>[Experimental]</c>, MAAI001 fires at declarations as well
/// as at call sites, and the ONE suppression in the solution is sufficient only while no compaction type
/// reaches a Pia signature. Cache a strategy in a field somewhere and the 0-warning build bar starts
/// pushing toward a project-wide &lt;NoWarn&gt;, which would silently hide experimental-API adoption across
/// the entire solution. Until these two tests, review was the only thing enforcing it.
/// <para>
/// NetArchTest cannot express the first rule: its <c>HaveDependencyOn</c> reads the IL of method BODIES
/// too, so it would flag AgentContextCompactor itself — the one type whose method-local use IS the design.
/// The surface/body distinction has to be walked over the reflection members by hand.
/// </para>
/// </summary>
public class ExperimentalApiContainmentTests
{
    private const string CompactionNamespacePrefix = "Microsoft.Agents.AI.Compaction";

    /// <summary>
    /// A namespace that legitimately DOES appear in Pia signatures, used as the positive control for the
    /// reflection walk — <c>ChatMessage</c> is all over the service surface.
    /// </summary>
    private const string ControlNamespacePrefix = "Microsoft.Extensions.AI";

    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    /// <summary>
    /// Repo root, resolved exactly the way <c>LocalizationTests.SourceDirectory</c> resolves its own path:
    /// five levels up from the test binary (<c>bin/{config}/{tfm}</c> → project → <c>tests</c> → root).
    /// </summary>
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string[] SolutionSourceRoots =
    [
        Path.Combine(RepositoryRoot, "src"),
        Path.Combine(RepositoryRoot, "tests"),
    ];

    private static readonly string ExpectedSuppressionFile =
        Path.Combine("src", "Pia.Wpf", "Services", "AgentContextCompactor.cs");

    /// <summary>
    /// The build and editor-config files that could silence a diagnostic for a whole project or the whole
    /// tree. Globbed rather than hardcoded: there is one <c>Directory.Build.props</c> (under <c>src</c>) and
    /// no <c>Directory.Packages.props</c> or <c>.editorconfig</c> today, and a glob picks up whichever of
    /// them somebody adds later without this list having to guess.
    /// </summary>
    private static readonly string[] BuildFilePatterns =
    [
        "*.csproj",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        ".editorconfig",
    ];

    /// <summary>
    /// Matches a warning-disable pragma naming MAAI001, including the multi-code list form
    /// (<c>disable CS0618, MAAI001</c>). Deliberately expressed as an escaped PATTERN and never as a plain
    /// literal: this file must not contain the text it is counting, or the test fails on itself.
    /// </summary>
    private static readonly Regex Maai001Disable = new(
        @"#pragma\s+warning\s+disable[^\r\n]*\bMAAI001\b", RegexOptions.Compiled);

    /// <summary>
    /// Walks every type in the Pia assembly and reports each member whose SURFACE names a type in the
    /// experimental compaction namespace: base type, interfaces, field/property/event types, method return
    /// types, and every method and constructor parameter — recursed through generic arguments, array,
    /// by-ref and pointer element types, and nullable underlying types.
    /// <para>
    /// This test is deliberately BLIND to method-local use, which is exactly WHY it passes today:
    /// AgentContextCompactor builds its strategy inside <c>CompactAsync</c> and lets it die there. Do not
    /// "improve" it into reading method bodies — that flags the one usage the suppression exists to permit,
    /// and the obvious repair would be the project-wide &lt;NoWarn&gt; this arrangement exists to avoid.
    /// </para>
    /// <para>
    /// Not redundant with the source scan below: an <c>[Experimental("MAAI001")]</c> attribute on a Pia
    /// member silences the diagnostic with no pragma and no &lt;NoWarn&gt;, so a compaction type could reach a
    /// Pia signature with nothing for a text scan to find. This walk is the only guard on that route.
    /// </para>
    /// </summary>
    [Fact]
    public void PiaTypes_ShouldNot_ExposeCompactionTypesInTheirSurface()
    {
        // POSITIVE CONTROL, inside the same fact so it can never be skipped or drift out of sync: the
        // IDENTICAL walk pointed at a namespace that is present in Pia signatures. CompactAsync alone
        // declares IReadOnlyList<ChatMessage>, so this also exercises the generic-argument recursion. An
        // empty result here means the walk is broken and the assertion below is vacuous, not passing.
        var control = SurfaceViolations(ControlNamespacePrefix);

        Assert.True(control.Count > 0,
            $"positive control broken: no Pia surface names a {ControlNamespacePrefix} type, so the assertion "
            + "below cannot distinguish 'contained' from 'the walk returned nothing'. Fix the walk, or pick a "
            + "control namespace that is still present — do not delete this guard");

        var violations = SurfaceViolations(CompactionNamespacePrefix);

        Assert.True(violations.Count == 0,
            "no Pia type may name a Microsoft.Agents.AI.Compaction type in its surface — the single MAAI001 "
            + "suppression in AgentContextCompactor.cs is sufficient only while those types stay method-local, "
            + $"but these members break that: {string.Join(", ", violations)}");
    }

    /// <summary>
    /// Source scan, and the test that actually catches the regression the premise fears: a SECOND
    /// suppression anywhere in the solution's sources, or the same diagnostic silenced project-wide from a
    /// csproj, a <c>Directory.Build.props</c> or an <c>.editorconfig</c>. The reflection guard above is
    /// blind to both.
    /// </summary>
    [Fact]
    public void Maai001_MustBeSuppressed_ExactlyOnceAndOnlyInAgentContextCompactor()
    {
        // Anti-vacuity: if the repo root resolved wrong, every scan below would come back empty and pass.
        var compactor = Path.Combine(RepositoryRoot, ExpectedSuppressionFile);
        Assert.True(File.Exists(compactor),
            $"the repo root must resolve from the test binary, but {compactor} does not exist");

        var suppressions = new List<(string File, int Line)>();

        foreach (var file in SolutionSourceFiles())
        {
            var text = File.ReadAllText(file);

            foreach (Match match in Maai001Disable.Matches(text))
            {
                var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                suppressions.Add((Path.GetRelativePath(RepositoryRoot, file), line));
            }
        }

        Assert.True(suppressions.Count == 1,
            "the MAAI001 experimental-API warning must be suppressed exactly once in the whole solution — a "
            + "second suppression means a compaction type escaped AgentContextCompactor.cs — but these were "
            + $"found: {string.Join(", ", suppressions.Select(s => $"{s.File}:{s.Line}"))}");

        Assert.Equal(ExpectedSuppressionFile, suppressions[0].File);

        var buildFiles = BuildConfigurationFiles().ToList();

        // Anti-vacuity again: three csproj plus src/Directory.Build.props are present today.
        Assert.True(buildFiles.Count >= 3,
            $"the build-file scan must find the project files, but it found {buildFiles.Count} under {RepositoryRoot}");

        var solutionWide = buildFiles
            .Where(f => File.ReadAllText(f).Contains("MAAI001", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepositoryRoot, f))
            .ToList();

        Assert.True(solutionWide.Count == 0,
            "no project or config file may mention MAAI001 — a <NoWarn> entry or a severity override silences "
            + "the experimental-API warning solution-wide and hides every future adoption of it, but these do: "
            + string.Join(", ", solutionWide));
    }

    private static List<string> SurfaceViolations(string namespacePrefix)
    {
        var violations = new List<string>();

        foreach (var type in GetLoadableTypes(PiaAssembly))
        {
            try
            {
                // Compiler-generated types ARE method-local scope, materialised: the async state machine for
                // CompactAsync can hoist the local ContextWindowCompactionStrategy into one of its fields, and
                // a lambda display class hoists its captures the same way. Removing this filter therefore
                // makes the test flag the exact usage the suppression exists to permit — it is not a loophole.
                // The same CompilerGeneratedAttribute filter is already applied by NamingConventionTests and
                // MvvmPatternTests. Inside the try because IsDefined is itself a reflection call that can fail
                // to load an attribute type.
                if (IsCompilerGenerated(type))
                    continue;

                foreach (var (member, surface) in SurfaceTypes(type))
                {
                    // NAMESPACE STRING comparison on purpose. typeof(ContextWindowCompactionStrategy) would
                    // force a Microsoft.Agents.AI package reference into the test project, and this project
                    // staying free of that reference is itself part of the containment being asserted.
                    if (surface.Namespace is { } ns && ns.StartsWith(namespacePrefix, StringComparison.Ordinal))
                        violations.Add($"{type.FullName} -> {member} ({surface.Name})");
                }
            }
            catch (TypeLoadException)
            {
                // A type whose dependencies will not load cannot be inspected, and WPF types live in this
                // assembly. Skipping is safe here because the source scan above is an independent backstop
                // that needs no type loading at all.
            }
            catch (FileNotFoundException)
            {
            }
            catch (FileLoadException)
            {
            }
            catch (BadImageFormatException)
            {
            }
        }

        return violations;
    }

    /// <summary>
    /// Every type named by <paramref name="type"/>'s declared surface, paired with a human-readable
    /// description of where it was named. Lazy on purpose so that a reflection failure surfaces inside the
    /// caller's try block.
    /// </summary>
    private static IEnumerable<(string Member, Type SurfaceType)> SurfaceTypes(Type type)
    {
        foreach (var surface in Expand(type.BaseType))
            yield return ("base type", surface);

        foreach (var contract in type.GetInterfaces())
        {
            foreach (var surface in Expand(contract))
                yield return ("implemented interface", surface);
        }

        foreach (var field in type.GetFields(DeclaredMembers))
        {
            if (IsCompilerGenerated(field))
                continue;

            foreach (var surface in Expand(field.FieldType))
                yield return ($"field {field.Name}", surface);
        }

        foreach (var property in type.GetProperties(DeclaredMembers))
        {
            if (IsCompilerGenerated(property))
                continue;

            foreach (var surface in Expand(property.PropertyType))
                yield return ($"property {property.Name}", surface);
        }

        foreach (var declaredEvent in type.GetEvents(DeclaredMembers))
        {
            if (IsCompilerGenerated(declaredEvent))
                continue;

            foreach (var surface in Expand(declaredEvent.EventHandlerType))
                yield return ($"event {declaredEvent.Name}", surface);
        }

        foreach (var method in type.GetMethods(DeclaredMembers))
        {
            if (IsCompilerGenerated(method))
                continue;

            foreach (var surface in Expand(method.ReturnType))
                yield return ($"return type of {method.Name}", surface);

            foreach (var parameter in method.GetParameters())
            {
                foreach (var surface in Expand(parameter.ParameterType))
                    yield return ($"parameter {parameter.Name} of {method.Name}", surface);
            }
        }

        foreach (var constructor in type.GetConstructors(DeclaredMembers))
        {
            if (IsCompilerGenerated(constructor))
                continue;

            foreach (var parameter in constructor.GetParameters())
            {
                foreach (var surface in Expand(parameter.ParameterType))
                    yield return ($"constructor parameter {parameter.Name}", surface);
            }
        }
    }

    /// <summary>
    /// Every type reachable from <paramref name="root"/> by walking generic arguments, array/by-ref/pointer
    /// element types and nullable underlying types — so <c>List&lt;Strategy&gt;</c>,
    /// <c>Task&lt;Foo&lt;Strategy&gt;&gt;</c>, <c>Strategy[]</c>, <c>ref Strategy</c> and <c>Strategy?</c> are
    /// all caught, not only a bare <c>Strategy</c>.
    /// </summary>
    private static IEnumerable<Type> Expand(Type? root)
    {
        if (root is null)
            yield break;

        // Fast path: almost every surface type is neither constructed-generic nor an array.
        if (!root.HasElementType && !root.IsGenericType)
        {
            yield return root;
            yield break;
        }

        var seen = new HashSet<Type>();
        var pending = new Stack<Type>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
                continue;

            yield return current;

            // Covers arrays, by-ref types and pointers in one shot.
            if (current.HasElementType && current.GetElementType() is { } element)
                pending.Push(element);

            // Nullable<T> is a generic type, so its underlying type comes through here too.
            if (current.IsGenericType)
            {
                foreach (var argument in current.GetGenericArguments())
                    pending.Push(argument);
            }
        }
    }

    /// <summary>
    /// True when the member — or any type enclosing it — is compiler-synthesised.
    /// </summary>
    private static bool IsCompilerGenerated(MemberInfo member)
    {
        MemberInfo? current = member;

        while (current is not null)
        {
            if (current.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                return true;

            current = current.DeclaringType;
        }

        return false;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static IEnumerable<string> SolutionSourceFiles()
    {
        foreach (var root in SolutionSourceRoots)
        {
            foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // obj/ holds thousands of generated .cs files (XAML .g.cs, GlobalUsings, AssemblyInfo) and
                // bin/ holds build output; neither is a solution SOURCE, and counting them would make
                // "exactly once" depend on whether somebody had built first.
                if (!IsBuildOutput(file))
                    yield return file;
            }
        }
    }

    private static IEnumerable<string> BuildConfigurationFiles()
    {
        foreach (var pattern in BuildFilePatterns)
        {
            foreach (var file in Directory.GetFiles(RepositoryRoot, pattern, SearchOption.TopDirectoryOnly))
                yield return file;

            foreach (var root in SolutionSourceRoots)
            {
                foreach (var file in Directory.GetFiles(root, pattern, SearchOption.AllDirectories))
                {
                    if (!IsBuildOutput(file))
                        yield return file;
                }
            }
        }
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}
