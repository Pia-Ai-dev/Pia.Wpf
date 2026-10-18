using System.Text.RegularExpressions;
using Microsoft.Mxc.Sdk.V1;

namespace Pia.Services.Plugins.Sandbox;

/// <summary>MXC's start errors name full paths and reach the release log, so the user sees these texts instead.</summary>
internal static partial class MxcSpawnErrors
{
    public static string Describe(MxcException ex) => Describe(ex.Code, ex.Message, ex.Remediation);

    public static string Describe(ErrorCode code, string message, string? remediation)
    {
        if (code == ErrorCode.BackendUnavailable)
        {
            // The missing-folder text also says "requires write-DAC permission", so it is matched first.
            if (message.Contains("(os error 2)", StringComparison.Ordinal) || message.Contains("(os error 3)", StringComparison.Ordinal))
                return "The sandbox could not start the server: a folder it needs is missing. Open the server's settings and detect runtimes again.";

            if (message.Contains("WRITE_DAC", StringComparison.Ordinal) || message.Contains("(os error 5)", StringComparison.Ordinal))
                return "The sandbox could not start the server: Pia may not change the permissions of a folder it needs, because it belongs to an administrator.";
        }

        var generic = $"The sandbox could not start the server ({code}).";
        return string.IsNullOrWhiteSpace(remediation) || PathPattern().IsMatch(remediation)
            ? generic
            : $"{generic} {remediation.Trim()}";
    }

    [GeneratedRegex(@"[A-Za-z]:[\\/]|\\\\[\w.$?-]")]
    private static partial Regex PathPattern();
}
