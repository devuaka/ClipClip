using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClipClip.ViewModels;

namespace ClipClip.Views;

public partial class HomeView : UserControl
{
    private MainViewModel? _main;

    public HomeView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _main = Window.GetWindow(this)?.DataContext as MainViewModel;
            if (_main != null) _main.ClipSaved += Bounce;
        };
        Unloaded += (_, _) =>
        {
            if (_main != null) _main.ClipSaved -= Bounce;
        };
    }

    /// <summary>Any clip, however it was triggered, gives the button a small kick.</summary>
    private void Bounce()
    {
        if (ClipBigButton.RenderTransform is not ScaleTransform { IsFrozen: false } scale)
        {
            scale = new ScaleTransform();
            ClipBigButton.RenderTransform = scale;
        }
        var kick = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(380) };
        kick.KeyFrames.Add(new EasingDoubleKeyFrame(0.93, KeyTime.FromPercent(0.25)));
        kick.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new ElasticEase { Oscillations = 1, Springiness = 4 }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, kick);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, kick);
    }
}
