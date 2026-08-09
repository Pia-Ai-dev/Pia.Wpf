using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;
using static Pia.Tests.Architecture.ArchitectureTestBase;

namespace Pia.Tests.Architecture;

/// <summary>The solution's single experimental-API suppression holds only while no compaction type reaches a
/// Pia signature; a project-wide &lt;NoWarn&gt; would hide every future adoption of the API.</summary>
public class ExperimentalApiContainmentTests
{
    private const string CompactionNamespacePrefix = "Microsoft.Agents.AI.Compaction";

    /// <summary>Positive control: a namespace that legitimately does appear in Pia signatures.</summary>
    private const string ControlNamespacePrefix = "Microsoft.Extensions.AI";

    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    /// <summary>Five levels up from the test binary: <c>bin/{config}/{tfm}</c> → project → <c>tests</c> → root.</summary>
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string[] SolutionSourceRoots =
    [
        Path.Combine(RepositoryRoot, "src"),
        Path.Combine(RepositoryRoot, "tests"),
    ];

    private static readonly string ExpectedSuppressionFile =
        Path.Combine("src", "Pia.Wpf", "Services", "AgentContextCompactor.cs");

    /// <summary>Globbed rather than hardcoded, so a file somebody adds later is still scanned.</summary>
    private static readonly string[] BuildFilePatterns =
    [
        "*.csproj",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        ".editorconfig",
    ];

    /// <summary>Kept as an escaped pattern, never a plain literal: this file must not contain the text it counts.</summary>
    private static readonly Regex Maai001Disable = new(
        @"#pragma\s+warning\s+disable[^\r\n]*\bMAAI001\b", RegexOptions.Compiled);

    /// <summary>Blind to method-local use on purpose: reading method bodies would flag the one usage the
    /// suppression exists to permit. Not redundant with the source scan — an attribute needs no pragma.</summary>
    [Fact]
    public void PiaTypes_ShouldNot_ExposeCompactionTypesInTheirSurface()
    {
        // The identical walk over a namespace Pia really does name, kept inside this fact so it cannot be
        // skipped: an empty result means the walk is broken, not that Pia is contained.
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

    /// <summary>Catches a second suppression, or the diagnostic silenced project-wide from a build file — both
    /// invisible to the reflection guard above.</summary>
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

        // Anti-vacuity: the project files really were found.
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
                // Compiler-generated types are method-local scope materialised — an async state machine hoists
                // the local into a field — so dropping this filter would flag the permitted usage, not a leak.
                if (IsCompilerGenerated(type))
                    continue;

                foreach (var (member, surface) in SurfaceTypes(type))
                {
                    // Compared as a namespace string because a typeof would force the package reference this
                    // project's freedom from is itself part of the containment.
                    if (surface.Namespace is { } ns && ns.StartsWith(namespacePrefix, StringComparison.Ordinal))
                        violations.Add($"{type.FullName} -> {member} ({surface.Name})");
                }
            }
            catch (TypeLoadException)
            {
                // A type whose dependencies will not load cannot be inspected; the source scan is an
                // independent backstop that needs no type loading at all.
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

    /// <summary>Lazy on purpose, so a reflection failure surfaces inside the caller's try block.</summary>
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

    /// <summary>Recurses generic arguments and element types, so a nested or wrapped type is caught too.</summary>
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

    /// <summary>True when the member, or any type enclosing it, is compiler-synthesised.</summary>
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
