using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ClipClip.Engine.Core;

namespace ClipClip.Controls;

/// <summary>An arc from 12 o'clock, for the buffer ring around the clip button.</summary>
public sealed class ArcRing : Shape
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ArcRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    protected override Geometry DefiningGeometry
    {
        get
        {
            double fraction = Math.Clamp(Progress, 0, 1);
            double radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - StrokeThickness / 2);
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            if (fraction >= 0.9999) return new EllipseGeometry(center, radius, radius);
            if (fraction <= 0) return Geometry.Empty;

            double angle = fraction * 2 * Math.PI;
            var start = new Point(center.X, center.Y - radius);
            var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
            return new PathGeometry([figure]);
        }
    }
}

/// <summary>A labelled row for the settings page, with the control on the right.</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow));

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}

/// <summary>A small labelled value on the Home page.</summary>
public sealed class StatTile : Control
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(StatTile));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(StatTile));

    public static readonly DependencyProperty DetailProperty =
        DependencyProperty.Register(nameof(Detail), typeof(string), typeof(StatTile));

    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? Value { get => (string?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string? Detail { get => (string?)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
}

/// <summary>Click, then press a key combination. Modifier-only presses keep waiting.</summary>
public sealed class HotkeyBox : Button
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(HotkeySpec), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).Refresh()));

    private bool _listening;

    public HotkeyBox()
    {
        Click += (_, _) => Listen(true);
        LostKeyboardFocus += (_, _) => Listen(false);
        PreviewKeyDown += OnPreviewKeyDown;
        Refresh();
    }

    public HotkeySpec? Hotkey
    {
        get => (HotkeySpec?)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    private void Listen(bool listening)
    {
        _listening = listening;
        if (listening) Focus();
        Refresh();
    }

    private void Refresh() => Content = _listening ? "Press keys..." : Hotkey?.ToString() ?? "Not set";

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_listening) return;
        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            Listen(false);
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        ModifierKeys modifiers = Keyboard.Modifiers;
        Hotkey = new HotkeySpec(KeyInterop.VirtualKeyFromKey(key),
            modifiers.HasFlag(ModifierKeys.Control), modifiers.HasFlag(ModifierKeys.Alt),
            modifiers.HasFlag(ModifierKeys.Shift), modifiers.HasFlag(ModifierKeys.Windows));
        Listen(false);
    }
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasValue = value is string text ? text.Length > 0 : value != null;
        return hasValue != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class Percent : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        $"{Math.Round(System.Convert.ToDouble(value) * 100)}%";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
