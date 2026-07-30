using System.Threading.Tasks;
using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

public class VaultWriteGateTests
{
    [Fact]
    public async Task Exclusive_blocks_until_writer_releases()
    {
        var gate = new VaultWriteGate();
        var writer = await gate.EnterWriteAsync(TestContext.Current.CancellationToken);
        var exclusive = gate.EnterExclusiveAsync(TestContext.Current.CancellationToken);
        Assert.False(exclusive.IsCompleted);   // blocked while writer holds it
        writer.Dispose();
        var handle = await exclusive;           // now proceeds
        handle.Dispose();
    }

    [Fact]
    public async Task Writer_blocks_while_exclusive_held()
    {
        var gate = new VaultWriteGate();
        var exclusive = await gate.EnterExclusiveAsync(TestContext.Current.CancellationToken);
        var writer = gate.EnterWriteAsync(TestContext.Current.CancellationToken);
        Assert.False(writer.IsCompleted);
        exclusive.Dispose();
        (await writer).Dispose();
    }
}
