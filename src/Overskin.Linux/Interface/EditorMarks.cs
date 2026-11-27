using System.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;
using Size = Avalonia.Size;
using Vector = Avalonia.Vector;

namespace Overskin.Linux.Interface;

/// <summary>
/// Picking up what's been drawn (as on Windows): with the Select tool a mark is clicked to select
/// it, dragged to move it, stretched by its corners or ends, and Delete removes it; right-click
/// for more. The Move tool moves things inside the screenshot itself: a word, an icon or a
/// picture is lifted off as a piece you can drag anywhere (and resize), and the gap it leaves is
/// filled in from what was around it. Pictures inside the screenshot can also be swapped for
/// another picture, keeping their place, size and rounded corners.
/// </summary>
public sealed partial class EditorWindow
{
    private Mark? _selected;
    private int _handle = -1;
    private bool _markDown, _moved;
    private Point _last;

    private Tool _tool => _canvas.Tool;

    /// <summary>The top mark under a point (a little slack all round).</summary>
    private Mark? HitTest(Point p)
    {
        for (int i = _canvas.Marks.Count - 1; i >= 0; i--)
            if (_canvas.Marks[i].Bounds.Inflate(4).Contains(p)) return _canvas.Marks[i];
        return null;
    }

    private int HandleAt(Point p)
    {
        if (_selected == null) return -1;
        var hs = _selected.Handles;
        double reach = 8 / Zoom();
        for (int i = 0; i < hs.Count; i++)
            if (Distance(hs[i], p) <= reach) return i;
        return -1;
    }

    private void Select(Mark? mark)
    {
        _selected = mark;
        _canvas.InvalidateVisual();
    }

    /// <summary>A press with the Select or Move tool. True if it was taken (the pointer is then followed until let go).</summary>
    private bool ToolPress(Point p, int clicks, KeyModifiers keys)
    {
        // a mark already picked: pull a handle to stretch it
        int handle = HandleAt(p);
        if (handle >= 0) return BeginMarkDrag(_selected!, handle, p);
        if (_tool == Tool.Move) return MoveToolDown(p);

        var hit = HitTest(p);
        if (hit is TextMark text && clicks >= 2)
        {
            EditText(text);
            return false;
        }
        if (hit == null)
        {
            Select(null);
            return SelectPress(p, clicks, keys);
        }
        CommitText();
        ClearTextSelection();
        return BeginMarkDrag(hit, -1, p);
    }

    private bool BeginMarkDrag(Mark mark, int handle, Point p)
    {
        CommitText();
        Select(mark);
        _handle = handle;
        _markDown = true;
        _moved = false;
        _last = p;
        _status.Text = mark is PieceMark
            ? "Drag it anywhere, or pull a corner to scale it (Shift: stretch freely). Delete removes it."
            : "Drag to move it, pull a corner or end to stretch it. Delete removes it; right-click for more.";
        return true;
    }

    private void ToolDrag(Point p, KeyModifiers keys)
    {
        if (_moveDragging) { _moveEnd = Clamp(p); _canvas.InvalidateVisual(); return; }
        if (!_markDown) { DragTextSelect(p); return; }
        if (_selected == null) return;
        if (!_moved)
        {
            if (Distance(p, _last) < 2) return;
            _canvas.BeginChange();
            _moved = true;
        }
        Mark now;
        if (_handle >= 0)
        {
            // a lifted piece scales in proportion from the opposite corner unless Shift is held
            now = _selected is PieceMark piece && !keys.HasFlag(KeyModifiers.Shift) ? piece.KeepShape(_handle, p) : _selected.WithHandle(_handle, p);
        }
        else
        {
            now = _selected.Moved(p - _last);
            _last = p;
        }
        _canvas.Swap(_selected, now);
        _selected = now;
    }

    private void ToolRelease()
    {
        if (_moveDragging) { MoveToolUp(); return; }
        if (!_markDown) { EndTextSelect(); return; }
        _markDown = false;
        if (_moved) _canvas.EndChange();
        _moved = false;
    }

