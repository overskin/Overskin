using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;
using Overskin.Shared;

namespace Overskin.Linux.Platform;

/// <summary>
/// People in screenshots, for auto-hide: faces (Apple's Vision face finder on a Mac), and the
/// names at the top of chat messages with their profile pictures (the same rules as on Windows).
/// </summary>
public static partial class People
{
    /// <summary>Vision's face finder; boxes as fractions of the picture, measured up from the bottom-left.</summary>
    private const string FaceScript = """
        ObjC.import('Foundation');
        ObjC.import('Vision');
        function run(argv) {
          var url = $.NSURL.fileURLWithPath(argv[0]);
          var handler = $.VNImageRequestHandler.alloc.initWithURLOptions(url, $.NSDictionary.dictionary);
          var request = $.VNDetectFaceRectanglesRequest.alloc.init;
          if (!handler.performRequestsError($.NSArray.arrayWithObject(request), $())) return 'ERROR could not read the picture';
          var results = request.results;
          var faces = [];
          for (var i = 0; i < results.count; i++) {
            var b = results.objectAtIndex(i).boundingBox;
            faces.push([b.origin.x, b.origin.y, b.size.width, b.size.height]);
          }
          return JSON.stringify(faces);
        }
        """;

    private static string? _faceScript;

    /// <summary>Faces in the picture, in picture pixels, a little larger than the face itself. Empty where there's no face finder.</summary>
    public static async Task<List<Rect>> FacesAsync(Bitmap image)
    {
        var found = new List<Rect>();
        if (!Mac.IsMac) return found;
        var file = Path.Combine(Path.GetTempPath(), $"overskin-faces-{Environment.ProcessId}-{Guid.NewGuid():N}.png");
        try
        {
            if (_faceScript == null || !File.Exists(_faceScript))
            {
                _faceScript = Path.Combine(Path.GetTempPath(), $"overskin-faces-{Environment.ProcessId}.js");
                await File.WriteAllTextAsync(_faceScript, FaceScript);
            }
            image.Save(file);
            var json = await ScreenGrabber.OutputOf("/usr/bin/osascript", $"-l JavaScript \"{_faceScript}\" \"{file}\"");
            if (json == null || json.StartsWith("ERROR")) { Log.Write("faces: " + (json ?? "osascript failed")); return found; }
            int w = image.PixelSize.Width, h = image.PixelSize.Height;
            using var doc = JsonDocument.Parse(json);
            foreach (var b in doc.RootElement.EnumerateArray())
            {
                var r = new Rect(b[0].GetDouble() * w, (1 - b[1].GetDouble() - b[3].GetDouble()) * h, b[2].GetDouble() * w, b[3].GetDouble() * h);
                // a little more than the face itself: hair, chin and ears
                found.Add(r.Inflate(new Thickness(r.Width * 0.15, r.Height * 0.25, r.Width * 0.15, r.Height * 0.15)).Intersect(new Rect(0, 0, w, h)));
            }
        }
        catch (Exception ex) { Log.Write("faces: " + ex.Message); }
        finally { try { File.Delete(file); } catch { } }
        return found;
    }

    // a message's time beside the name: "Today at 3:45 PM", "Yesterday at 21:07", "11/24/2026 3:45 PM", "3:45 PM", "— 24/11/2026 09:12"
    [GeneratedRegex(@"^(?:[—–-]\s*)?(?:(?:today|yesterday)\s+at\s+|\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2,4},?\s*)?\d{1,2}[:.]\d{2}(?:\s*[ap]\.?m\.?)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^(?:[—–-]\s*)?(?:today|yesterday)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DayPattern();

    /// <summary>
    /// The names at the top of chat messages (a name with the message's time after it), each with
    /// the round profile picture to its left, and @mentions in the messages. As on Windows.
    /// </summary>
    public static List<PrivateDetail> ChatNames(IReadOnlyList<IReadOnlyList<OcrWord>> lines, int width, int height)
    {
        var found = new List<PrivateDetail>();
        void Add(Rect r, string kind) => found.Add(new PrivateDetail(r.X, r.Y, r.Width, r.Height, kind));
        foreach (var raw in lines)
        {
            var line = raw.OrderBy(w => w.Bounds.Left).ToList();
            if (line.Count == 0) continue;
            foreach (var w in line)
                if (w.Text.Length > 2 && w.Text[0] == '@' && !w.Text[1..].Contains('@') && !w.Text[1..].Contains('.'))
                    Add(w.Bounds.Inflate(2), "name");
            for (int t = 1; t < line.Count && t <= 5; t++)
            {
                string rest = string.Join(" ", line.Skip(t).Take(5).Select(w => w.Text));
                if (!TimePattern().IsMatch(rest) && !DayPattern().IsMatch(rest)) continue;
                var name = line.Take(t).ToList();
                if (name.Any(w => w.Text.Any(char.IsDigit) && w.Text.Any(c => c == ':'))) break;
                double gap = line[t].Bounds.Left - name[^1].Bounds.Right;
                double h = name.Max(w => w.Bounds.Height);
                if (gap > h * 6) break;
                var box = name.Select(w => w.Bounds).Aggregate((a, b) => a.Union(b));
                Add(box.Inflate(2), "name");
                // the profile picture: a square about two and a half lines high, a little to the left
                double s = Math.Max(h, 10) / 15.0;
                var face = new Rect(box.Left - 58 * s, box.Top - 6 * s, 46 * s, 46 * s).Intersect(new Rect(0, 0, width, height));
                if (face.Width > 10 * s && face.Height > 10 * s) Add(face, "profile picture");
                break;
            }
        }
        return found;
    }
}
