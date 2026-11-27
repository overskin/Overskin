using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>
/// Live text in the editor (as on Windows): with the Select tool the words in the screenshot can be
/// selected by dragging over them, copied, erased (filled in from what's around), hidden, or
/// edited like a document: click between letters for a cursor, Backspace and Delete take letters
/// out and close the gap, and typing adds letters in the line's own font. The picture is read in
/// the background as soon as it opens, so selecting is instant.
/// </summary>
public sealed partial class EditorWindow
{
    private List<OcrWord> _words = new();
    private List<int> _wordLine = new();
    private Bitmap? _wordsFor;
    private Task? _reading;
    private bool _textDragging;

    private Bitmap Base => _canvas.Picture;

    /// <summary>Reads the words of the current picture, once per picture.</summary>
    private Task ReadWordsAsync()
    {
        if (ReferenceEquals(_wordsFor, Base) && _reading != null) return _reading;
        var current = Base;
        _wordsFor = current;
        if (!Ocr.IsAvailable) return _reading = Task.CompletedTask;
        _reading = ReadAsync();
        return _reading;

        async Task ReadAsync()
        {
            try
            {
                var lines = await Ocr.ReadLinesAsync(current);
                var words = new List<OcrWord>();
                var lineOf = new List<int>();
                if (lines != null)
                {
                    // reading order: top to bottom, then left to right within a line
                    var ordered = lines.Where(l => l.Count > 0).OrderBy(l => l.Min(w => w.Bounds.Top)).ToList();
                    for (int i = 0; i < ordered.Count; i++)
                        foreach (var w in ordered[i].OrderBy(w => w.Bounds.Left)) { words.Add(w); lineOf.Add(i); }
                }
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(_wordsFor, current)) return; // the picture changed meanwhile
                    _words = words;
                    ResetEdges();
                    _wordLine = lineOf;
                });
            }
            catch (Exception ex)
            {
                Log.Write("editor text: " + ex.Message); // text just can't be selected
            }
        }
    }

    /// <summary>The picture changed (erase, crop, turn, undo): the old words no longer apply.</summary>
    private void PictureChanged()
    {
        ClearTextSelection();
        _words = new();
        ResetEdges();
        _wordLine = new();
        _ = ReadWordsAsync();
    }

    private int WordAt(Point p, double slack = 2)
    {
        for (int i = 0; i < _words.Count; i++)
        {
            var r = _words[i].Bounds.Inflate(new Thickness(slack, slack + _words[i].Bounds.Height * 0.15));
            if (r.Contains(p)) return i;
        }
        return -1;
    }

    /// <summary>The word nearest the pointer, for dragging past the ends of lines.</summary>
    private int NearestWord(Point p)
    {
        int best = -1;
        double bestScore = double.MaxValue;
        for (int i = 0; i < _words.Count; i++)
        {
            var r = _words[i].Bounds;
            double dx = p.X < r.Left ? r.Left - p.X : p.X > r.Right ? p.X - r.Right : 0;
            double dy = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
            double score = dy * 4 + dx; // staying on the same line matters most
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    // ---- letters ---------------------------------------------------------------------------
    //
    // The text reader only says where each word is. Where each letter is comes from the picture
    // itself: inside the word's box, the columns with ink in them form blobs (usually one per
    // letter, with blank columns between). The blobs are matched to the word's letters, using how
    // wide each letter usually is to tell when two letters touch (one blob, split in proportion)
    // or one letter is in pieces. The gap between two letters is the middle of the blank between
    // them. If the box has no clear ink (a busy background), the usual widths are used alone.

    private static readonly Dictionary<char, double> LetterWidths = new();
    private static readonly Typeface LetterFace = new(new FontFamily("avares://overskin/Assets/Fonts#Inter"));
    private List<double[]?> _edgeCache = new();

    private byte[]? _px;
    private int _pxW, _pxH;
    private Bitmap? _pxFor;

    private void ResetEdges()
    {
        _edgeCache = Enumerable.Repeat<double[]?>(null, _words.Count).ToList();
        _lineStyles.Clear();
        _matchedStyles.Clear();
    }

    private byte[] BasePixels()
    {
        if (_px != null && ReferenceEquals(_pxFor, Base)) return _px;
        _px = Imaging.Pixels(Base, out _);
        _pxW = Base.PixelSize.Width;
        _pxH = Base.PixelSize.Height;
        _pxFor = Base;
        return _px;
    }

    private static double LetterWidth(char c)
    {
        lock (LetterWidths)
        {
            if (LetterWidths.TryGetValue(c, out double w)) return w;
            w = Math.Max(10, Ft(c.ToString(), LetterFace, 100).WidthIncludingTrailingWhitespace);
            LetterWidths[c] = w;
            return w;
        }
    }

    /// <summary>The x of every gap between letters of word <paramref name="i"/>, from its left edge to its right.</summary>
    private double[] Edges(int i)
    {
        while (_edgeCache.Count < _words.Count) _edgeCache.Add(null);
        if (_edgeCache[i] is { } cached) return cached;
        var edges = InkEdges(i) ?? FontEdges(_words[i]);
        _edgeCache[i] = edges;
        return edges;
    }

    /// <summary>Gaps shared out by the letters' usual widths.</summary>
    private static double[] FontEdges(OcrWord word)
    {
        string text = word.Text;
        var edges = new double[text.Length + 1];
        double total = 0;
        foreach (char c in text) total += LetterWidth(c);
        double x = 0;
        edges[0] = word.Bounds.Left;
        for (int k = 0; k < text.Length; k++)
        {
            x += LetterWidth(text[k]);
            edges[k + 1] = word.Bounds.Left + word.Bounds.Width * x / Math.Max(1, total);
        }
        return edges;
    }

    /// <summary>The background colour of a stretch of the picture: its most common colour (to 4 bits a channel), its commonest exact colour, and what share it has.</summary>
    private static (double B, double G, double R, double Share) BackgroundOf(byte[] px, int w, int x0, int y0, int x1, int y1)
    {
        var counts = new Dictionary<int, int>();
        int all = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int o = (y * w + x) * 4;
                int key = (px[o] >> 4) | (px[o + 1] >> 4 << 4) | (px[o + 2] >> 4 << 8);
                counts[key] = counts.GetValueOrDefault(key) + 1;
                all++;
            }
        if (all == 0) return (255, 255, 255, 0);
        var top = counts.MaxBy(kv => kv.Value);
        // the exact colour that's commonest in that group, so a fill matches the background to the last bit
        var exact = new Dictionary<int, int>();
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int o = (y * w + x) * 4;
                if (((px[o] >> 4) | (px[o + 1] >> 4 << 4) | (px[o + 2] >> 4 << 8)) != top.Key) continue;
                int colour = px[o] | px[o + 1] << 8 | px[o + 2] << 16;
                exact[colour] = exact.GetValueOrDefault(colour) + 1;
            }
        int best = exact.MaxBy(e => e.Value).Key;
        return (best & 255, best >> 8 & 255, best >> 16 & 255, (double)top.Value / all);
    }

    /// <summary>Letter gaps found from the ink in the picture; null when the ink can't be told from the background.</summary>
    private double[]? InkEdges(int i)
    {
        var word = _words[i];
        string text = word.Text;
        int n = text.Length;
        if (n == 0) return null;
        var px = BasePixels();
        int w = _pxW, h = _pxH;
        var b = word.Bounds;
        int x0 = Math.Max(0, (int)Math.Floor(b.Left) - 1), x1 = Math.Min(w, (int)Math.Ceiling(b.Right) + 1);
        int y0 = Math.Max(0, (int)Math.Floor(b.Top)), y1 = Math.Min(h, (int)Math.Ceiling(b.Bottom));
        if (x1 - x0 < 2 || y1 - y0 < 2) return null;

        var bg = BackgroundOf(px, w, x0, y0, x1, y1);
        if (bg.Share < 0.35) return null; // no one background colour: a photo or a gradient
        double Dist(int x, int y)
        {
            int o = (y * w + x) * 4;
            double db = px[o] - bg.B, dg = px[o + 1] - bg.G, dr = px[o + 2] - bg.R;
            return Math.Sqrt(db * db + dg * dg + dr * dr);
        }
        var dists = new List<double>((x1 - x0) * (y1 - y0));
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) dists.Add(Dist(x, y));
        dists.Sort();
        double contrast = dists[(int)(dists.Count * 0.97)];
        if (contrast < 40) return null;

        // how much ink each column holds, and the runs of columns with any
        var colInk = new int[x1 - x0 + 1];
        for (int x = x0; x < x1; x++)
            for (int y = y0; y < y1; y++)
                if (Dist(x, y) >= contrast * 0.4) colInk[x - x0]++;
        var blobs = new List<(int S, int E)>();
        int start = -1;
        for (int x = x0; x <= x1; x++)
        {
            bool ink = colInk[x - x0] > 0;
            if (ink && start < 0) start = x;
            else if (!ink && start >= 0) { blobs.Add((start, x)); start = -1; }
        }
        if (blobs.Count == 0) return null;

        // cells: each blob plus half the blank on either side; the outer edges are the ink's
        int m = blobs.Count;
        var c = new double[m + 1];
        c[0] = blobs[0].S;
        c[m] = blobs[m - 1].E;
        for (int j = 1; j < m; j++) c[j] = (blobs[j - 1].E + blobs[j].S) / 2.0;

        var f = text.Select(LetterWidth).ToArray();
        double span = c[m] - c[0], scale = span / f.Sum(), avg = span / n;
        var fSum = new double[n + 1];
        for (int k = 0; k < n; k++) fSum[k + 1] = fSum[k] + f[k] * scale;

        // match letters to blobs, in order: several letters to one blob (touching), or one letter to several blobs (in pieces)
        const int Most = 12;
        var cost = new double[n + 1, m + 1];
        var from = new (int P, int Q)[n + 1, m + 1];
        for (int a = 0; a <= n; a++)
            for (int q = 0; q <= m; q++) cost[a, q] = double.PositiveInfinity;
        cost[0, 0] = 0;
        for (int a = 0; a < n; a++)
            for (int bb = 0; bb < m; bb++)
            {
                if (double.IsPositiveInfinity(cost[a, bb])) continue;
                for (int p = 1; p <= Math.Min(Most, n - a); p++)
                    for (int q = 1; q <= Math.Min(Most, m - bb); q++)
                    {
                        if (p > 1 && q > 1) continue;
                        double err = (fSum[a + p] - fSum[a] - (c[bb + q] - c[bb])) / avg;
                        double next = cost[a, bb] + err * err + 0.3 * (p - 1) + 0.8 * (q - 1);
                        if (next < cost[a + p, bb + q]) { cost[a + p, bb + q] = next; from[a + p, bb + q] = (p, q); }
                    }
            }
        if (double.IsPositiveInfinity(cost[n, m])) return null;

        var edges = new double[n + 1];
        for (int a = n, bb = m; a > 0;)
        {
            var (p, q) = from[a, bb];
            int la = a - p, lb = bb - q;
            double left = c[lb], right = c[bb], want = fSum[a] - fSum[la];
            edges[la] = left;
            edges[a] = right;
            if (p > 1)
            {
                // touching letters: the cuts go through thin ink (where letters join) while keeping
                // each letter about its usual width, all chosen together
                var wants = Enumerable.Range(la, p).Select(k => (right - left) * (fSum[k + 1] - fSum[k]) / Math.Max(1e-9, want)).ToArray();
                var xs = Enumerable.Range((int)Math.Ceiling(left), Math.Max(0, (int)Math.Floor(right) - (int)Math.Ceiling(left)))
                    .Where(x => x >= x0 && x < x1).ToArray();
                double InkCost(int j) => 3.0 * colInk[xs[j] - x0] / Math.Max(1, y1 - y0);
                double WidthCost(double from1, double to1, int t) { double e1 = (to1 - from1 - wants[t]) / avg; return 2.5 * e1 * e1; }
                int K = xs.Length;
                if (K >= p - 1)
                {
                    var D = new double[p, K];
                    var back = new int[p, K];
                    for (int j = 0; j < K; j++) D[1, j] = WidthCost(left, xs[j] + 0.5, 0) + InkCost(j);
                    for (int t = 2; t < p; t++)
                        for (int j = 0; j < K; j++)
                        {
                            D[t, j] = double.PositiveInfinity;
                            for (int ii = 0; ii < j; ii++)
                            {
                                double v = D[t - 1, ii] + WidthCost(xs[ii] + 0.5, xs[j] + 0.5, t - 1);
                                if (v < D[t, j]) { D[t, j] = v; back[t, j] = ii; }
                            }
                            D[t, j] += InkCost(j);
                        }
                    int bestJ = 0;
                    double bestV = double.PositiveInfinity;
                    for (int j = 0; j < K; j++)
                    {
                        double v = D[p - 1, j] + WidthCost(xs[j] + 0.5, right, p - 1);
                        if (v < bestV) { bestV = v; bestJ = j; }
                    }
                    for (int t = p - 1, j = bestJ; t >= 1; t--)
                    {
                        edges[la + t] = xs[j] + 0.5;
                        j = back[t, j];
                    }
                }
                else
                    for (int t = 1; t < p; t++) edges[la + t] = left + (right - left) * (fSum[la + t] - fSum[la]) / Math.Max(1e-9, want);
            }
            a = la;
            bb = lb;
        }
        return edges;
    }

    /// <summary>The gap between letters of word <paramref name="i"/> nearest <paramref name="x"/>.</summary>
    private int CaretIn(int i, double x)
    {
        var e = Edges(i);
        int best = 0;
        for (int k = 1; k < e.Length; k++)
            if (Math.Abs(e[k] - x) < Math.Abs(e[best] - x)) best = k;
        return best;
    }

    // a caret is "before letter K of word W"; the selection runs from the anchor to the focus,
    // and when they're the same it's just a blinking cursor
    private (int W, int K) _anchor = (-1, 0), _focus = (-1, 0);

    private static int Compare((int W, int K) a, (int W, int K) b) => a.W != b.W ? a.W.CompareTo(b.W) : a.K.CompareTo(b.K);

    private bool HasTextSelection =>
        _anchor.W >= 0 && _focus.W >= 0 && _anchor.W < _words.Count && _focus.W < _words.Count && Compare(_anchor, _focus) != 0;

    private bool HasTextCaret =>
        _focus.W >= 0 && _focus.W < _words.Count && _focus.K <= _words[_focus.W].Text.Length && Compare(_anchor, _focus) == 0;

    /// <summary>One step left or right through the text; from the end of a word, right goes past the space to the next.</summary>
    private (int W, int K) Step((int W, int K) c, int dir)
    {
        if (dir > 0)
            return c.K < _words[c.W].Text.Length ? (c.W, c.K + 1) : c.W + 1 < _words.Count ? (c.W + 1, 0) : c;
        return c.K > 0 ? (c.W, c.K - 1) : c.W > 0 ? (c.W - 1, _words[c.W - 1].Text.Length) : c;
    }

    /// <summary>The arrow keys: move the cursor (Shift: grow the selection).</summary>
    private void MoveCaret(int dir, bool extend)
    {
        if (!extend && HasTextSelection)
        {
            var (s, e) = Compare(_anchor, _focus) <= 0 ? (_anchor, _focus) : (_focus, _anchor);
            _anchor = _focus = dir > 0 ? e : s;
        }
        else
        {
            _focus = Step(_focus, dir);
            if (!extend) _anchor = _focus;
        }
        ShowTextSelection();
        ShowTextHint();
    }

    /// <summary>The pointer over a point with the Select tool: a hand over a link, the text cursor over a word.</summary>
    private StandardCursorType SelectCursorAt(Point p) =>
        ActionAt(p) != null ? StandardCursorType.Hand : WordAt(p) >= 0 ? StandardCursorType.Ibeam : StandardCursorType.Arrow;

    /// <summary>A press with the Select tool: on a link it opens (when let go without dragging), on a word it selects.</summary>
    private bool SelectPress(Point p, int clicks, KeyModifiers keys)
    {
        CommitText();
        // a click on a link, email address or phone number opens it (or copies the number);
        // dragging over it still selects, and a double-click picks it to edit
        if (clicks == 1 && ActionAt(p) is { } link)
        {
            if (keys.HasFlag(KeyModifiers.Control) || keys.HasFlag(KeyModifiers.Meta))
            {
                _ = RunLinkAsync(link);
                return false;
            }
            _linkPress = (link, p);
        }
        else _linkPress = null;
        if (TryBeginTextSelect(p, clicks)) return true;
        if (_words.Count == 0)
            _status.Text = !Ocr.IsAvailable ? Ocr.InstallHint : _reading is { IsCompleted: false } ? "Still reading the text in the picture…" : "No text found in this picture.";
        return false;
    }

    private async Task RunLinkAsync(TextAction link) => _status.Text = await TextActions.RunAsync(link, this);

    /// <summary>
    /// The Select tool pressed on a word: a click puts a cursor between letters, a drag selects
    /// letters, and a double-click takes the whole word.
    /// </summary>
    private bool TryBeginTextSelect(Point p, int clicks = 1)
    {
        int i = WordAt(p);
        if (i < 0) { ClearTextSelection(); return false; }
        if (clicks >= 2)
        {
            _linkTimer?.Stop(); // a double-click edits the link rather than opening it
            _anchor = (i, 0);
            _focus = (i, _words[i].Text.Length);
            _wordPicked = true;
        }
        else
        {
            _anchor = _focus = (i, CaretIn(i, p.X));
            _wordPicked = false;
        }
        _textDragging = true;
        ShowTextSelection();
        return true;
    }

    private bool _wordPicked;

    /// <summary>A press on a link: let go without dragging and it opens.</summary>
    private (TextAction Link, Point At)? _linkPress;
    private DispatcherTimer? _linkTimer;

    private void DragTextSelect(Point p)
    {
        if (_linkPress is { } press && Distance(p, press.At) > 4 / Math.Max(0.05, Zoom())) _linkPress = null;
        if (_wordPicked || !_textDragging) return;
        int i = NearestWord(p);
        if (i < 0) return;
        var caret = (i, CaretIn(i, p.X));
        if (caret == _focus) return;
        _focus = caret;
        ShowTextSelection();
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private void EndTextSelect()
    {
        _textDragging = false;
        if (_linkPress is { } press)
        {
            // opened once it's clear this isn't the first half of a double-click (which edits it)
            _linkPress = null;
            _linkTimer?.Stop();
            _linkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _linkTimer.Tick += (_, _) =>
            {
                _linkTimer?.Stop();
                ClearTextSelection();
                _ = RunLinkAsync(press.Link);
            };
            _linkTimer.Start();
        }
        ShowTextHint();
    }

    /// <summary>How many screen pixels one picture pixel takes up just now.</summary>
    private double Zoom()
    {
        var m = _canvas.TransformToVisual(this);
        return m is { } t && t.M11 > 0 ? t.M11 : 1;
    }

    private static string Mod => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";

    private void ShowTextHint()
    {
        if (HasTextSelection)
            _status.Text = $"{Mod}C copies the text · Delete removes it · type to replace it · right-click for more";
        else if (HasTextCaret)
        {
            string font = _lineStyles.TryGetValue(_wordLine[_focus.W], out var style) && style != null
                ? $"Typing in {style.Name}, {style.Size:0.#} px · " : "Type to add text · ";
            _status.Text = font + "Backspace or Delete removes letters · arrows move · Esc for tool shortcuts";
        }
    }

    /// <summary>The selected part of each word, in reading order (words with nothing selected are left out).</summary>
    private List<(int Word, int From, int To)> SelectedParts()
    {
        var parts = new List<(int, int, int)>();
        if (!HasTextSelection) return parts;
        var (s, e) = Compare(_anchor, _focus) <= 0 ? (_anchor, _focus) : (_focus, _anchor);
        for (int i = s.W; i <= e.W; i++)
        {
            int from = i == s.W ? s.K : 0, to = i == e.W ? e.K : _words[i].Text.Length;
            if (to > from) parts.Add((i, from, to));
        }
        return parts;
    }

    private string SelectedText()
    {
        var text = new System.Text.StringBuilder();
        int last = -1;
        foreach (var (i, from, to) in SelectedParts())
        {
            if (last >= 0) text.Append(_wordLine[i] != _wordLine[last] ? "\n" : " ");
            text.Append(_words[i].Text, from, to - from);
            last = i;
        }
        return text.ToString();
    }

    private List<Rect> SelectedBoxes()
    {
        var boxes = new List<Rect>();
        // one box per line, so the highlight reads like a text selection
        foreach (var line in SelectedParts().GroupBy(p => _wordLine[p.Word]))
        {
            Rect? r = null;
            foreach (var (i, from, to) in line)
            {
                var e = Edges(i);
                var b = _words[i].Bounds;
                var part = new Rect(e[from], b.Top, Math.Max(1, e[to] - e[from]), b.Height);
                r = r is { } u ? u.Union(part) : part;
            }
            // a little above and below for the letters' tails; hardly any sideways, so the next letter is left alone
            if (r is { } box) boxes.Add(box.Inflate(new Thickness(1, 2)));
        }
        return boxes;
    }

    private DispatcherTimer? _caretBlink;
    private bool _caretOn = true;

    private void ShowTextSelection()
    {
        if (HasTextCaret)
        {
            _caretOn = true;
            _caretBlink ??= new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Background, (_, _) =>
            {
                _caretOn = !_caretOn;
                _canvas.InvalidateVisual();
            });
            _caretBlink.Stop();
            _caretBlink.Start();
        }
        else _caretBlink?.Stop();
        _canvas.InvalidateVisual();
        UpdateTypeBar();
    }

    /// <summary>The selection and the text cursor, drawn over the picture.</summary>
    private void DrawTextMarks(DrawingContext ctx)
    {
        if (_tool != Tool.Select || _words.Count == 0) return;
        var accent = Colours.Accent;
        var fill = new SolidColorBrush(Color.FromArgb(70, accent.R, accent.G, accent.B));
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(160, accent.R, accent.G, accent.B)), 1);
        foreach (var r in SelectedBoxes()) ctx.DrawRectangle(fill, edge, r, 3, 3);
        if (HasTextCaret && _caretOn)
        {
            // a text cursor, a couple of screen pixels wide whatever the zoom, blinking
            var b = _words[_focus.W].Bounds;
            double x = SpaceCaret ?? Edges(_focus.W)[_focus.K], width = Math.Max(1, 2 / Math.Max(0.05, Zoom()));
            ctx.FillRectangle(new SolidColorBrush(accent), new Rect(x - width / 2, b.Top - 2, width, b.Height + 4));
        }
    }

    private void ClearTextSelection()
    {
        _anchor = _focus = (-1, 0);
        _wordPicked = false;
        _textDragging = false;
        _caretBlink?.Stop();
        _canvas.InvalidateVisual();
        UpdateTypeBar();
    }

    // ---- deleting letters ------------------------------------------------------------------
    //
    // Backspace and Delete edit the picture like text: the letters go and the rest of the line
    // moves up to close the gap, when the text sits on a plain background. On a busy one the
    // letters are smart-erased where they are instead.

    private bool _textEditBusy;

    /// <summary>Backspace (<paramref name="back"/>) or Delete: the selection, or the letter beside the cursor.</summary>
    private async Task DeleteTextAsync(bool back)
    {
        if (_textEditBusy) return;
        (int W, int K) a, b;
        if (HasTextSelection)
            (a, b) = Compare(_anchor, _focus) <= 0 ? (_anchor, _focus) : (_focus, _anchor);
        else if (back && SpaceCaret is { } spaceX)
        {
            // Backspace right after typing a space: the space goes again
            double end = Edges(_focus.W)[_focus.K];
            _textEditBusy = true;
            try { await CutLineAsync(_focus, _focus, end, spaceX, justSpace: false, join: false); }
            finally { _textEditBusy = false; }
            return;
        }
        else if (HasTextCaret)
        {
            var near = Step(_focus, back ? -1 : 1);
            (a, b) = back ? (near, _focus) : (_focus, near);
            if (a == b) return;
        }
        else return;

        if (_wordLine[a.W] != _wordLine[b.W])
        {
            // across lines: a selection is erased where it is; a cursor at the end of a line stays put
            if (HasTextSelection) await EraseSelectedTextAsync();
            return;
        }

        double xa = Edges(a.W)[a.K], xb = Edges(b.W)[b.K];
        bool justSpace = a.W != b.W && b.W == a.W + 1 && a.K == _words[a.W].Text.Length && b.K == 0;
        if (justSpace)
        {
            // joining two words: keep about a letter gap between them
            double cell = (Edges(a.W)[^1] - Edges(a.W)[0]) / Math.Max(1, _words[a.W].Text.Length);
            xb -= Math.Max(1, Math.Round(cell * 0.12));
        }
        if (xb - xa < 0.5) return;

        _textEditBusy = true;
        try { await CutLineAsync(a, b, xa, xb, justSpace); }
        finally { _textEditBusy = false; }
    }

    /// <summary>Takes the stretch from <paramref name="xa"/> to <paramref name="xb"/> out of one line of text.</summary>
    private async Task CutLineAsync((int W, int K) a, (int W, int K) b, double xa, double xb, bool justSpace, bool join = true)
    {
        int line = _wordLine[a.W];
        var (lo, hi, y0, y1, lineRight0, leftMost) = LineBand(line);
        var src = BasePixels();
        int w = _pxW, h = _pxH;
        var bg = BackgroundOf(src, w, Math.Max(0, (int)leftMost - 2), y0, Math.Clamp(lineRight0, 0, w), y1);
        int ix0 = Math.Clamp((int)Math.Round(xa), 0, w), ix1 = Math.Clamp((int)Math.Round(xb), 0, w);
        if (bg.Share >= 0.5 && ix1 - ix0 >= 2)
        {
            // the cut only ever moves outwards, never into the letters being taken out
            ix0 = Seam(src, w, y0, y1, xa, 2, (bg.B, bg.G, bg.R), -1);
            ix1 = Math.Max(ix0 + 1, Seam(src, w, y0, y1, xb, 2, (bg.B, bg.G, bg.R), +1));
        }
        int lineRight = Math.Clamp(lineRight0, ix1, w);
        int shift = ix1 - ix0;
        if (shift <= 0 || y1 <= y0) return;
        bool flat = bg.Share >= 0.5;
        Bitmap result;
        byte[] px;
        if (flat)
        {
            // the rest of the line slides left, and the end it leaves is filled with the background
            px = (byte[])src.Clone();
            byte fb = (byte)bg.B, fg = (byte)bg.G, fr = (byte)bg.R;
            bool Ink(int x, int y)
            {
                int o = (y * w + x) * 4;
                return Math.Abs(src[o] - bg.B) + Math.Abs(src[o + 1] - bg.G) + Math.Abs(src[o + 2] - bg.R) > 60;
            }
            int hook = Math.Max(2, (int)((y1 - y0) * 0.35));
            for (int y = y0; y < y1; y++)
            {
                int row = y * w * 4;
                // a stroke of a letter being taken out that runs back across the cut (the tail of a
                // j, a slanted letter's foot): cleared too, as long as it ends in background soon
                if (!justSpace && ix0 > 0 && ix0 < w && Ink(ix0, y) && Ink(ix0 - 1, y))
                {
                    int l = ix0 - 1;
                    while (l > ix0 - 1 - hook && l > 0 && Ink(l, y)) l--;
                    if (!Ink(l, y))
                        for (int x = l + 1; x < ix0; x++)
                        {
                            int o = row + x * 4;
                            px[o] = fb; px[o + 1] = fg; px[o + 2] = fr; px[o + 3] = 255;
                        }
                }
                Array.Copy(px, row + ix1 * 4, px, row + ix0 * 4, (lineRight - ix1) * 4);
                for (int x = lineRight - shift; x < lineRight; x++)
                {
                    int o = row + x * 4;
                    px[o] = fb; px[o + 1] = fg; px[o + 2] = fr; px[o + 3] = 255;
                }
            }
            result = Imaging.FromPixels(px, new PixelSize(w, h));
        }
        else
        {
            var mask = new bool[w * h];
            for (int y = y0; y < y1; y++)
                for (int x = ix0; x < ix1; x++) mask[y * w + x] = true;
            var image = Base;
            result = await Task.Run(() => Effects.Inpaint(image, mask));
            px = Imaging.Pixels(result, out _);
            shift = 0;
        }

        // the words on this line, letter by letter, without the ones cut and with the rest moved along
        var cells = new List<(char Ch, double L, double R, double T, double B)>();
        bool firstAfter = true;
        for (int i = lo; i <= hi; i++)
        {
            var e = Edges(i);
            var box = _words[i].Bounds;
            for (int k = 0; k < _words[i].Text.Length; k++)
            {
                if (Compare((i, k), a) >= 0 && Compare((i, k + 1), b) <= 0) continue; // cut
                double l = e[k], r = e[k + 1];
                if (l >= xb - 0.01 || (justSpace && i == b.W))
                {
                    l -= shift;
                    r -= shift;
                    // the first letter after the cut joins on, so the pieces either side make one word
                    if (flat && join && firstAfter) l = Math.Min(l, xa);
                    firstAfter = false;
                }
                cells.Add((_words[i].Text[k], l, r, box.Top, box.Bottom));
            }
        }
        CommitLine(lo, hi, line, cells, result, px, xa);
    }

    /// <summary>The words on a line and the band of the picture they sit in (with room for tails, never into the lines above and below).</summary>
    private (int Lo, int Hi, int Y0, int Y1, int Right, double Left) LineBand(int line)
    {
        int lo = _wordLine.IndexOf(line), hi = _wordLine.LastIndexOf(line);
        double top = double.MaxValue, bottom = double.MinValue, right = double.MinValue, leftMost = double.MaxValue;
        for (int i = lo; i <= hi; i++)
        {
            var r = _words[i].Bounds;
            top = Math.Min(top, r.Top);
            bottom = Math.Max(bottom, r.Bottom);
            right = Math.Max(right, Math.Max(r.Right, Edges(i)[^1]));
            leftMost = Math.Min(leftMost, Math.Min(r.Left, Edges(i)[0]));
        }
        double pad = (bottom - top) * 0.25, bandTop = top - pad, bandBottom = bottom + pad;
        // stop halfway to the text just above and just below, but only text that really is above or
        // below (overlapping this line across): the line before or after in reading order can sit
        // beside this one (a name and its time, another column), and halving the gap to that would
        // cut the letters in two
        double middle = (top + bottom) / 2;
        for (int k = 0; k < _words.Count; k++)
        {
            if (_wordLine[k] == line) continue;
            var b = _words[k].Bounds;
            if (b.Right <= leftMost || b.Left >= right) continue;
            double centre = (b.Top + b.Bottom) / 2;
            if (centre < middle && b.Bottom <= top + (bottom - top) * 0.2) bandTop = Math.Max(bandTop, (b.Bottom + top) / 2);
            else if (centre > middle && b.Top >= bottom - (bottom - top) * 0.2) bandBottom = Math.Min(bandBottom, (b.Top + bottom) / 2);
        }
        // and never less than the words themselves
        bandTop = Math.Min(bandTop, top - 1);
        bandBottom = Math.Max(bandBottom, bottom + 1);
        var px = BasePixels();
        int y0 = Math.Clamp((int)Math.Floor(bandTop), 0, _pxH), y1 = Math.Clamp((int)Math.Ceiling(bandBottom), 0, _pxH);
        int x0 = Math.Clamp((int)Math.Floor(leftMost) - 1, 0, _pxW), x1 = Math.Min(_pxW, (int)Math.Ceiling(right) + 2);
        (y0, y1) = GrowBand(px, _pxW, _pxH, x0, x1, y0, y1, line, (bottom - top) * 0.6);
        return (lo, hi, y0, y1, x1, leftMost);
    }

    /// <summary>
    /// The band grown to take in ink that pokes out of it and joins ink inside it: the tops of tall
    /// letters, accents and tails the reader's boxes missed. Left behind, they're the thin lines and
    /// dots that stay where a word was. A row that's a long straight line (an underline, a box's
    /// edge) or that belongs to another line's words is never taken.
    /// </summary>
    private (int Y0, int Y1) GrowBand(byte[] px, int w, int h, int x0, int x1, int y0, int y1, int line, double most)
    {
        if (x1 - x0 < 2 || y1 <= y0) return (y0, y1);
        var bg = BackgroundOf(px, w, x0, y0, x1, y1);
        if (bg.Share < 0.5) return (y0, y1);
        bool Ink(int x, int y)
        {
            int o = (y * w + x) * 4;
            double db = px[o] - bg.B, dg = px[o + 1] - bg.G, dr = px[o + 2] - bg.R;
            return db * db + dg * dg + dr * dr > 30 * 30;
        }
        bool OtherWords(int y)
        {
            for (int k = 0; k < _words.Count; k++)
            {
                if (_wordLine[k] == line) continue;
                var b = _words[k].Bounds;
                if (y >= b.Top && y < b.Bottom && b.Right > x0 && b.Left < x1) return true;
            }
            return false;
        }
        // a row joins if some of its ink touches ink in the row it would join, and it isn't a rule
        bool Joins(int y, int edge)
        {
            int count = 0, touching = 0;
            for (int x = x0; x < x1; x++)
            {
                if (!Ink(x, y)) continue;
                count++;
                for (int d = -1; d <= 1; d++)
                    if (x + d >= x0 && x + d < x1 && Ink(x + d, edge)) { touching++; break; }
            }
            return touching > 0 && count < (x1 - x0) * 0.6;
        }
        int limit = (int)Math.Ceiling(most);
        for (int n = 0; n < limit && y0 > 0 && Joins(y0 - 1, y0) && !OtherWords(y0 - 1); n++) y0--;
        for (int n = 0; n < limit && y1 < h && Joins(y1, y1 - 1) && !OtherWords(y1); n++) y1++;
        // one more row of soft edge either way, if it's not someone else's
        if (y0 > 0 && !OtherWords(y0 - 1) && Joins(y0 - 1, y0)) y0--;
        if (y1 < h && !OtherWords(y1) && Joins(y1, y1 - 1)) y1++;
        return (y0, y1);
    }

    /// <summary>
    /// Where to cut a line near <paramref name="x"/>: the column with the least ink within
    /// <paramref name="reach"/> pixels, so the cut runs through the gap between letters rather than
    /// through a letter's soft edge (which leaves a thin line behind).
    /// </summary>
    private static int Seam(byte[] px, int w, int y0, int y1, double x, int reach, (double B, double G, double R) bg, int side = 0)
    {
        int centre = (int)Math.Round(x), best = centre;
        double least = double.MaxValue;
        for (int c = centre - (side > 0 ? 0 : reach); c <= centre + (side < 0 ? 0 : reach); c++)
        {
            if (c < 0 || c >= w) continue;
            double ink = 0;
            for (int y = y0; y < y1; y++)
            {
                int o = (y * w + c) * 4;
                ink += Math.Abs(px[o] - bg.B) + Math.Abs(px[o + 1] - bg.G) + Math.Abs(px[o + 2] - bg.R);
            }
            // the nearer column wins a tie
            ink += Math.Abs(c - x) * 0.5;
            if (ink < least) { least = ink; best = c; }
        }
        return Math.Clamp(best, 0, w);
    }

    /// <summary>
    /// Puts an edited line in place: its letters (<paramref name="cells"/>, left to right) are
    /// grouped back into words wherever they touch, the picture becomes <paramref name="result"/>,
    /// and the cursor goes to the letter gap nearest <paramref name="caretX"/>. One undo step.
    /// </summary>
    private void CommitLine(int lo, int hi, int line, List<(char Ch, double L, double R, double T, double B)> cells, Bitmap result, byte[] px, double caretX)
    {
        var newWords = new List<OcrWord>();
        var newEdges = new List<double[]?>();
        for (int s = 0; s < cells.Count;)
        {
            int t = s + 1;
            while (t < cells.Count && cells[t].L - cells[t - 1].R < 0.01) t++;
            var part = cells.GetRange(s, t - s);
            double wl = part[0].L, wr = part[^1].R, wt = part.Min(c => c.T), wb = part.Max(c => c.B);
            newWords.Add(new OcrWord(new string(part.Select(c => c.Ch).ToArray()), new Rect(wl, wt, Math.Max(1, wr - wl), wb - wt)));
            newEdges.Add(part.Select(c => c.L).Append(wr).ToArray());
            s = t;
        }

        _words = _words.ToList(); // a new list, so links are looked for again
        _words.RemoveRange(lo, hi - lo + 1);
        _words.InsertRange(lo, newWords);
        _wordLine.RemoveRange(lo, hi - lo + 1);
        _wordLine.InsertRange(lo, Enumerable.Repeat(line, newWords.Count));
        while (_edgeCache.Count < hi + 1) _edgeCache.Add(null);
        _edgeCache.RemoveRange(lo, hi - lo + 1);
        _edgeCache.InsertRange(lo, newEdges);

        (int W, int K) caret = (-1, 0);
        double bestGap = double.MaxValue;
        for (int i = lo; i < lo + newWords.Count; i++)
            for (int k = 0; k < newEdges[i - lo]!.Length; k++)
            {
                double gap = Math.Abs(newEdges[i - lo]![k] - caretX);
                if (gap < bestGap - 0.01) { bestGap = gap; caret = (i, k); }
            }
        _anchor = _focus = caret;

        // the new picture keeps these words: no need to read it again
        _wordsFor = result;
        _px = px;
        _pxW = result.PixelSize.Width;
        _pxH = result.PixelSize.Height;
        _pxFor = result;
        _canvas.ReplacePicture(_ => result, flatten: false);
        ShowTextSelection();
        ShowTextHint();
    }

    private void SelectAllText()
    {
        if (_words.Count == 0) { _status.Text = "No text found in this picture."; return; }
        _anchor = (0, 0);
        _focus = (_words.Count - 1, _words[^1].Text.Length);
        ShowTextSelection();
        _status.Text = $"{Mod}C copies the text · Delete erases it · right-click for more";
    }

    private async Task CopySelectedTextAsync()
    {
        var text = SelectedText();
        if (text.Length == 0) return;
        await Desktop.CopyTextAsync(text, this);
        _status.Text = text.Length <= 40 ? $"Copied “{text.Replace("\n", " ")}”" : $"Copied {text.Length} characters";
    }

    /// <summary>Removes the selected words with smart erase: they're filled in from what's around them.</summary>
    private async Task EraseSelectedTextAsync()
    {
        var boxes = SelectedBoxes();
        if (boxes.Count == 0) return;
        var image = Base;
        _status.Text = "Erasing the text…";
        IsHitTestVisible = false;
        try
        {
            var filled = await Task.Run(() =>
            {
                int w = image.PixelSize.Width, h = image.PixelSize.Height;
                var mask = new bool[w * h];
                foreach (var r0 in boxes)
                {
                    // the soft edges of the letters above and below; only a pixel sideways, or a letter next to the selection goes too
                    var r = r0.Inflate(new Thickness(1.5, 2 + r0.Height * 0.12));
                    int x0 = Math.Max(0, (int)r.Left), x1 = Math.Min(w - 1, (int)Math.Ceiling(r.Right));
                    int y0 = Math.Max(0, (int)r.Top), y1 = Math.Min(h - 1, (int)Math.Ceiling(r.Bottom));
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++) mask[y * w + x] = true;
                }
                return FillTextHole(image, mask);
            });
            ClearTextSelection();
            _canvas.ReplacePicture(_ => filled, flatten: false);
            _status.Text = $"Erased the selected text. {Mod}Z brings it back.";
        }
        finally
        {
            IsHitTestVisible = true;
        }
    }

    /// <summary>
    /// Fills erased text. Text usually sits on a flat background: when most of the ring of pixels
    /// around the hole is one colour, the hole is filled with exactly that colour, so letters left
    /// next to it don't smudge in. Anything else (a gradient, a photo) goes to smart erase.
    /// </summary>
    internal static Bitmap FillTextHole(Bitmap image, bool[] mask)
    {
        int w = image.PixelSize.Width, h = image.PixelSize.Height;
        var px = Imaging.Pixels(image, out _);

        // the ring: unmasked pixels within 3 of the hole
        var counts = new Dictionary<int, (int N, long B, long G, long R)>();
        int ring = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (mask[i]) continue;
                bool near = false;
                for (int dy = -3; dy <= 3 && !near; dy++)
                    for (int dx = -3; dx <= 3 && !near; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && yy >= 0 && xx < w && yy < h && mask[yy * w + xx]) near = true;
                    }
                if (!near) continue;
                ring++;
                int o = i * 4;
                int key = (px[o] >> 4) | (px[o + 1] >> 4 << 4) | (px[o + 2] >> 4 << 8);
                counts.TryGetValue(key, out var c);
                counts[key] = (c.N + 1, c.B + px[o], c.G + px[o + 1], c.R + px[o + 2]);
            }
        if (ring == 0) return Effects.Inpaint(image, mask);
        var top = counts.Values.MaxBy(c => c.N);
        if (top.N < ring * 0.6) return Effects.Inpaint(image, mask);

        byte b = (byte)(top.B / top.N), g = (byte)(top.G / top.N), r = (byte)(top.R / top.N);
        for (int i = 0; i < w * h; i++)
        {
            if (!mask[i]) continue;
            px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = 255;
        }
        return Imaging.FromPixels(px, new PixelSize(w, h));
    }

    /// <summary>Hides the selected words with the current hide style (blur, pixelate or mosaic).</summary>
    private void HideSelectedText()
    {
        var boxes = SelectedBoxes();
        if (boxes.Count == 0) return;
        var whole = new Rect(0, 0, Base.PixelSize.Width, Base.PixelSize.Height);
        var marks = boxes.Select(r0 => r0.Intersect(whole)).Where(r => r.Width >= 1 && r.Height >= 1)
            .Select(r => new PatchMark
            {
                Area = new Rect(new Point(Math.Floor(r.X), Math.Floor(r.Y)), new Point(Math.Ceiling(r.Right), Math.Ceiling(r.Bottom))),
                Mode = _canvas.HideMode, Strength = Math.Max(4, _canvas.HideStrength),
            }).ToList();
        ClearTextSelection();
        _canvas.AddRange(marks);
    }

    // ---- links, email addresses and phone numbers -------------------------------------------

    private List<TextAction>? _actions;
    private List<OcrWord>? _actionsFor;

    /// <summary>The link, email address or phone number at <paramref name="p"/>, if any.</summary>
    private TextAction? ActionAt(Point p)
    {
        if (!ReferenceEquals(_actionsFor, _words))
        {
            _actions = TextActions.Find(_words, _wordLine);
            _actionsFor = _words;
        }
        return _actions!.FirstOrDefault(a => a.Bounds.Inflate(new Thickness(2, 3)).Contains(p));
    }

    /// <summary>A right-click: a link's menu, the selected text's menu, or nothing (false).</summary>
    private bool RightPress(Point p) => ShowLinkMenu(p) || ShowTextMenu(p) || ShowMarkMenu(p) || ShowPictureMenu(p);

    private ContextMenu Menu(params (string Header, Action Click)[] items)
    {
        var menu = new ContextMenu();
        foreach (var (header, click) in items)
        {
            if (header == "-") { menu.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => click();
            menu.Items.Add(mi);
        }
        return menu;
    }

    /// <summary>Right-clicking a link, email address or phone number offers to open, write to or copy it.</summary>
    private bool ShowLinkMenu(Point p)
    {
        if (ActionAt(p) is not { } action) return false;
        var items = new List<(string, Action)>
        {
            (action.Kind switch { TextActionKind.Link => "Open the link", TextActionKind.Email => "Write an email", _ => "Copy the number" }, () => _ = RunLinkAsync(action)),
        };
        if (action.Kind != TextActionKind.Phone)
            items.Add((action.Kind == TextActionKind.Link ? "Copy the link" : "Copy the address", () => _ = Desktop.CopyTextAsync(action.Value, this)));
        items.Add(("-", () => { }));
        items.Add(("Select all the text", SelectAllText));
        Menu(items.ToArray()).Open(_canvas);
        return true;
    }

    /// <summary>The right-click menu over selected text. True if it was shown.</summary>
    private bool ShowTextMenu(Point p)
    {
        if (!HasTextSelection || !SelectedBoxes().Any(r => r.Contains(p))) return false;
        Menu(("Copy the text", () => _ = CopySelectedTextAsync()),
             ("Move it (lift it off and drag it)", LiftSelectedText),
             ("Erase it (fill in from around it)", () => _ = EraseSelectedTextAsync()),
             ("Hide it (blur, pixelate or mosaic)", HideSelectedText),
             ("Select all the text", SelectAllText)).Open(_canvas);
        return true;
    }

    /// <summary>
    /// The keys while the text cursor or a selection is in the picture. True if the key was used;
    /// letters aren't (they arrive as typing).
    /// </summary>
    private bool TextKey(KeyEventArgs e, bool ctrl)
    {
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && e.Key == Key.C && HasTextSelection) { _ = CopySelectedTextAsync(); return true; }
        if (ctrl && e.Key == Key.A && _tool == Tool.Select) { SelectAllText(); return true; }
        if (!(HasTextCaret || HasTextSelection)) return false;
        switch (e.Key)
        {
            case Key.Back or Key.Delete: _ = DeleteTextAsync(e.Key == Key.Back); return true;
            case Key.Left or Key.Right when !ctrl: MoveCaret(e.Key == Key.Right ? 1 : -1, shift); return true;
            case Key.Escape: ClearTextSelection(); _status.Text = ""; return true;
        }
        return false;
    }

    // ---- for the self-test ------------------------------------------------------------------

    /// <summary>Pretend the reader found these words, and select some of them.</summary>
    internal string SelectWordsForTest(List<OcrWord> words, int from, int to, int[]? lines = null)
    {
        _words = words.ToList();
        ResetEdges();
        _wordLine = lines?.ToList() ?? words.Select(w => (int)Math.Round(w.Bounds.Top / 20)).ToList();
        _wordsFor = Base;
        _reading = Task.CompletedTask;
        Pick(Tool.Select);
        _anchor = (from, 0);
        _focus = (to, words[to].Text.Length);
        return SelectedText();
    }

    /// <summary>A press at <paramref name="from"/>, a drag to <paramref name="to"/> and a release, as the mouse would.</summary>
    internal string DragOverTextForTest(Point from, Point to)
    {
        if (!TryBeginTextSelect(from)) return "(nothing under the press)";
        DragTextSelect(to);
        EndTextSelect();
        return SelectedText();
    }

    internal string DoubleClickForTest(Point p)
    {
        if (!TryBeginTextSelect(p, 2)) return "(nothing there)";
        EndTextSelect();
        return SelectedText();
    }

    internal Task ClickAndDeleteForTest(Point p, bool back)
    {
        if (!TryBeginTextSelect(p)) return Task.CompletedTask;
        EndTextSelect();
        return DeleteTextAsync(back);
    }

    internal string WordsForTest => string.Join("|", _words.Select(w => w.Text));
    internal string CaretForTest => HasTextCaret ? $"{_words[_focus.W].Text}@{_focus.K}" : "none";
    internal Rect FirstBoxForTest => SelectedBoxes().FirstOrDefault();
    internal double[] EdgesForTest(int i) => Edges(i);
    internal Task EraseSelectedForTest() => EraseSelectedTextAsync();
    internal Task DeleteSelectionForTest() => DeleteTextAsync(back: false);
    internal Bitmap BaseForTest => Base;
    internal TextAction? LinkAtForTest(Point p) => ActionAt(p);
    internal string HideSelectedForTest() { HideSelectedText(); return string.Join(",", _canvas.Marks.OfType<PatchMark>().Select(m => m.Pixels != null)); }
}
