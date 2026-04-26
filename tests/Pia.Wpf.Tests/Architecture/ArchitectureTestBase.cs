using System.Reflection;
using NetArchTest.Rules;

namespace Pia.Tests.Architecture;

public static class ArchitectureTestBase
{
    public static readonly Assembly PiaAssembly = typeof(App).Assembly;

    // Namespace constants matching RootNamespace "Pia"
    public const string ViewModelsNamespace = "Pia.ViewModels";
    public const string ViewModelModelsNamespace = "Pia.ViewModels.Models";
    public const string ServicesNamespace = "Pia.Services";
    public const string ServiceInterfacesNamespace = "Pia.Services.Interfaces";
    public const string E2EENamespace = "Pia.Services.E2EE";
    public const string InfrastructureNamespace = "Pia.Infrastructure";
    public const string ModelsNamespace = "Pia.Models";
    public const string NavigationNamespace = "Pia.Navigation";
    public const string ConvertersNamespace = "Pia.Converters";

    public static string FormatFailingTypes(TestResult result)
    {
        if (result.IsSuccessful || result.FailingTypeNames == null)
            return string.Empty;

        return string.Join(", ", result.FailingTypeNames);
    }
}
