using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Pia.Models;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class HotkeyCaptureContentDialog : ContentDialog, INotifyPropertyChanged
{
    public KeyboardShortcut? CapturedHotkey { get; private set; }

    public HotkeyCaptureContentDialog(ContentDialogHost? dialogHost)
        : base(dialogHost)
    {
        InitializeComponent();
        DataContext = this;

        Loaded += OnLoaded;
    }

    public string CapturedHotkeyDisplayText
    {
        get => _capturedHotkeyDisplayText;
        set
        {
            if (_capturedHotkeyDisplayText != value)
            {
                _capturedHotkeyDisplayText = value;
                OnPropertyChanged();
            }
        }
    }
    private string _capturedHotkeyDisplayText = "Press a key combination";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Keyboard.Focus(this);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
            e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
            e.Key == Key.LeftShift || e.Key == Key.RightShift ||
            e.Key == Key.LWin || e.Key == Key.RWin)
            return;

        var modifiers = KeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            modifiers |= KeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            modifiers |= KeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            modifiers |= KeyModifiers.Shift;

        var virtualKeyCode = KeyInterop.VirtualKeyFromKey(e.Key);

        var shortcut = new KeyboardShortcut(modifiers, e.Key, (uint)virtualKeyCode);
        CapturedHotkey = shortcut;
        CapturedHotkeyDisplayText = shortcut.DisplayText;

        e.Handled = true;
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result == ContentDialogResult.Secondary)
        {
            CapturedHotkey = null;
        }

        PreviewKeyDown -= OnPreviewKeyDown;
    }
}
