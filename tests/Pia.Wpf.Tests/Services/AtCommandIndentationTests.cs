using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>An @-command rewrites the outgoing message, and pasted code must survive that rewrite.</summary>
public class AtCommandIndentationTests
{
    private const string Yaml =
        "env:\n- name: DATABASE_URL\n  valueFrom:\n    secretKeyRef:\n      name: test-db-app\n      key: uri";

    [Fact]
    public void AtCommandInMessage_KeepsPastedYamlIndentation()
    {
        var text = "@Files pruefe das Deployment\n\n```yaml\n" + Yaml + "\n```";

        Assert.NotEmpty(AtCommandParser.ExtractAllCommands(text));

        var sent = AtCommandParser.SubstituteCommands(text);

        Assert.Equal("pruefe das Deployment\n\n```yaml\n" + Yaml + "\n```", sent);
    }

    [Fact]
    public void SubstitutedTitle_KeepsPastedYamlIndentation()
    {
        var text = "@Todo:Deployment abarbeiten\n\n```yaml\n" + Yaml + "\n```";

        var sent = AtCommandParser.SubstituteCommands(text);

        Assert.Equal("Deployment abarbeiten\n\n```yaml\n" + Yaml + "\n```", sent);
    }

    [Fact]
    public void RunsOfSpacesOutsideACommand_AreLeftAlone()
    {
        var sent = AtCommandParser.SubstituteCommands("@Memory zwei  Leerzeichen bleiben");

        Assert.Equal("zwei  Leerzeichen bleiben", sent);
    }
}
