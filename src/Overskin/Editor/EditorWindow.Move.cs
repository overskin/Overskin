using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Overskin;

/// <summary>
/// Moving things inside the screenshot itself: a word, an icon or a picture is lifted off as a
/// piece you can drag anywhere (and resize), and the gap it leaves is filled in from what was
/// around it. Pictures inside the screenshot can also be swapped for another picture, keeping
/// their place, size and rounded corners.
/// </summary>
public partial class EditorWindow
{
    private Rectangle? _moveBox;
    private Point _moveStart;
    private bool _moveDragging;

    // ---- pictures inside the screenshot ----

    private BitmapSource? _picturesFor;
    private List<FoundImage> _pictures = new();

    /// <summary>The pictures inside the screenshot, found once per version of it.</summary>
    private List<FoundImage> Pictures()
    {
        if (_picturesFor == _base) return _pictures;
        var px = BasePixels();
        try { _pictures = ImageFinder.Find(px, _pxW, _pxH); }
        catch { _pictures = new(); }
        _picturesFor = _base;
        return _pictures;
    }

    private FoundImage? PictureUnder(Point p) => ImageFinder.At(Pictures(), (int)p.X, (int)p.Y);

    // ---- the Move tool ----

    /// <summary>Move tool, mouse down: a picture or a word is lifted at once; anywhere else starts a box.</summary>
    private void MoveToolDown(Point p)
    {
        // a piece already lifted: pull a corner to scale it, or drag it to move it
        int handle = _selected is { Kind: AnnotationKind.Piece } ? HandleAt(p) : -1;
        var piece = handle >= 0 ? _selected : _items.LastOrDefault(i => i.Kind == AnnotationKind.Piece && i.Bounds().Contains(p));
        if (piece != null)
        {
            _selected = piece;
            ShowSelection();
            _handle = handle;
            _down = true;
            _moved = false;
            _last = p;
            Doc.CaptureMouse();
            return;
        }
        if (_selected != null)
        {
            _selected = null;
            ShowSelection();
        }
        if (PictureUnder(p) is { } picture && WordAt(p) < 0)
        {
            _ = LiftAsync(picture.Bounds, keyOut: false, picture.Radius, dragFrom: p);
            return;
        }
        int word = WordAt(p);
        if (word >= 0)
        {
            var r = _words[word].Bounds;
            r.Inflate(1.5, 2 + r.Height * 0.12);
            _ = LiftAsync(ToPixels(r), keyOut: true, 0, dragFrom: p);
            return;
        }
        _moveStart = p;
        _moveDragging = true;
        _moveBox = new Rectangle
        {
            Stroke = (Brush)FindResource("Accent"), StrokeThickness = 1.5 / Zoom,
            StrokeDashArray = new DoubleCollection { 5, 3 }, Fill = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
        };
        Adorn.Children.Add(_moveBox);
        UpdateBox(_moveBox, p, p);
        Doc.CaptureMouse();
    }

    private void MoveToolMove(Point p)
    {
        if (_moveDragging && _moveBox != null) UpdateBox(_moveBox, Clamp(_moveStart), Clamp(p));
        else if (!_moveDragging)
        {
            bool onPiece = _items.Any(i => i.Kind == AnnotationKind.Piece && i.Bounds().Contains(p));
            bool onHandle = _selected is { Kind: AnnotationKind.Piece } && HandleAt(p) >= 0;
            Doc.Cursor = onHandle ? Cursors.SizeNWSE : onPiece || PictureUnder(p) != null || WordAt(p) >= 0 ? Cursors.SizeAll : Cursors.Cross;
        }
    }

    private void MoveToolUp(Point p)
    {
        if (!_moveDragging) return;
        _moveDragging = false;
        Doc.ReleaseMouseCapture();
        if (_moveBox != null) Adorn.Children.Remove(_moveBox);
        _moveBox = null;
        var r = new Rect(Clamp(_moveStart), Clamp(p));
        if (r.Width < 4 || r.Height < 4) return;
        _ = LiftAsync(ToPixels(r), keyOut: true, 0, dragFrom: null, tighten: true);
    }

