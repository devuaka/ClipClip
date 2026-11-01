using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipClip.Engine.Library;

namespace ClipClip.ViewModels;

public sealed class ClipItem : ObservableObject
{
    private ImageSource? _thumbnail;
    private bool _thumbnailLoaded;

    public ClipItem(ClipInfo info) => Info = info;

    public ClipInfo Info { get; }
    public string Title => Info.Title;
    public string Game => Info.Game ?? "Desktop";
    public string When => Info.Created.ToString("MMM d, yyyy  h:mm tt");
    public string Duration => FormatTime(Info.DurationSeconds);
    public string Size => Info.SizeBytes >= 1024 * 1024 * 1024
        ? $"{Info.SizeBytes / (1024.0 * 1024 * 1024):F1} GB"
        : $"{Info.SizeBytes / (1024.0 * 1024):F0} MB";
    public string Resolution => $"{Info.Width} x {Info.Height}";

    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnailLoaded = true;
                _thumbnail = Load(Info.ThumbnailPath);
            }
            return _thumbnail;
        }
    }

    public void TitleChanged() => Notify(nameof(Title));

    public static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private static ImageSource? Load(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 400;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
