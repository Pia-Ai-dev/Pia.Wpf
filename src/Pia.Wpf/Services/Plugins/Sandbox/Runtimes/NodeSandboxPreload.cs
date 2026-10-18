using System.IO;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

public static class NodeSandboxPreload
{
    public const string FileName = "node-sandbox.cjs";

    private const string ResourceName = "Pia.Sandbox.NodePreload";

    private static readonly Lazy<string> LazyContent = new(Load);

    public static string Content => LazyContent.Value;

    private static string Load()
    {
        using var stream = typeof(NodeSandboxPreload).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The Node sandbox preload resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
