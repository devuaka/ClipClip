using System.Text.Json;
using System.Text.Json.Serialization;
using ClipClip.Engine.Core;

namespace ClipClip.Engine.Editing;

public enum ExportAspect { Original, Vertical, Square }
public enum TransitionKind { None, Fade, Flash, Zoom }
public enum MotionEffect { None, SlowZoom, Shake }
public enum FilterPreset { None, Vivid, Warm, Cool, Mono, Faded }
public enum LayerAnimation { None, Fade, Pop, SlideUp }
public enum LayerKind { Text, Sticker }

/// <summary>A piece of the main video track. Pieces play back to back.</summary>
public sealed class VideoClip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourcePath { get; set; } = "";
    public string Title { get; set; } = "";
    public double SourceDuration { get; set; }
    public double In { get; set; }
    public double Out { get; set; }
    public double Speed { get; set; } = 1;
    public bool KeepPitch { get; set; } = true;
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public FilterPreset Filter { get; set; }
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1;
    public double Saturation { get; set; } = 1;
    public MotionEffect Motion { get; set; }
    public TransitionKind Transition { get; set; }
    public double TransitionSeconds { get; set; } = 0.5;

    [JsonIgnore] public double Duration => Math.Max(0.05, (Out - In) / Speed);
}

/// <summary>Text or an image placed over the video for a stretch of time.</summary>
public sealed class OverlayLayer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public LayerKind Kind { get; set; }
    public string Text { get; set; } = "Your text";
    public string? ImagePath { get; set; }
    public double Start { get; set; }
    public double End { get; set; } = 3;
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
    /// <summary>Text: height as a share of the frame. Sticker: width as a share of the frame.</summary>
    public double Size { get; set; } = 0.08;
    public string Color { get; set; } = "#FFFFFF";
    public string Font { get; set; } = "Bahnschrift";
    public bool Bold { get; set; } = true;
    public bool Outline { get; set; } = true;
    public LayerAnimation Animation { get; set; } = LayerAnimation.Pop;

    [JsonIgnore] public double Duration => End - Start;
}

/// <summary>Music, a sound or a voiceover, placed anywhere on the timeline.</summary>
public sealed class AudioClip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourcePath { get; set; } = "";
    public string Title { get; set; } = "";
    public double SourceDuration { get; set; }
    public double Start { get; set; }
    public double In { get; set; }
    public double Out { get; set; }
    public double Volume { get; set; } = 1;
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    public bool IsVoiceover { get; set; }

    [JsonIgnore] public double Duration => Out - In;
    [JsonIgnore] public double End => Start + Duration;
}

public sealed class EditorProject
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled project";
    public DateTime Modified { get; set; } = DateTime.Now;
    public ExportAspect Aspect { get; set; }
    public int Fps { get; set; } = 60;
    public List<VideoClip> Video { get; set; } = [];
    public List<OverlayLayer> Layers { get; set; } = [];
    public List<AudioClip> Audio { get; set; } = [];

    /// <summary>The video track sets the length. Text or audio past its end is cut off.</summary>
    [JsonIgnore] public double Duration => Video.Sum(v => v.Duration);

    public IEnumerable<(VideoClip Clip, double Start)> Sequence()
    {
        double start = 0;
        foreach (VideoClip clip in Video)
        {
            yield return (clip, start);
            start += clip.Duration;
        }
    }

    public (VideoClip? Clip, double Start) ClipAt(double time)
    {
        foreach ((VideoClip clip, double start) in Sequence())
        {
            if (time < start + clip.Duration) return (clip, start);
        }
        return (null, 0);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static EditorProject FromJson(string json) => JsonSerializer.Deserialize<EditorProject>(json, Json) ?? new EditorProject();
    public EditorProject Clone() => FromJson(ToJson());
}

public sealed record ProjectEntry(string Id, string Name, DateTime Modified);

public static class ProjectStore
{
    public static string Directory => Path.Combine(AppPaths.DataDir, "projects");

    public static string MediaDirectory(string projectId)
    {
        string path = Path.Combine(Directory, projectId);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    public static void Save(EditorProject project)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            project.Modified = DateTime.Now;
            string path = Path.Combine(Directory, project.Id + ".json");
            File.WriteAllText(path + ".tmp", project.ToJson());
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex)
        {
            Log.Warn("Project not saved: " + ex.Message);
        }
    }

    public static EditorProject? Load(string id)
    {
        try
        {
            string path = Path.Combine(Directory, id + ".json");
            return File.Exists(path) ? EditorProject.FromJson(File.ReadAllText(path)) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Project unreadable: " + ex.Message);
            return null;
        }
    }

    public static IReadOnlyList<ProjectEntry> List()
    {
        var result = new List<ProjectEntry>();
        if (!System.IO.Directory.Exists(Directory)) return result;
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            EditorProject? project = Load(Path.GetFileNameWithoutExtension(file));
            if (project != null) result.Add(new ProjectEntry(project.Id, project.Name, project.Modified));
        }
        return result.OrderByDescending(p => p.Modified).ToList();
    }

    public static void Delete(string id)
    {
        try
        {
            File.Delete(Path.Combine(Directory, id + ".json"));
            string media = Path.Combine(Directory, id);
            if (System.IO.Directory.Exists(media)) System.IO.Directory.Delete(media, true);
        }
        catch (IOException ex)
        {
            Log.Warn("Project not deleted: " + ex.Message);
        }
    }
}
