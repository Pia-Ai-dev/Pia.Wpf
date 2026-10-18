using Pia.Services.Plugins.Sandbox.Runtimes;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class NodeSandboxPreloadTests
{
    [Fact]
    public void Content_LoadsFromTheEmbeddedResource()
    {
        var content = NodeSandboxPreload.Content;

        Assert.StartsWith("'use strict';", content, StringComparison.Ordinal);
        Assert.EndsWith("require('module').syncBuiltinESMExports();", content.TrimEnd(), StringComparison.Ordinal);
        Assert.Same(content, NodeSandboxPreload.Content);
    }

    [Fact]
    public void Content_SplitsTheRootsOnAPipeOnly()
    {
        var content = NodeSandboxPreload.Content;

        Assert.Contains("(process.env.PIA_SANDBOX_ROOTS || '').split('|')", content, StringComparison.Ordinal);
        Assert.DoesNotContain(".split(';')", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FileName_IsTheNameNodeOptionsRequires()
    {
        Assert.Equal("node-sandbox.cjs", NodeSandboxPreload.FileName);
    }
}
