using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Scheduling;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// <see cref="RecurrenceType"/> is shared with routines, and Manual belongs to those alone: a reminder that
/// never fires is a reminder that was silently lost.
/// </summary>
public class ReminderManualRecurrenceTests : IDisposable
{
    private readonly SqliteContext _ctx;
    private readonly ReminderService _service;
    private readonly string _tmpDir;

    public ReminderManualRecurrenceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _service = new ReminderService(_ctx, new RecurrenceCalculator(), NullLogger<ReminderService>.Instance);
    }

    [Fact]
    public async Task CreateAsync_RefusesManual()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _service.CreateAsync("TEST_Manual", RecurrenceType.Manual, new TimeOnly(9, 0)));
    }

    [Fact]
    public async Task UpdateAsync_RefusesManual()
    {
        var created = await _service.CreateAsync("TEST_ManualUpdate", RecurrenceType.Daily, new TimeOnly(9, 0));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _service.UpdateAsync(created.Id, recurrence: RecurrenceType.Manual));
    }

    [Fact]
    public async Task CreateReminderTool_GivenManual_FallsBackToOnce()
    {
        var reminders = Substitute.For<IReminderService>();
        reminders.CreateAsync(Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>())
            .Returns(ci => new Reminder
            {
                Description = ci.ArgAt<string>(0),
                Recurrence = ci.ArgAt<RecurrenceType>(1),
                TimeOfDay = ci.ArgAt<TimeOnly>(2),
                NextFireAt = DateTime.Now.AddDays(1),
            });
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]!);
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]!);
        var handler = new ReminderToolHandler(reminders, loc, NullLogger<ReminderToolHandler>.Instance);

        var call = new FunctionCallContent("call-1", "create_reminder", new Dictionary<string, object?>
        {
            ["description"] = "TEST_ManualTool",
            ["recurrence"] = "manual",
            ["timeOfDay"] = "09:00"
        });

        var (_, pending) = await handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        await pending!.Execute();

        await reminders.Received(1).CreateAsync(
            Arg.Any<string>(), RecurrenceType.Once, Arg.Any<TimeOnly>(),
            Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>());
    }

    public void Dispose()
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Reminders WHERE Description LIKE 'TEST_%'";
        cmd.ExecuteNonQuery();
        _ctx.Dispose();
        TempPath.Remove(_tmpDir);
    }
}
