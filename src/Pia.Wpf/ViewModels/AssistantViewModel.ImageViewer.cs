using CommunityToolkit.Mvvm.Input;
using Pia.Models;

namespace Pia.ViewModels;

public partial class AssistantViewModel
{
    // The overlay refuses overlapping sessions; staying enabled lets focus return to the thumbnail.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task OpenPendingImageAsync(ImageAttachment image)
    {
        var images = PendingAttachments.ToList();
        var index = images.IndexOf(image);
        return index < 0 ? Task.CompletedTask : _dialogService.ShowImageViewerAsync(images, index);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task OpenSentImageAsync(ImageAttachment image)
    {
        var images = Messages.FirstOrDefault(m => m.Attachments.Contains(image))?.Attachments.ToList();
        return images is null ? Task.CompletedTask : _dialogService.ShowImageViewerAsync(images, images.IndexOf(image));
    }
}
