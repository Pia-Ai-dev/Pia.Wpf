using System.IO;
using Microsoft.Data.Sqlite;
using Pia.Infrastructure;
using Pia.Services;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Vault;

public class FtsQueryTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SqliteContext _ctx;

    public FtsQueryTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-fts-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        TempPath.Remove(_tmpDir);
    }

    [Fact]
    public void BuildFtsQuery_quotes_every_prefix_term()
    {
        Assert.Equal(
            "\"Marco,\" OR \"Marco,\"* OR \"neo42\" OR \"neo42\"*",
            MemoryService.BuildFtsQuery("Marco, neo42"));
    }

    // Escaping before the sub-token cut could split a doubled quote and leave a dangling one.
    [Fact]
    public void BuildFtsQuery_cuts_sub_token_prefixes_before_escaping()
    {
        var query = MemoryService.BuildFtsQuery("ab\"cdefghij", includeSubTokenPrefixes: true);

        Assert.Contains("\"ab\"\"c\"*", query, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ab\"\"*", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Marco,")]
    [InlineData("foo-bar")]
    [InlineData("say \"hi\"")]
    [InlineData("Kaffeemaschinen-Einstellung")]
    [InlineData("ab\"cdefghij")]
    [InlineData("a , b")]
    [InlineData("NOT AND OR")]
    public void BuildFtsQuery_output_is_valid_fts5(string input)
    {
        var query = MemoryService.BuildFtsQuery(input, includeSubTokenPrefixes: true);

        foreach (var table in new[] { "ChunksFts", "MemoriesFts" })
        {
            using var cmd = _ctx.GetConnection().CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {table} MATCH @q;";
            cmd.Parameters.AddWithValue("@q", query);

            var ex = Record.Exception(() => cmd.ExecuteScalar());

            Assert.False(ex is SqliteException, $"{table} rejected {query}: {ex?.Message}");
        }
    }
}
