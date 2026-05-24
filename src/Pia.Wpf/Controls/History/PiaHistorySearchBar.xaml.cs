using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.History;

public partial class PiaHistorySearchBar : UserControl
{
    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.Register(nameof(Query), typeof(string), typeof(PiaHistorySearchBar),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty StartDateProperty =
        DependencyProperty.Register(nameof(StartDate), typeof(DateTime?), typeof(PiaHistorySearchBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty EndDateProperty =
        DependencyProperty.Register(nameof(EndDate), typeof(DateTime?), typeof(PiaHistorySearchBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty TemplateIdProperty =
        DependencyProperty.Register(nameof(TemplateId), typeof(Guid?), typeof(PiaHistorySearchBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty TemplatesProperty =
        DependencyProperty.Register(nameof(Templates), typeof(IEnumerable), typeof(PiaHistorySearchBar),
            new PropertyMetadata(null));

    public string Query
    {
        get => (string)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public DateTime? StartDate
    {
        get => (DateTime?)GetValue(StartDateProperty);
        set => SetValue(StartDateProperty, value);
    }

    public DateTime? EndDate
    {
        get => (DateTime?)GetValue(EndDateProperty);
        set => SetValue(EndDateProperty, value);
    }

    public Guid? TemplateId
    {
        get => (Guid?)GetValue(TemplateIdProperty);
        set => SetValue(TemplateIdProperty, value);
    }

    public IEnumerable? Templates
    {
        get => (IEnumerable?)GetValue(TemplatesProperty);
        set => SetValue(TemplatesProperty, value);
    }

    public PiaHistorySearchBar() => InitializeComponent();
}
