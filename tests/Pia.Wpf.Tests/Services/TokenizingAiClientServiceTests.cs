using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Guards the WriteOperations allow-list that gates argument detokenization before a
/// write reaches the vault. The memory tool rename (Phase 3) replaced the retired
/// create_object/update_object/append_to_list/delete_object verbs with remember/forget;
/// recall is read-only and must NOT be treated as a write.
/// </summary>
public class TokenizingAiClientServiceTests
{
    [Theory]
    [InlineData("remember")]
    [InlineData("forget")]
    [InlineData("create_reminder")]
    [InlineData("delete_todo")]
    public void IsWriteOperation_WriteVerbs_ReturnTrue(string toolName)
    {
        Assert.True(TokenizingAiClientService.IsWriteOperation(toolName));
    }

    [Theory]
    [InlineData("recall")]            // read-only search, must not detokenize
    [InlineData("create_object")]     // retired
    [InlineData("update_object")]     // retired
    [InlineData("append_to_list")]    // retired
    [InlineData("delete_object")]     // retired
    [InlineData("totally_unknown")]
    public void IsWriteOperation_ReadOnlyOrRetiredVerbs_ReturnFalse(string toolName)
    {
        Assert.False(TokenizingAiClientService.IsWriteOperation(toolName));
    }
}
