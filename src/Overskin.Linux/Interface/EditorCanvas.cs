using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;

namespace Overskin.Linux.Interface;

/// <summary>
/// The picture and everything drawn on it, sized in picture pixels (the editor scales it to fit).
/// Every change, marks or the picture itself (crop, erase, turn), can be undone.
/// </summary>
public sealed class EditorCanvas : Control
{
    public Bitmap Picture { get; private set; }
    public List<Mark> Marks { get; private set; } = new();

    private readonly Stack<(Bitmap Picture, List<Mark> Marks)> _undo = new();
    private readonly Stack<(Bitmap Picture, List<Mark> Marks)> _redo = new();

    private Tool _tool = Tool.Select;
    public Tool Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            _cursorKind = value is Tool.Select or Tool.Move ? StandardCursorType.Arrow : StandardCursorType.Cross;
            Cursor = new Cursor(_cursorKind);
        }
    }
    public Color Colour { get; set; } = Color.Parse("#EF4444");
    public double PenWidth { get; set; } = 4;
    public HideMode HideMode { get; set; } = HideMode.Pixelate;
    /// <summary>1 to 12: how strongly Hide blurs or how big its blocks are.</summary>
    public double HideStrength { get; set; } = 4;
    public double EraseRadius => Math.Max(6, PenWidth * 3);

    /// <summary>Asks the window to put a text box here (picture pixels).</summary>
    public event Action<Point>? TextRequested;
    public event Action? Changed;

    // the Select tool's text editing lives in the window: presses, drags and releases go there
    /// <summary>A press with the Select tool (point, clicks, keys held); true if it was taken.</summary>
    public Func<Point, int, KeyModifiers, bool>? SelectPressed;
    public Action<Point, KeyModifiers>? SelectDragged;
    public Action? SelectReleased;
    /// <summary>The pointer to show over a point with the Select tool.</summary>
    public Func<Point, StandardCursorType>? SelectHover;
    /// <summary>A right-click; true if a menu was shown.</summary>
    public Func<Point, bool>? RightPressed;
    /// <summary>Drawn on top of everything (the text selection and cursor).</summary>
    public Action<DrawingContext>? Overlay;
    private bool _selecting;

    private Mark? _drawing;
    private Point? _cropStart;
    private Point _cropEnd;
    private Point? _hover;
    private StandardCursorType _cursorKind = StandardCursorType.Cross;

    public EditorCanvas(Bitmap picture)
    {
        Picture = picture;
        Resize();
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    private void Resize()
    {
        Width = Picture.PixelSize.Width;
        Height = Picture.PixelSize.Height;
    }

    public PixelSize PixelSize => Picture.PixelSize;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private void Remember()
    {
        _undo.Push((Picture, Marks.ToList()));
        _redo.Clear();
    }

    private void Done()
    {
        InvalidateVisual();
        Changed?.Invoke();
    }

    public void Add(Mark mark)
    {
        if (mark is PatchMark patch && patch.Pixels == null) Bake(patch);
        Remember();
        Marks.Add(mark);
        Done();
    }

    public void AddRange(IEnumerable<Mark> marks)
    {
        var list = marks.ToList();
        if (list.Count == 0) return;
        foreach (var p in list.OfType<PatchMark>()) if (p.Pixels == null) Bake(p);
        Remember();
        Marks.AddRange(list);
        Done();
    }

    /// <summary>Replaces the picture. With <paramref name="flatten"/>, the marks are drawn into it first.</summary>
    public void ReplacePicture(Func<Bitmap, Bitmap> change, bool flatten)
    {
        var source = flatten ? Export() : Picture;
        var result = change(source);
        Remember();
        Picture = result;
        if (flatten) Marks = new List<Mark>();
        // hidden parts show the new picture, blurred again (new marks, so undo keeps the old ones)
        else Marks = Marks.Select(m => m is PatchMark { Pixels: not null } p ? Rebaked(p) : m).ToList();
        Resize();
        Done();
    }

    // ---- changing one mark (the Select tool): remembered once, then swapped as it's dragged ----

    /// <summary>Remembers how things are, before a change made in steps (dragging a mark).</summary>
    public void BeginChange() => Remember();

    /// <summary>Puts <paramref name="now"/> where <paramref name="old"/> was, without another undo step.</summary>
    public void Swap(Mark old, Mark now)
    {
        int i = Marks.IndexOf(old);
        if (i < 0) return;
        if (now is PatchMark { Pixels: null } patch) Bake(patch);
        Marks[i] = now;
        InvalidateVisual();
    }

    /// <summary>Finishes a change made in steps.</summary>
    public void EndChange() => Done();

    /// <summary>One whole change to one mark, as its own undo step.</summary>
    public void Replace(Mark old, Mark now)
    {
        if (!Marks.Contains(old)) return;
        Remember();
        Swap(old, now);
        Done();
    }

    public void Remove(Mark mark)
    {
        if (!Marks.Contains(mark)) return;
        Remember();
        Marks.Remove(mark);
        Done();
    }

    /// <summary>Takes a mark out or puts it back without an undo step (while its text is being edited).</summary>
    public void RemoveQuietly(Mark mark) { Marks.Remove(mark); InvalidateVisual(); }
    public void InsertQuietly(int index, Mark mark) { Marks.Insert(Math.Clamp(index, 0, Marks.Count), mark); InvalidateVisual(); }

    /// <summary>To the front (on top of the others) or the back.</summary>
    public void Restack(Mark mark, bool front)
    {
        if (!Marks.Contains(mark)) return;
        Remember();
        Marks.Remove(mark);
        if (front) Marks.Add(mark); else Marks.Insert(0, mark);
        Done();
    }

    /// <summary>The picture swapped and a mark added, as one undo step (a piece lifted off).</summary>
    public void ReplacePictureAndAdd(Bitmap picture, Mark mark)
    {
        Remember();
        Picture = picture;
        Marks = Marks.Select(m => m is PatchMark { Pixels: not null } p ? Rebaked(p) : m).ToList();
        Marks.Add(mark);
        Resize();
        Done();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push((Picture, Marks.ToList()));
        (Picture, Marks) = _undo.Pop();
        Resize();
        Done();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push((Picture, Marks.ToList()));
        (Picture, Marks) = _redo.Pop();
        Resize();
        Done();
    }

    private void Bake(PatchMark patch)
    {
        var area = new PixelRect((int)Math.Floor(patch.Area.X), (int)Math.Floor(patch.Area.Y),
                                 (int)Math.Ceiling(patch.Area.Width), (int)Math.Ceiling(patch.Area.Height));
        patch.Pixels = Effects.Hide(Picture, area, patch.Mode, patch.Strength);
    }

    private PatchMark Rebaked(PatchMark p)
    {
        var copy = new PatchMark { Area = p.Area, Mode = p.Mode, Strength = p.Strength };
        Bake(copy);
        return copy;
    }

    private int NextNumber => Marks.OfType<CounterMark>().Select(c => c.Number).DefaultIfEmpty(0).Max() + 1;

    /// <summary>The finished picture, with the marks drawn in.</summary>
    public Bitmap Export() => Marks.Count == 0 ? Picture : Imaging.Render(Picture.PixelSize, DrawAll);

    public void DrawAll(DrawingContext ctx)
    {
        ctx.DrawImage(Picture, new Rect(Picture.Size), new Rect(0, 0, Picture.PixelSize.Width, Picture.PixelSize.Height));
        // spotlights darken everything round them, under the other marks
        var spots = Marks.OfType<SpotlightMark>().Select(m => m.Area).ToList();
        if (_drawing is ShapeMark { Kind: Tool.Spotlight } growing) spots.Add(new Rect(growing.From, growing.To).Normalize());
        SpotlightMark.DrawDim(ctx, spots, new Size(Picture.PixelSize.Width, Picture.PixelSize.Height));
        foreach (var mark in Marks) mark.Draw(ctx);
    }

    private static readonly IBrush CropShade = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));

    private Bitmap? _clearFor;
    private bool _clear;

    /// <summary>Whether the picture has see-through parts (a cut-out), worked out once per picture.</summary>
    private bool SeeThrough
    {
        get
        {
            if (!ReferenceEquals(_clearFor, Picture))
            {
                _clearFor = Picture;
                var px = Imaging.Pixels(Picture, out _);
                _clear = false;
                for (int i = 3; i < px.Length; i += 4) if (px[i] < 250) { _clear = true; break; }
            }
            return _clear;
        }
    }

    private static readonly IBrush Checker = new DrawingBrush
    {
        TileMode = TileMode.Tile,
        DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
        Drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Brush = Brushes.White, Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)) },
                new GeometryDrawing
                {
                    Brush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)),
                    Geometry = new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8)) } },
                },
            },
        },
    };

    public override void Render(DrawingContext ctx)
    {
        // see-through parts show as a light checkerboard, not whatever is behind the editor
        if (SeeThrough) ctx.FillRectangle(Checker, new Rect(0, 0, Picture.PixelSize.Width, Picture.PixelSize.Height));
        DrawAll(ctx);
        _drawing?.Draw(ctx);
        Overlay?.Invoke(ctx);

        if (Tool == Tool.Crop && _cropStart is { } start)
        {
            var sel = new Rect(start, _cropEnd).Normalize();
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(Bounds.Size)), new RectangleGeometry(sel));
            ctx.DrawGeometry(CropShade, null, outside);
            ctx.DrawRectangle(null, new Pen(Brushes.White, 1.5, new DashStyle(new double[] { 4, 3 }, 0)), sel);
        }
        if (Tool == Tool.Erase && _hover is { } h)
        {
            ctx.DrawEllipse(null, new Pen(Brushes.White, 1.5), h, EraseRadius, EraseRadius);
            ctx.DrawEllipse(null, new Pen(Brushes.Black, 1), h, EraseRadius + 1.5, EraseRadius + 1.5);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var p = point.Position;
        if (point.Properties.IsRightButtonPressed)
        {
            if (RightPressed?.Invoke(p) == true) e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        switch (Tool)
        {
            case Tool.Select:
            case Tool.Move:
                Focus(); // typing and the arrow keys come here
                if (SelectPressed?.Invoke(p, e.ClickCount, e.KeyModifiers) == true)
                {
                    _selecting = true;
                    e.Pointer.Capture(this);
                }
                return;
            case Tool.Text:
                TextRequested?.Invoke(p);
                return;
            case Tool.Counter:
                Add(new CounterMark { At = p, Number = NextNumber, Colour = Colour, Width = PenWidth });
                return;
            case Tool.Crop:
                _cropStart = p;
                _cropEnd = p;
                break;
            case Tool.Pen:
            case Tool.Highlighter:
            case Tool.Erase:
                var stroke = Tool == Tool.Erase
                    ? new StrokeMark { Colour = Color.FromArgb(150, 255, 80, 80), Width = EraseRadius * 2 }
                    : new StrokeMark { Colour = Colour, Width = PenWidth, Highlighter = Tool == Tool.Highlighter };
                stroke.Points.Add(p);
                _drawing = stroke;
                break;
            case Tool.Hide:
                _drawing = new ShapeMark { Kind = Tool.Rectangle, From = p, To = p, Colour = Colors.White, Width = 1 };
                break;
            default:
                _drawing = new ShapeMark { Kind = Tool, From = p, To = p, Colour = Colour, Width = PenWidth };
                break;
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        p = new Point(Math.Clamp(p.X, 0, Width), Math.Clamp(p.Y, 0, Height));
        _hover = p;
        if (_selecting) { SelectDragged?.Invoke(e.GetPosition(this), e.KeyModifiers); return; }
        if (Tool is Tool.Select or Tool.Move)
        {
            var kind = SelectHover?.Invoke(p) ?? StandardCursorType.Arrow;
            if (_cursorKind != kind) { _cursorKind = kind; Cursor = new Cursor(kind); }
            return;
        }
        if (_cropStart != null) _cropEnd = p;
        switch (_drawing)
        {
            case StrokeMark s: s.Points.Add(p); break;
            case ShapeMark sh:
                // Shift keeps squares square and lines straight
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) p = Constrain(sh.From, p, sh.Kind is Tool.Arrow or Tool.Line);
                sh.To = p;
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_selecting)
        {
            _selecting = false;
            SelectReleased?.Invoke();
            return;
        }

        if (_cropStart is { } start)
        {
            _cropStart = null;
            var r = new Rect(start, _cropEnd).Normalize();
            if (r.Width >= 4 && r.Height >= 4) Crop(r);
            else InvalidateVisual();
            return;
        }

        if (_drawing == null) return;
        var done = _drawing;
        _drawing = null;

        if (Tool == Tool.Erase && done is StrokeMark erase)
        {
            var mask = Effects.StrokeMask(erase.Points, EraseRadius, Picture.PixelSize.Width, Picture.PixelSize.Height);
            ReplacePicture(p => Effects.Inpaint(p, mask), flatten: false);
            return;
        }

        if (done is ShapeMark shape)
        {
            var size = new Rect(shape.From, shape.To).Normalize();
            if (size.Width < 3 && size.Height < 3) { InvalidateVisual(); return; }
            if (Tool == Tool.Hide)
            {
                Add(new PatchMark { Area = size, Mode = HideMode, Strength = HideStrength });
                return;
            }
            if (Tool == Tool.Spotlight)
            {
                Add(new SpotlightMark { Area = size });
                return;
            }
        }
        Add(done);
    }

    /// <summary>Keeps only part of the picture; marks are drawn in first so nothing shifts.</summary>
    public void Crop(Rect area)
    {
        var px = new PixelRect((int)area.X, (int)area.Y, (int)Math.Ceiling(area.Width), (int)Math.Ceiling(area.Height))
            .Intersect(new PixelRect(Picture.PixelSize));
        if (px.Width < 1 || px.Height < 1) return;
        ReplacePicture(p => Imaging.Crop(p, px), flatten: true);
    }

    private static Point Constrain(Point from, Point to, bool angles)
    {
        var d = to - from;
        if (angles)
        {
            double angle = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 4)) * (Math.PI / 4);
            double length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            return from + new Point(Math.Cos(angle) * length, Math.Sin(angle) * length);
        }
        double side = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
        return from + new Point(Math.Sign(d.X) * side, Math.Sign(d.Y) * side);
    }
}
