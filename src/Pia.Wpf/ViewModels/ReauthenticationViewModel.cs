using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Shared.Auth;

namespace Pia.ViewModels;

public sealed partial class ReauthenticationViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _code = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private bool _isCodeRequired;

    [ObservableProperty]
    private string? _errorMessage;

    public bool CanConfirm => Password.Length > 0 && (!IsCodeRequired || Code.Trim().Length > 0);

    // Six digits is an authenticator code; anything else is taken as a recovery code.
    public ReauthenticationFields ToFields()
    {
        if (!IsCodeRequired)
            return new ReauthenticationFields(Password);

        var code = Code.Trim().Replace(" ", "");
        return code.Length == 6 && code.All(char.IsAsciiDigit)
            ? new ReauthenticationFields(Password, Code: code)
            : new ReauthenticationFields(Password, RecoveryCode: code);
    }
}
