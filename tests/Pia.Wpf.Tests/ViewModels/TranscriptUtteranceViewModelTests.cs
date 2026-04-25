using Pia.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

public class TranscriptUtteranceViewModelTests
{
    [Fact]
    public void You_DisplayName_AlwaysEqualsYou()
    {
        var counterpart = "Alex";
        var u = new TranscriptUtterance(TranscriptSpeaker.You, "hi", DateTimeOffset.Now);
        var vm = new TranscriptUtteranceViewModel(u, () => counterpart);

        Assert.Equal("you", vm.DisplayName);
        Assert.True(vm.IsYou);
    }

    [Fact]
    public void Them_DisplayName_TracksCounterpartAccessor()
    {
        var counterpart = "them";
        var u = new TranscriptUtterance(TranscriptSpeaker.Them, "hello", DateTimeOffset.Now);
        var vm = new TranscriptUtteranceViewModel(u, () => counterpart);

        Assert.Equal("them", vm.DisplayName);

        counterpart = "Alex";
        vm.RefreshDisplayName();
        Assert.Equal("Alex", vm.DisplayName);
    }

    [Fact]
    public void RefreshDisplayName_RaisesPropertyChanged()
    {
        var name = "them";
        var u = new TranscriptUtterance(TranscriptSpeaker.Them, "hello", DateTimeOffset.Now);
        var vm = new TranscriptUtteranceViewModel(u, () => name);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RefreshDisplayName();

        Assert.Contains(nameof(TranscriptUtteranceViewModel.DisplayName), raised);
    }
}
