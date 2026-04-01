using FluentAssertions;
using NetArchTest.Rules;
using Xunit;
using static Pia.Tests.Architecture.ArchitectureTestBase;

namespace Pia.Tests.Architecture;

public class LayerDependencyTests
{
    [Fact]
    public void ViewModels_ShouldNot_DependOn_Infrastructure()
    {
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .ShouldNot().HaveDependencyOn(InfrastructureNamespace)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "ViewModels must not depend on Infrastructure, but these types do: {0}",
            FormatFailingTypes(result));
    }

    [Fact]
    public void Services_ShouldNot_DependOn_ViewModels()
    {
        // WindowManagerService and DialogService are excluded — they require ViewModel
        // references for scope creation and dialog display models respectively.
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ServicesNamespace)
            .And().DoNotHaveNameMatching("WindowManagerService")
            .And().DoNotHaveNameMatching("DialogService")
            .ShouldNot().HaveDependencyOn(ViewModelsNamespace)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Services must not depend on ViewModels, but these types do: {0}",
            FormatFailingTypes(result));
    }

    [Fact]
    public void Models_ShouldNot_DependOn_Services()
    {
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ModelsNamespace)
            .ShouldNot().HaveDependencyOn(ServicesNamespace)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Models must not depend on Services, but these types do: {0}",
            FormatFailingTypes(result));
    }

    [Fact]
    public void Models_ShouldNot_DependOn_ViewModels()
    {
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ModelsNamespace)
            .ShouldNot().HaveDependencyOn(ViewModelsNamespace)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Models must not depend on ViewModels, but these types do: {0}",
            FormatFailingTypes(result));
    }

    [Fact]
    public void Infrastructure_ShouldNot_DependOn_ViewModels()
    {
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(InfrastructureNamespace)
            .ShouldNot().HaveDependencyOn(ViewModelsNamespace)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Infrastructure must not depend on ViewModels, but these types do: {0}",
            FormatFailingTypes(result));
    }

    [Fact]
    public void Infrastructure_ShouldNot_DependOn_Services()
    {
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(InfrastructureNamespace)
            .ShouldNot().HaveDependencyOn(ServicesNamespace)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Infrastructure must not depend on Services, but these types do: {0}",
            FormatFailingTypes(result));
    }
}
