using System;
using System.IO;
using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

public class AssistantFolderValidatorTests : IDisposable
{
    private readonly string _profile;
    private readonly string _temp;

    public AssistantFolderValidatorTests()
    {
        _profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _temp = Path.Combine(_profile, "pia-validator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    [Fact]
    public void Empty_folder_under_profile_is_ok()
    {
        Assert.Equal(FolderValidation.Ok, AssistantFolderValidator.Validate(_temp, currentFolder: null));
    }

    [Fact]
    public void Blank_candidate_is_invalid()
    {
        Assert.Equal(FolderValidation.Invalid, AssistantFolderValidator.Validate("  ", null));
    }

    [Fact]
    public void Folder_outside_profile_is_rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "pia-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            // Only meaningful when TEMP is not under the profile. Skip if it is.
            if (outside.StartsWith(_profile, StringComparison.OrdinalIgnoreCase)) return;
            Assert.Equal(FolderValidation.OutsideUserProfile,
                AssistantFolderValidator.Validate(outside, null));
        }
        finally { try { Directory.Delete(outside, true); } catch { } }
    }

    [Fact]
    public void Nesting_target_inside_current_folder_is_rejected()
    {
        var child = Path.Combine(_temp, "child");
        Directory.CreateDirectory(child);
        Assert.Equal(FolderValidation.NestedInCurrent,
            AssistantFolderValidator.Validate(child, currentFolder: _temp));
    }

    [Fact]
    public void Same_folder_as_current_is_ok_noop()
    {
        Assert.Equal(FolderValidation.Ok,
            AssistantFolderValidator.Validate(_temp, currentFolder: _temp));
    }

    [Fact]
    public void Existing_non_empty_target_is_rejected()
    {
        File.WriteAllText(Path.Combine(_temp, "preexisting.txt"), "x");
        Assert.Equal(FolderValidation.NotEmpty,
            AssistantFolderValidator.Validate(_temp, currentFolder: null));
    }
}
