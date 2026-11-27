using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;

namespace Overskin.Linux.Platform;

/// <summary>
/// Reading text on a Mac with Apple's own Vision text reader (the one behind Live Text), through
/// the JavaScript that macOS's osascript runs. Nothing to install; works offline.
/// </summary>
public static class MacText
{
    /// <summary>
    /// Asks Vision for every line, and the box of each word in it. Boxes come back as fractions of
    /// the picture, measured up from its bottom-left corner.
    /// </summary>
    private const string Script = """
        ObjC.import('Foundation');
        ObjC.import('Vision');
        function run(argv) {
          var url = $.NSURL.fileURLWithPath(argv[0]);
          var handler = $.VNImageRequestHandler.alloc.initWithURLOptions(url, $.NSDictionary.dictionary);
          var request = $.VNRecognizeTextRequest.alloc.init;
          request.setRecognitionLevel(0);
          request.setUsesLanguageCorrection(true);
          if (!handler.performRequestsError($.NSArray.arrayWithObject(request), $())) return 'ERROR could not read the picture';
          var results = request.results;
          var lines = [];
          for (var i = 0; i < results.count; i++) {
            var best = results.objectAtIndex(i).topCandidates(1);
            if (best.count == 0) continue;
            var c = best.objectAtIndex(0);
            var text = c.string.js;
            var words = [];
            var re = /\S+/g, m;
            while ((m = re.exec(text)) !== null) {
              var box = null;
              try {
                var r = c.boundingBoxForRangeError($.NSMakeRange(m.index, m[0].length), $());
                if (r) { var b = r.boundingBox; box = [b.origin.x, b.origin.y, b.size.width, b.size.height]; }
              } catch (e) { box = null; }
              words.push({ t: m[0], s: m.index, b: box });
            }
            var lb = results.objectAtIndex(i).boundingBox;
            lines.push({ t: text, b: [lb.origin.x, lb.origin.y, lb.size.width, lb.size.height], w: words });
          }
          return JSON.stringify(lines);
        }
        """;

    private static string? _scriptFile;

    private static string ScriptFile()
    {
        if (_scriptFile != null && File.Exists(_scriptFile)) return _scriptFile;
        _scriptFile = Path.Combine(Path.GetTempPath(), $"overskin-vision-{Environment.ProcessId}.js");
        File.WriteAllText(_scriptFile, Script);
        return _scriptFile;
    }

    /// <summary>The words in the picture, grouped into lines, in picture pixels. Null if Vision couldn't run.</summary>
    public static async Task<List<List<OcrWord>>?> ReadLinesAsync(Bitmap image)
    {
        var file = Path.Combine(Path.GetTempPath(), $"overskin-ocr-{Environment.ProcessId}-{Guid.NewGuid():N}.png");
        try
        {
            image.Save(file);
            var json = await ScreenGrabber.OutputOf("/usr/bin/osascript", $"-l JavaScript \"{ScriptFile()}\" \"{file}\"");
            if (json == null || json.StartsWith("ERROR"))
            {
                Log.Write("vision: " + (json ?? "osascript failed"));
                return null;
            }
            return Parse(json, image.PixelSize.Width, image.PixelSize.Height);
        }
        catch (Exception ex)
        {
            Log.Write("vision: " + ex.Message);
            return null;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>
    /// Vision's lines as words in picture pixels (top-left origin). A word whose own box didn't come
    /// back gets its share of the line's box by how many letters come before it.
    /// </summary>
    public static List<List<OcrWord>> Parse(string json, int width, int height)
    {
        var lines = new List<List<OcrWord>>();
        using var doc = JsonDocument.Parse(json);
        Rect Box(JsonElement b) => new(b[0].GetDouble() * width, (1 - b[1].GetDouble() - b[3].GetDouble()) * height,
                                        b[2].GetDouble() * width, b[3].GetDouble() * height);
        foreach (var line in doc.RootElement.EnumerateArray())
        {
            string text = line.GetProperty("t").GetString() ?? "";
            var lineBox = Box(line.GetProperty("b"));
            var words = new List<OcrWord>();
            foreach (var w in line.GetProperty("w").EnumerateArray())
            {
                string t = w.GetProperty("t").GetString() ?? "";
                int start = w.GetProperty("s").GetInt32();
                Rect box;
                if (w.GetProperty("b").ValueKind == JsonValueKind.Array) box = Box(w.GetProperty("b"));
                else
                {
                    double per = lineBox.Width / Math.Max(1, text.Length);
                    box = new Rect(lineBox.X + start * per, lineBox.Y, t.Length * per, lineBox.Height);
                }
                // Vision sometimes gives a word the whole line's box: share it out instead
                if (box.Width >= lineBox.Width * 0.98 && text.Length > t.Length)
                {
                    double per = lineBox.Width / Math.Max(1, text.Length);
                    box = new Rect(lineBox.X + start * per, lineBox.Y, t.Length * per, lineBox.Height);
                }
                words.Add(new OcrWord(t, box));
            }
            if (words.Count > 0) lines.Add(words);
        }
        return lines;
    }
}

/// <summary>Pictures of single windows on a Mac, for live pins.</summary>
public static class MacWindowPicture
{
    /// <summary>A picture of one window by its number (without its shadow), even when it's partly covered. Null if it's gone.</summary>
    public static async Task<Avalonia.Media.Imaging.Bitmap?> WindowPictureAsync(uint id)
    {
        var path = Path.Combine(Path.GetTempPath(), $"overskin-live-{Environment.ProcessId}-{id}.png");
        var (ok, _) = await ScreenGrabber.RunAsync("/usr/sbin/screencapture", $"-x -o -t png -l {id} \"{path}\"", 10);
        if (!ok || !File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return new Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch { return null; }
        finally { try { File.Delete(path); } catch { } }
    }
}