    /// <summary>The pointer over a point with the Select or Move tool.</summary>
    private StandardCursorType ToolCursor(Point p)
    {
        if (HandleAt(p) >= 0) return StandardCursorType.SizeAll;
        if (_tool == Tool.Move)
            return _canvas.Marks.Any(m => m is PieceMark && m.Bounds.Contains(p)) || PictureUnder(p) != null || WordAt(p) >= 0
                ? StandardCursorType.SizeAll : StandardCursorType.Cross;
        if (HitTest(p) != null) return StandardCursorType.SizeAll;
        return SelectCursorAt(p);
    }

    private void DeleteSelected()
    {
        if (_selected == null) return;
        var gone = _selected;
        Select(null);
        _canvas.Remove(gone);
    }

    /// <summary>The selected mark's box and handles, and the Move tool's box.</summary>
    private void DrawMarkSelection(DrawingContext ctx)
    {
        double z = Zoom();
        var accent = new SolidColorBrush(Colours.Accent);
        if (_selected != null && _canvas.Marks.Contains(_selected) && _tool is Tool.Select or Tool.Move)
        {
            var b = _selected.Bounds.Inflate(4 / z);
            ctx.DrawRectangle(null, new Pen(accent, 1.2 / z, new DashStyle(new double[] { 4, 3 }, 0)), b);
            double size = 9 / z;
            foreach (var h in _selected.Handles)
                ctx.DrawRectangle(Brushes.White, new Pen(accent, 1.5 / z), new Rect(h.X - size / 2, h.Y - size / 2, size, size));
        }
        if (_moveDragging)
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), new Pen(accent, 1.5 / z, new DashStyle(new double[] { 5, 3 }, 0)),
                new Rect(_moveStart, _moveEnd).Normalize());
    }

    /// <summary>Right-click on a mark: what can be done with it. True if a menu was shown.</summary>
    private bool ShowMarkMenu(Point p)
    {
        var hit = HitTest(p);
        if (hit == null) return false;
        CommitText();
        if (_tool != Tool.Select) Pick(Tool.Select);
        Select(hit);
        var items = new List<(string, Action)>();
        if (hit is TextMark text) items.Add(("Edit text", () => EditText(text)));
        if (hit is ShapeMark { Kind: Tool.Rectangle or Tool.Ellipse } shape)
            items.Add((shape.Filled ? "Outline only" : "Filled", () => { var now = shape.WithFilled(!shape.Filled); _canvas.Replace(shape, now); Select(now); }));
        if (hit is not (PatchMark or PieceMark or SpotlightMark))
            items.Add(("Use the current colour and size", () => { var now = hit.Restyled(_canvas.Colour, _canvas.PenWidth); _canvas.Replace(hit, now); Select(now); }));
        items.Add(("Duplicate", () =>
        {
            var copy = hit.Moved(new Vector(14, 14));
            _canvas.Add(copy);
            Select(copy);
        }));
        items.Add(("-", () => { }));
        items.Add(("Bring to front", () => _canvas.Restack(hit, front: true)));
        items.Add(("Send to back", () => _canvas.Restack(hit, front: false)));
        items.Add(("-", () => { }));
        items.Add(("Delete", DeleteSelected));
        Menu(items.ToArray()).Open(_canvas);
        return true;
    }

    // ---- pictures inside the screenshot ----

    private Bitmap? _picturesFor;
    private List<FoundImage> _pictures = new();

    /// <summary>The pictures inside the screenshot, found once per version of it.</summary>
    private List<FoundImage> Pictures()
    {
        if (ReferenceEquals(_picturesFor, Base)) return _pictures;
        var px = BasePixels();
        try { _pictures = ImageFinder.Find(px, _pxW, _pxH); }
        catch { _pictures = new(); }
        _picturesFor = Base;
        return _pictures;
    }

    private FoundImage? PictureUnder(Point p) => ImageFinder.At(Pictures(), (int)p.X, (int)p.Y);

    // ---- the Move tool ----

    private Point _moveStart, _moveEnd;
    private bool _moveDragging;

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, Base.PixelSize.Width), Math.Clamp(p.Y, 0, Base.PixelSize.Height));

    /// <summary>Move tool, press: a piece already lifted is dragged; a picture or a word is lifted at once; anywhere else starts a box.</summary>
    private bool MoveToolDown(Point p)
    {
        if (_canvas.Marks.LastOrDefault(m => m is PieceMark && m.Bounds.Contains(p)) is { } piece) return BeginMarkDrag(piece, -1, p);
        Select(null);
        if (PictureUnder(p) is { } picture && WordAt(p) < 0)
        {
            _ = LiftAsync(picture.Bounds, keyOut: false, picture.Radius, dragFrom: p);
            return true;
        }
        int word = WordAt(p);
        if (word >= 0)
        {
            var r = _words[word].Bounds.Inflate(new Thickness(1.5, 2 + _words[word].Bounds.Height * 0.12));
            _ = LiftAsync(ToPixels(r), keyOut: true, 0, dragFrom: p);
            return true;
        }
        _moveStart = _moveEnd = Clamp(p);
        _moveDragging = true;
        return true;
    }

    private void MoveToolUp()
    {
        _moveDragging = false;
        _canvas.InvalidateVisual();
        var r = new Rect(_moveStart, _moveEnd).Normalize();
        if (r.Width < 4 || r.Height < 4) return;
        _ = LiftAsync(ToPixels(r), keyOut: true, 0, dragFrom: null, tighten: true);
    }

    private Int32Rect ToPixels(Rect r)
    {
        int x0 = Math.Max(0, (int)Math.Floor(r.X)), y0 = Math.Max(0, (int)Math.Floor(r.Y));
        int x1 = Math.Min(Base.PixelSize.Width, (int)Math.Ceiling(r.Right)), y1 = Math.Min(Base.PixelSize.Height, (int)Math.Ceiling(r.Bottom));
        return new Int32Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>Lifts the selected text off as a piece to drag.</summary>
    private void LiftSelectedText()
    {
        var boxes = SelectedBoxes();
        if (boxes.Count == 0) return;
        var all = boxes.Aggregate((a, b) => a.Union(b)).Inflate(new Thickness(1.5, 2 + boxes.Max(b => b.Height) * 0.12));
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
        var image = Base;
        _status.Text = "Lifting it off…";
        try
        {
            var result = await Task.Run(() => Lift(image, r, keyOut, radius, tighten));
            if (result is not { } lifted) { _status.Text = "Nothing to move there."; _markDown = false; return; }
            var piece = new PieceMark { Area = new Rect(lifted.At.X, lifted.At.Y, lifted.At.Width, lifted.At.Height), Pixels = lifted.Piece };
            ClearTextSelection();
            _canvas.ReplacePictureAndAdd(lifted.Filled, piece);
            // still holding the button: carry straight on dragging it
            if (dragFrom is { } from) BeginMarkDrag(piece, -1, from);
            else Select(piece);
            _status.Text = $"Drag it anywhere, or pull a corner to scale it (Shift: stretch freely). Delete removes it; {Mod}Z puts it back.";
        }
        catch (Exception ex)
        {
            Log.Write("lift: " + ex);
            _status.Text = "Couldn't lift it off: " + ex.Message;
        }
    }

    internal readonly record struct Lifted(Bitmap Piece, Int32Rect At, Bitmap Filled);

    internal static Lifted? Lift(Bitmap image, Int32Rect r, bool keyOut, int radius, bool tighten)
    {
        int w = image.PixelSize.Width, h = image.PixelSize.Height;
        var px = Imaging.Pixels(image, out _);

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
                byte cb, cg, cr;
                if (keyOut && flat && a < 1)
                {
                    // the colour of the ink alone, without the background mixed in at soft edges
                    cb = (byte)Math.Clamp(bb + (px[o] - bb) / a, 0, 255);
                    cg = (byte)Math.Clamp(bg + (px[o + 1] - bg) / a, 0, 255);
                    cr = (byte)Math.Clamp(br + (px[o + 2] - br) / a, 0, 255);
                }
                else { cb = px[o]; cg = px[o + 1]; cr = px[o + 2]; }
                // pictures here keep their colours premultiplied by how see-through they are
                piece[d] = (byte)Math.Round(cb * a);
                piece[d + 1] = (byte)Math.Round(cg * a);
                piece[d + 2] = (byte)Math.Round(cr * a);
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
        var pieceImage = Imaging.FromPixels(piece, new PixelSize(pw, ph));
        var filled = FillTextHole(image, grown);
        return new Lifted(pieceImage, new Int32Rect(r.X + x0, r.Y + y0, pw, ph), filled);
    }

    // ---- swapping a picture ----

    /// <summary>Puts another picture where one is, filling the same box (cropped to fit) with the same corners.</summary>
    private void SwapPicture(FoundImage target, Bitmap replacement)
    {
        CommitText();
        var r = target.Bounds;
        var picture = Base;
        var swapped = Imaging.Render(picture.PixelSize, ctx =>
        {
            ctx.DrawImage(picture, new Rect(picture.Size), new Rect(0, 0, picture.PixelSize.Width, picture.PixelSize.Height));
            var box = new Rect(r.X, r.Y, r.Width, r.Height);
            using (ctx.PushGeometryClip(new RectangleGeometry(box, target.Radius, target.Radius)))
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                // fill the box, cropping whichever way the new picture is too long
                double scale = Math.Max(box.Width / replacement.PixelSize.Width, box.Height / replacement.PixelSize.Height);
                double dw = replacement.PixelSize.Width * scale, dh = replacement.PixelSize.Height * scale;
                ctx.DrawImage(replacement, new Rect(replacement.Size), new Rect(box.X + (box.Width - dw) / 2, box.Y + (box.Height - dh) / 2, dw, dh));
            }
        });
        _canvas.ReplacePicture(_ => swapped, flatten: false);
        _status.Text = $"Swapped the picture. {Mod}Z puts the old one back.";
    }

    /// <summary>The right-click menu over a picture inside the screenshot. True if it was shown.</summary>
    private bool ShowPictureMenu(Point p)
    {
        if (PictureUnder(p) is not { } picture) return false;
        Menu(("Move this picture", () => _ = LiftAsync(picture.Bounds, keyOut: false, picture.Radius, dragFrom: null)),
             ("-", () => { }),
             ("Replace with a picture file…", () => _ = ReplaceFromFileAsync(picture)),
             ("Replace with the clipboard picture", () => _ = ReplaceFromClipboardAsync(picture))).Open(_canvas);
        return true;
    }

    private async Task ReplaceFromFileAsync(FoundImage picture)
    {
        var path = await Ui.PickPictureAsync(this);
        if (path == null) return;
        try { SwapPicture(picture, new Bitmap(path)); }
        catch { _status.Text = "Couldn't open that picture."; }
    }

    private async Task ReplaceFromClipboardAsync(FoundImage picture)
    {
        if (await Desktop.PastePictureAsync(this) is { } pasted) SwapPicture(picture, pasted);
        else _status.Text = "There's no picture on the clipboard.";
    }

    // ---- for the self-test ----

    internal int PicturesForTest() => Pictures().Count;
    internal Task LiftForTest(Int32Rect r, bool keyOut, int radius) => LiftAsync(r, keyOut, radius, null, tighten: keyOut);
    internal void SwapPictureForTest(int index, Bitmap replacement) => SwapPicture(Pictures()[index], replacement);
    internal IReadOnlyList<Mark> MarksForTest => _canvas.Marks;
    internal Mark? SelectedForTest => _selected;

    /// <summary>A press, a drag and a release with the current tool, as the mouse would.</summary>
    internal void DragForTest(Point from, Point to, int clicks = 1)
    {
        if (!ToolPress(from, clicks, KeyModifiers.None)) return;
        for (int k = 1; k <= 4; k++) ToolDrag(from + (to - from) * (k / 4.0), KeyModifiers.None);
        ToolRelease();
    }

    internal void PickForTest(Tool tool) => Pick(tool);
    internal void DeleteSelectedForTest() => DeleteSelected();
}
