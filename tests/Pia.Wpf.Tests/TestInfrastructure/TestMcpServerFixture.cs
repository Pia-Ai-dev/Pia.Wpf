using System.IO;

namespace Pia.Tests.TestInfrastructure;

internal static class TestMcpServerFixture
{
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "Pia.TestMcpServer", "Pia.TestMcpServer.exe");
}
