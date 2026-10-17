using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pia.TestMcpServer;

// The exe directory is always readable inside a sandbox; the working directory may not be.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// stdout belongs to the MCP stdio transport.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "Pia.TestMcpServer", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithTools<FileTools>();

await builder.Build().RunAsync();