    private Int32Rect ToPixels(Rect r)
    {
        int x0 = Math.Max(0, (int)Math.Floor(r.X)), y0 = Math.Max(0, (int)Math.Floor(r.Y));
        int x1 = Math.Min(_base.PixelWidth, (int)Math.Ceiling(r.Right)), y1 = Math.Min(_base.PixelHeight, (int)Math.Ceiling(r.Bottom));
        return new Int32Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>Lifts the selected text off as a piece to drag.</summary>
    private void LiftSelectedText()
    {
        var boxes = SelectedBoxes();
        if (boxes.Count == 0) return;
        var all = boxes.Aggregate(Rect.Union);
        all.Inflate(1.5, 2 + boxes.Max(b => b.Height) * 0.12);
        ClearTextSelection();
        _ = LiftAsync(ToPixels(all), keyOut: true, 0, dragFrom: null);
    }

    /// <summary>
    /// Takes part of the picture off as a piece. On a plain background (text, an icon) the
    /// background is taken out, soft edges and all, so the piece sits cleanly wherever it goes;
    /// a picture comes off whole, with its round corners. The gap is filled in from around it.
    /// </summary>
    private async Task LiftAsync(Int32Rect r, bool keyOut, int radius, Point? dragFrom, bool tighten = false)
    {
        if (r.Width < 2 || r.Height < 2) return;
        CommitText();
        var image = ImageLoader.Normalize(_base);
        HintText.Text = "Lifting it off…";
        IsHitTestVisible = false;
        try
        {
            var result = await Task.Run(() => Lift(image, r, keyOut, radius, tighten));
            if (result is not { } lifted) { Toast.Show("Nothing to move there."); return; }
            PushUndo();
            _base = lifted.Filled;
            foreach (var mark in _items.Where(m => m.Kind == AnnotationKind.Pixelate)) RemakeHidden(mark);
            ApplyBase();
            var piece = new Annotation
            {
                Kind = AnnotationKind.Piece, Size = 0, Pixels = lifted.Piece,
                Points = { new Point(lifted.At.X, lifted.At.Y), new Point(lifted.At.X + lifted.At.Width, lifted.At.Y + lifted.At.Height) },
            };
            _items.Add(piece);
            // the tool stays as it was (Move stays Move), with the piece picked up
            _selected = piece;
            Render();
            ShowSelection();
            HintText.Text = "Drag it anywhere, or pull a corner to scale it (Shift: stretch freely). Delete removes it; Ctrl+Z puts it back.";
            // still holding the button: carry straight on dragging it
            if (dragFrom != null && Mouse.LeftButton == MouseButtonState.Pressed)
            {
                IsHitTestVisible = true;
                _down = true;
                _moved = false;
                _handle = -1;
                _last = Mouse.GetPosition(Doc);
                Doc.CaptureMouse();
            }
        }
        finally
        {
            IsHitTestVisible = true;
        }
    }

    internal readonly record struct Lifted(BitmapSource Piece, Int32Rect At, BitmapSource Filled);

    internal static Lifted? Lift(BitmapSource image, Int32Rect r, bool keyOut, int radius, bool tighten)
    {
        int w = image.PixelWidth, h = image.PixelHeight;
        var px = new byte[w * h * 4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(px, w * 4, 0);

        // the background: the most common colour in a ring just outside the box
        var counts = new Dictionary<int, (int N, long B, long G, long R)>();
        int ring = 0;
        for (int y = r.Y - 2; y < r.Y + r.Height + 2; y++)
            for (int x = r.X - 2; x < r.X + r.Width + 2; x++)
            {
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                if (x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height) continue;
                int o = (y * w + x) * 4;
                int key = (px[o] >> 4) | (px[o + 1] >> 4 << 4) | (px[o + 2] >> 4 << 8);
                counts.TryGetValue(key, out var c);
                counts[key] = (c.N + 1, c.B + px[o], c.G + px[o + 1], c.R + px[o + 2]);
                ring++;
            }
        bool flat = false;
        int bb = 0, bg = 0, br = 0;
        if (ring > 0)
        {
            var top = counts.Values.MaxBy(c => c.N);
            flat = top.N >= ring * 0.6;
            bb = (int)(top.B / top.N); bg = (int)(top.G / top.N); br = (int)(top.R / top.N);
        }

        var alpha = new float[r.Width * r.Height];
        if (keyOut && flat)
        {
            // how far each pixel is from the background, as coverage (soft edges become see-through)
            int most = 0;
            for (int y = 0; y < r.Height; y++)
                for (int x = 0; x < r.Width; x++)
                {
                    int o = ((r.Y + y) * w + r.X + x) * 4;
                    most = Math.Max(most, Math.Max(Math.Abs(px[o] - bb), Math.Max(Math.Abs(px[o + 1] - bg), Math.Abs(px[o + 2] - br))));
                }
            double full = Math.Max(30, most * 0.85);
            for (int y = 0; y < r.Height; y++)
                for (int x = 0; x < r.Width; x++)
                {
                    int o = ((r.Y + y) * w + r.X + x) * 4;
                    int d = Math.Max(Math.Abs(px[o] - bb), Math.Max(Math.Abs(px[o + 1] - bg), Math.Abs(px[o + 2] - br)));
                    alpha[y * r.Width + x] = (float)Math.Clamp((d - 6) / (full - 6), 0, 1);
                }
        }
        else
        {
            Array.Fill(alpha, 1f);
            if (radius > 0)
                for (int y = 0; y < r.Height; y++)
                    for (int x = 0; x < r.Width; x++)
                    {
                        // round corners: coverage of the pixel by the rounded box
                        double cx = Math.Clamp(x + 0.5, radius, r.Width - radius), cy = Math.Clamp(y + 0.5, radius, r.Height - radius);
                        double dist = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                        alpha[y * r.Width + x] = (float)Math.Clamp(radius - dist + 0.5, 0, 1);
                    }
        }

        // only what's there: shrink to the pixels that are part of the piece
        int x0 = 0, y0 = 0, x1 = r.Width - 1, y1 = r.Height - 1;
        if (tighten || (keyOut && flat))
        {
            x0 = int.MaxValue; y0 = int.MaxValue; x1 = -1; y1 = -1;
            for (int y = 0; y < r.Height; y++)
                for (int x = 0; x < r.Width; x++)
                    if (alpha[y * r.Width + x] > 0.04f)
                    {
                        x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                    }
            if (x1 < 0) return null;
            x0 = Math.Max(0, x0 - 1); y0 = Math.Max(0, y0 - 1); x1 = Math.Min(r.Width - 1, x1 + 1); y1 = Math.Min(r.Height - 1, y1 + 1);
        }
        int pw = x1 - x0 + 1, ph = y1 - y0 + 1;
        var piece = new byte[pw * ph * 4];
        var mask = new bool[w * h];
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                int sx = r.X + x0 + x, sy = r.Y + y0 + y;
                int o = (sy * w + sx) * 4, d = (y * pw + x) * 4;
                float a = alpha[(y0 + y) * r.Width + x0 + x];
                if (a <= 0) continue;
                if (keyOut && flat && a < 1)
                {
                    // the colour of the ink alone, without the background mixed in at soft edges
                    piece[d] = (byte)Math.Clamp(bb + (px[o] - bb) / a, 0, 255);
                    piece[d + 1] = (byte)Math.Clamp(bg + (px[o + 1] - bg) / a, 0, 255);
                    piece[d + 2] = (byte)Math.Clamp(br + (px[o + 2] - br) / a, 0, 255);
                }
                else
                {
                    piece[d] = px[o]; piece[d + 1] = px[o + 1]; piece[d + 2] = px[o + 2];
                }
                piece[d + 3] = (byte)Math.Round(a * 255);
                mask[sy * w + sx] = true;
            }
        // the gap: everything the piece covered, a pixel wider so no fringe is left behind
        var grown = (bool[])mask.Clone();
        for (int y = Math.Max(0, r.Y + y0 - 1); y <= Math.Min(h - 1, r.Y + y1 + 1); y++)
            for (int x = Math.Max(0, r.X + x0 - 1); x <= Math.Min(w - 1, r.X + x1 + 1); x++)
            {
                if (mask[y * w + x]) continue;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && yy >= 0 && xx < w && yy < h && mask[yy * w + xx]) { grown[y * w + x] = true; dx = dy = 2; }
                    }
            }
        var pieceImage = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Bgra32, null, piece, pw * 4);
        pieceImage.Freeze();
        var filled = FillTextHole(image, grown);
        return new Lifted(pieceImage, new Int32Rect(r.X + x0, r.Y + y0, pw, ph), filled);
    }

    // ---- swapping a picture ----

    /// <summary>Puts another picture where one is, filling the same box (cropped to fit) with the same corners.</summary>
    private void ReplacePicture(FoundImage target, BitmapSource replacement)
    {
        CommitText();
        var r = target.Bounds;
        int w = _base.PixelWidth, h = _base.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(_base, new Rect(0, 0, w, h));
            var box = new Rect(r.X, r.Y, r.Width, r.Height);
            dc.PushClip(new RectangleGeometry(box, target.Radius, target.Radius));
            // fill the box, cropping whichever way the new picture is too long
            double scale = Math.Max(box.Width / replacement.PixelWidth, box.Height / replacement.PixelHeight);
            double dw = replacement.PixelWidth * scale, dh = replacement.PixelHeight * scale;
            dc.DrawImage(replacement, new Rect(box.X + (box.Width - dw) / 2, box.Y + (box.Height - dh) / 2, dw, dh));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        rtb.Render(visual);
        rtb.Freeze();
        PushUndo();
        _base = ImageLoader.Normalize(rtb);
        foreach (var mark in _items.Where(m => m.Kind == AnnotationKind.Pixelate)) RemakeHidden(mark);
        ApplyBase();
        Render();
        Toast.Show("Swapped the picture. Ctrl+Z puts the old one back.");
    }

    /// <summary>The right-click menu over a picture inside the screenshot. True if it was shown.</summary>
    private bool ShowPictureMenu(Point p)
    {
        if (PictureUnder(p) is not { } picture) return false;
        var menu = new ContextMenu();
        void Item(string header, Action act, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        Item("Move this picture", () => _ = LiftAsync(picture.Bounds, keyOut: false, picture.Radius, dragFrom: null));
        menu.Items.Add(new Separator());
        Item("Replace with a picture file…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the picture to put there",
                Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*",
            };
            if (dialog.ShowDialog(this) != true) return;
            try { ReplacePicture(picture, ImageLoader.LoadFile(dialog.FileName)); }
            catch { Toast.Show("Couldn't open that picture.", true); }
        });
        bool clip = false;
        try { clip = Clipboard.ContainsImage() || Clipboard.ContainsFileDropList(); } catch { }
        Item("Replace with the clipboard picture", () =>
        {
            if (ImageLoader.FromClipboard(out _) is { } pasted) ReplacePicture(picture, pasted);
            else Toast.Show("There's no picture on the clipboard.", true);
        }, clip);
        menu.IsOpen = true;
        return true;
    }

#if DEBUG
    internal int PicturesForTest() => Pictures().Count;
    internal Task LiftForTest(Int32Rect r, bool keyOut, int radius) => LiftAsync(r, keyOut, radius, null, tighten: keyOut);
    internal void ReplacePictureForTest(int index, BitmapSource replacement) => ReplacePicture(Pictures()[index], replacement);
    internal IReadOnlyList<Annotation> ItemsForTest => _items;
#endif
}
