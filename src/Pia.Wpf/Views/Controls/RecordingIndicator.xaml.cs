using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Pia.Views.Controls;

public partial class RecordingIndicator : UserControl
{
    private readonly DispatcherTimer _durationTimer;
    private int _durationSeconds;
    private float _smoothedLevel;

    private const float SmoothingFactor = 0.35f;
    private const float MinScale = 0.85f;
    private const float MaxScale = 1.4f;
    private const float MinGlowRadius = 8f;
    private const float MaxGlowRadius = 40f;
    private const float MinGlowOpacity = 0.3f;
    private const float MaxGlowOpacity = 0.85f;

    public static readonly DependencyProperty AudioLevelProperty =
        DependencyProperty.Register(nameof(AudioLevel), typeof(float), typeof(RecordingIndicator),
            new PropertyMetadata(0f, OnAudioLevelChanged));

    public static readonly DependencyProperty ShowDurationProperty =
        DependencyProperty.Register(nameof(ShowDuration), typeof(bool), typeof(RecordingIndicator),
            new PropertyMetadata(false, OnShowDurationChanged));

    public static readonly DependencyProperty IsTimerRunningProperty =
        DependencyProperty.Register(nameof(IsTimerRunning), typeof(bool), typeof(RecordingIndicator),
            new PropertyMetadata(false, OnIsTimerRunningChanged));

    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(RecordingIndicator),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty EllipseSizeProperty =
        DependencyProperty.Register(nameof(EllipseSize), typeof(double), typeof(RecordingIndicator),
            new PropertyMetadata(80.0));

    public float AudioLevel
    {
        get => (float)GetValue(AudioLevelProperty);
        set => SetValue(AudioLevelProperty, value);
    }

    public bool ShowDuration
    {
        get => (bool)GetValue(ShowDurationProperty);
        set => SetValue(ShowDurationProperty, value);
    }

    public bool IsTimerRunning
    {
        get => (bool)GetValue(IsTimerRunningProperty);
        set => SetValue(IsTimerRunningProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public double EllipseSize
    {
        get => (double)GetValue(EllipseSizeProperty);
        set => SetValue(EllipseSizeProperty, value);
    }

    public RecordingIndicator()
    {
        InitializeComponent();

        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += OnDurationTimerTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Set glow color to match accent
        var accentColor = TryFindResource("SystemAccentColorSecondary");
        if (accentColor is Color color)
        {
            IndicatorGlow.Color = color;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _durationTimer.Stop();
    }

    private static void OnAudioLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RecordingIndicator indicator)
        {
            indicator.UpdateVisuals((float)e.NewValue);
        }
    }

    private static void OnShowDurationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RecordingIndicator indicator)
        {
            indicator.DurationTextBlock.Visibility = (bool)e.NewValue
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private static void OnIsTimerRunningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RecordingIndicator indicator)
        {
            if ((bool)e.NewValue)
            {
                indicator._durationSeconds = 0;
                indicator.DurationTextBlock.Text = "00:00";
                indicator._smoothedLevel = 0;
                indicator._durationTimer.Start();
            }
            else
            {
                indicator._durationTimer.Stop();
            }
        }
    }

    private void UpdateVisuals(float rawLevel)
    {
        _smoothedLevel += SmoothingFactor * (rawLevel - _smoothedLevel);

        var easedLevel = (float)Math.Pow(_smoothedLevel, 0.7);

        var scale = MinScale + easedLevel * (MaxScale - MinScale);
        var glowRadius = MinGlowRadius + easedLevel * (MaxGlowRadius - MinGlowRadius);
        var glowOpacity = MinGlowOpacity + easedLevel * (MaxGlowOpacity - MinGlowOpacity);

        IndicatorScale.ScaleX = scale;
        IndicatorScale.ScaleY = scale;
        IndicatorGlow.BlurRadius = glowRadius;
        IndicatorGlow.Opacity = glowOpacity;
    }

    private void OnDurationTimerTick(object? sender, EventArgs e)
    {
        _durationSeconds++;
        var timeSpan = TimeSpan.FromSeconds(_durationSeconds);
        DurationTextBlock.Text = timeSpan.ToString(@"mm\:ss");
    }
}
