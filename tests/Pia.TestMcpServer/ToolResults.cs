using ModelContextProtocol.Protocol;

namespace Pia.TestMcpServer;

internal static class ToolResults
{
    public static CallToolResult Ok(string text) => new()
    {
        IsError = false,
        Content = [new TextContentBlock { Text = text }],
    };

    public static CallToolResult Error(Exception ex) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = FormatError(ex) }],
    };

    // The inner suffix keeps HttpRequestException -> SocketException distinguishable from a bare HTTP failure.
    public static string FormatError(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: 1 } agg)
        {
            ex = agg.InnerExceptions[0];
        }

        var text = $"ERROR {ex.GetType().Name}: {ex.Message}";
        var inner = ex.InnerException;
        if (inner is not null)
        {
            while (inner.InnerException is not null)
            {
                inner = inner.InnerException;
            }

            text += $" (inner {inner.GetType().Name}: {inner.Message})";
        }

        return text;
    }

    public static CallToolResult Run(Func<string> action)
    {
        try
        {
            return Ok(action());
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    public static async Task<CallToolResult> RunAsync(Func<Task<string>> action)
    {
        try
        {
            return Ok(await action().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }
}
