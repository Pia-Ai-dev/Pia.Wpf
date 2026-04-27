using System.Text.Json;
using Pia.Services.Consent;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: verify-audit-chain <log-path> [manifest-path]");
    Console.Error.WriteLine("If manifest-path is omitted, '<log>.manifest.json' is assumed.");
    return 2;
}

var logPath = args[0];
var manifestPath = args.Length >= 2
    ? args[1]
    : Path.ChangeExtension(logPath, ".manifest.json");

if (!File.Exists(logPath))
{
    Console.Error.WriteLine($"log not found: {logPath}");
    return 2;
}
if (!File.Exists(manifestPath))
{
    Console.Error.WriteLine($"manifest not found: {manifestPath}");
    return 2;
}

string publicKeyBase64;
try
{
    using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
    publicKeyBase64 = doc.RootElement.GetProperty("public_key").GetString()
        ?? throw new InvalidOperationException("manifest missing 'public_key'");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"failed to read manifest: {ex.Message}");
    return 2;
}

var (ok, brokenIndex) = HashChainedAuditLog.Verify(logPath, publicKeyBase64);
if (ok)
{
    Console.WriteLine("OK");
    return 0;
}
Console.WriteLine($"BROKEN at line index {brokenIndex} (0-based)");
return 1;
