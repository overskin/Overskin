using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Overskin;

/// <summary>
/// Live text in the editor: the words in the screenshot can be selected by dragging over them
/// (with the Select tool), copied, erased (Delete fills them in from what's around), or hidden.
/// The picture is read in the background as soon as it opens, so selecting is instant.
/// </summary>
public partial class EditorWindow
{
    private List<OcrWord> _words = new();
    private List<int> _wordLine = new();
    private BitmapSource? _wordsFor;
    private Task? _reading;
    private int _selFrom = -1, _selTo = -1;
    private bool _textDragging;
    private Canvas? _textMarks;

    private bool HasTextSelection => _selFrom >= 0 && _selTo >= 0 && _words.Count > 0;

    /// <summary>Reads the words of the current picture, once per picture.</summary>
    private Task ReadWordsAsync()
    {
        if (_wordsFor == _base && _reading != null) return _reading;
        var current = _base;
        _wordsFor = current;
        // a plain copy made here: some kinds of WPF bitmap can't be read from another thread
        var image = ImageLoader.Normalize(current);
        _reading = Task.Run(async () =>
        {
            try
            {
                var lines = await TextRecognizer.RecognizeLinesAsync(image);
                var words = new List<OcrWord>();
                var lineOf = new List<int>();
                if (lines != null)
                {
                    // reading order: top to bottom, then left to right within a line
                    var ordered = lines.Where(l => l.Count > 0).OrderBy(l => l.Min(w => w.Bounds.Top)).ToList();
                    for (int i = 0; i < ordered.Count; i++)
                        foreach (var w in ordered[i].OrderBy(w => w.Bounds.Left)) { words.Add(w); lineOf.Add(i); }
                }
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_wordsFor != current) return; // the picture changed meanwhile
                    _words = words;
                    _wordLine = lineOf;
                });
            }
            catch
            {
                // no text reader on this PC: text just can't be selected
            }
        });
        return _reading;
    }

    /// <summary>The picture changed (erase, crop, turn): the old words no longer apply.</summary>
    private void PictureChanged()
    {
        ClearTextSelection();
        _words = new();
        _wordLine = new();
        _ = ReadWordsAsync();
    }

    private int WordAt(Point p, double slack = 2)
    {
        for (int i = 0; i < _words.Count; i++)
        {
            var r = _words[i].Bounds;
            r.Inflate(slack, slack + r.Height * 0.15);
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

    /// <summary>Starts selecting text when the Select tool presses on a word.</summary>
    private bool TryBeginTextSelect(Point p)
    {
        int i = WordAt(p);
        if (i < 0) { ClearTextSelection(); return false; }
        _selFrom = _selTo = i;
        _textDragging = true;
        Doc.CaptureMouse();
        ShowTextSelection();
        return true;
    }

    private void DragTextSelect(Point p)
    {
        int i = NearestWord(p);
        if (i < 0 || i == _selTo) return;
        _selTo = i;
        ShowTextSelection();
    }

    private void EndTextSelect()
    {
        _textDragging = false;
        Doc.ReleaseMouseCapture();
        if (HasTextSelection)
            HintText.Text = "Ctrl+C copies the text · Delete erases it · right-click for more";
    }

    private (int From, int To) SelectedRange() => (Math.Min(_selFrom, _selTo), Math.Max(_selFrom, _selTo));

    private string SelectedText()
    {
        if (!HasTextSelection) return "";
        var (a, b) = SelectedRange();
        var text = new System.Text.StringBuilder();
        for (int i = a; i <= b; i++)
        {
            if (i > a) text.Append(_wordLine[i] != _wordLine[i - 1] ? "\n" : " ");
            text.Append(_words[i].Text);
        }
        return text.ToString();
    }

    private List<Rect> SelectedBoxes()
    {
        var boxes = new List<Rect>();
        if (!HasTextSelection) return boxes;
        var (a, b) = SelectedRange();
        // one box per line, so the highlight reads like a text selection
        foreach (var line in Enumerable.Range(a, b - a + 1).GroupBy(i => _wordLine[i]))
        {
            var r = Rect.Empty;
            foreach (var i in line) r.Union(_words[i].Bounds);
            r.Inflate(2, 2);
            boxes.Add(r);
        }
        return boxes;
    }

    private void ShowTextSelection()
    {
        if (_textMarks == null)
        {
            _textMarks = new Canvas { IsHitTestVisible = false };
            Doc.Children.Add(_textMarks);
        }
        _textMarks.Children.Clear();
        var accent = ((SolidColorBrush)FindResource("Accent")).Color;
        foreach (var r in SelectedBoxes())
        {
            var box = new Rectangle
            {
                Width = r.Width, Height = r.Height, RadiusX = 3, RadiusY = 3,
                Fill = new SolidColorBrush(Color.FromArgb(70, accent.R, accent.G, accent.B)),
                Stroke = new SolidColorBrush(Color.FromArgb(160, accent.R, accent.G, accent.B)),
                StrokeThickness = 1,
            };
            Canvas.SetLeft(box, r.X);
            Canvas.SetTop(box, r.Y);
            _textMarks.Children.Add(box);
        }
    }

    private void ClearTextSelection()
    {
        _selFrom = _selTo = -1;
        _textDragging = false;
        _textMarks?.Children.Clear();
    }

    private void SelectAllText()
    {
        if (_words.Count == 0) { Toast.Show("No text found in this picture."); return; }
        _selFrom = 0;
        _selTo = _words.Count - 1;
        ShowTextSelection();
        HintText.Text = "Ctrl+C copies the text · Delete erases it · right-click for more";
    }

    private void CopySelectedText()
    {
        var text = SelectedText();
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch { Toast.Show("Couldn't reach the clipboard. Try again.", true); return; }
        var (a, b) = SelectedRange();
        int n = b - a + 1;
        Toast.Show(n == 1 ? $"Copied “{text}”" : $"Copied {n} words");
    }

    /// <summary>Removes the selected words with smart erase: they're filled in from what's around them.</summary>
    private async Task EraseSelectedTextAsync()
    {
        var boxes = SelectedBoxes();
        if (boxes.Count == 0) return;
        var (a, b) = SelectedRange();
        int count = b - a + 1;
        var image = ImageLoader.Normalize(_base);
        HintText.Text = "Erasing the text…";
        IsHitTestVisible = false;
        try
        {
            var filled = await Task.Run(() =>
            {
                int w = image.PixelWidth, h = image.PixelHeight;
                var mask = new bool[w * h];
                foreach (var r0 in boxes)
                {
                    var r = r0;
                    r.Inflate(2 + r.Height * 0.12, 2 + r.Height * 0.12); // take the soft edges of the letters too
                    int x0 = Math.Max(0, (int)r.Left), x1 = Math.Min(w - 1, (int)Math.Ceiling(r.Right));
                    int y0 = Math.Max(0, (int)r.Top), y1 = Math.Min(h - 1, (int)Math.Ceiling(r.Bottom));
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++) mask[y * w + x] = true;
                }
                return Inpaint.Fill(image, mask);
            });
            PushUndo();
            _base = filled;
            foreach (var mark in _items.Where(m => m.Kind == AnnotationKind.Pixelate)) RemakeHidden(mark);
            ApplyBase();
            Render();
            Toast.Show(count == 1 ? "Erased the word. Ctrl+Z brings it back." : $"Erased {count} words. Ctrl+Z brings them back.");
        }
        finally
        {
            IsHitTestVisible = true;
            HintText.Text = "";
        }
    }

    /// <summary>Hides the selected words with the current hide style (blur, pixelate or mosaic).</summary>
    private void HideSelectedText()
    {
        var boxes = SelectedBoxes();
        if (boxes.Count == 0) return;
        PushUndo();
        foreach (var r0 in boxes)
        {
            var r = Rect.Intersect(r0, new Rect(0, 0, _base.PixelWidth, _base.PixelHeight));
            if (r.IsEmpty) continue;
            var mark = new Annotation
            {
                Kind = AnnotationKind.Pixelate, Mode = _lastHide, Size = _size, Color = Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF),
                Points = { new Point(Math.Floor(r.X), Math.Floor(r.Y)), new Point(Math.Ceiling(r.Right), Math.Ceiling(r.Bottom)) },
            };
            RemakeHidden(mark);
            if (mark.Pixels != null) _items.Add(mark);
        }
        ClearTextSelection();
        Render();
    }

    /// <summary>The right-click menu over selected text. True if it was shown.</summary>
    private bool ShowTextMenu(Point p)
    {
        if (!HasTextSelection || !SelectedBoxes().Any(r => r.Contains(p))) return false;
        var menu = new ContextMenu();
        void Item(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        Item("Copy the text", CopySelectedText);
        Item("Erase it (fill in from around it)", () => _ = EraseSelectedTextAsync());
        Item("Hide it (blur, pixelate or mosaic)", HideSelectedText);
        Item("Select all the text", SelectAllText);
        menu.IsOpen = true;
        return true;
    }

    /// <summary>The "Copy text" button: every word in the picture, marks included, onto the clipboard.</summary>
    private async void CopyAllText_Click(object sender, RoutedEventArgs e)
    {
        CommitText();
        CopyTextButton.IsEnabled = false;
        try
        {
            var text = await TextRecognizer.RecognizeAsync(Export());
            if (text == null) { Toast.Show("Windows has no text-reading language installed, so the text can't be read.", true); return; }
            if (string.IsNullOrWhiteSpace(text)) { Toast.Show("No text found in this picture."); return; }
            Clipboard.SetText(text);
            int lines = text.Split('\n').Length;
            Toast.Show($"Copied all the text ({lines} line{(lines == 1 ? "" : "s")})");
        }
        catch (Exception ex) { Toast.Show("Couldn't copy the text: " + ex.Message, true); }
        finally { CopyTextButton.IsEnabled = true; }
    }

    /// <summary>For the self-test: pretend the reader found these words, and select some of them.</summary>
    internal string SelectWordsForTest(List<OcrWord> words, int from, int to)
    {
        _words = words;
        _wordLine = words.Select(w => (int)Math.Round(w.Bounds.Top / 20)).ToList();
        _wordsFor = _base;
        _reading = Task.CompletedTask;
        _selFrom = from;
        _selTo = to;
        return SelectedText();
    }

    internal Task EraseSelectedForTest() => EraseSelectedTextAsync();
    internal BitmapSource BaseForTest => _base;
    internal void CloseForTest() { _dirty = false; Close(); }
}
