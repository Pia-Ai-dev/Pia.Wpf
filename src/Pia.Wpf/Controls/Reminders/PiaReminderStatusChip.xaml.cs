using System.Windows;
using System.Windows.Controls;
using Pia.Models;

namespace Pia.Controls.Reminders;

public partial class PiaReminderStatusChip : UserControl
{
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(
            nameof(Status),
            typeof(ReminderStatus),
            typeof(PiaReminderStatusChip),
            new PropertyMetadata(ReminderStatus.Active));

    public ReminderStatus Status
    {
        get => (ReminderStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public PiaReminderStatusChip()
    {
        InitializeComponent();
    }
}
