using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pia.Services.MeetingAttendee;
using Xunit;

namespace Pia.Tests.Services.MeetingAttendee;

/// <summary>
/// The composition: a real file on disk through the mail/text readers and out as a join link. The
/// ranking itself is exercised directly in <see cref="TeamsMeetingUrlTests"/>.
/// </summary>
public sealed class MeetingInviteReaderTests
{
    private const string JoinUrl =
        "https://teams.microsoft.com/l/meetup-join/19%3ameeting_ZGVjb3k%40thread.v2/0?context=x";

    [Fact]
    public async Task ReadAsync_Ics_UnfoldsAndReturnsTheJoinLink()
    {
        var calendar = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "BEGIN:VEVENT",
            "SUMMARY:Standup",
            "DESCRIPTION:Join here: https://teams.microsoft.com/l/meetup-join/19%3ameeting_ZGVjb3k%40thr",
            " ead.v2/0?context=x",
            "END:VEVENT",
            "END:VCALENDAR");

        var result = await WithTempFile(".ics", calendar);

        Assert.Equal(MeetingInviteReader.ReadStatus.Ok, result.Status);
        Assert.Equal(JoinUrl, result.Url);
    }

    [Fact]
    public async Task ReadAsync_Eml_ReturnsTheJoinLink()
    {
        var mail = string.Join("\r\n",
            "Subject: Standup",
            "Content-Type: text/plain; charset=UTF-8",
            "",
            "Microsoft Teams meeting",
            "Join: https://teams.microsoft.com/meet/368400251931177?p=1HSbqlBpMrcHsvZhWY",
            "System reference <" + JoinUrl + ">",
            "");

        var result = await WithTempFile(".eml", mail);

        Assert.Equal(MeetingInviteReader.ReadStatus.Ok, result.Status);
        Assert.Equal(JoinUrl, result.Url);
    }

    [Fact]
    public async Task ReadAsync_Msg_WithoutATeamsLink_ReportsNoUrl()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample-mail.msg");

        var result = await MeetingInviteReader.ReadAsync(path, CancellationToken.None);

        Assert.Equal(MeetingInviteReader.ReadStatus.NoUrl, result.Status);
        Assert.Null(result.Url);
    }

    [Fact]
    public async Task ReadAsync_MissingFile_ReportsUnreadable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pia-invite-{Guid.NewGuid():N}.msg");

        var result = await MeetingInviteReader.ReadAsync(path, CancellationToken.None);

        Assert.Equal(MeetingInviteReader.ReadStatus.Unreadable, result.Status);
    }

    private static async Task<MeetingInviteReader.ReadResult> WithTempFile(string extension, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pia-invite-{Guid.NewGuid():N}{extension}");
        await File.WriteAllTextAsync(path, content);
        try
        {
            return await MeetingInviteReader.ReadAsync(path, CancellationToken.None);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
