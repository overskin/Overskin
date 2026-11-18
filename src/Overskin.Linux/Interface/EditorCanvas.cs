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

    public Tool Tool { get; set; } = Tool.Arrow;
    public Color Colour { get; set; } = Color.Parse("#EF4444");
    public double PenWidth { get; set; } = 4;
    public HideMode HideMode { get; set; } = HideMode.Pixelate;
    /// <summary>1 to 12: how strongly Hide blurs or how big its blocks are.</summary>
    public double HideStrength { get; set; } = 4;
    public double EraseRadius => Math.Max(6, PenWidth * 3);

    /// <summary>Asks the window to put a text box here (picture pixels).</summary>
    public event Action<Point>? TextRequested;
    public event Action? Changed;

    private Mark? _drawing;
    private Point? _cropStart;
    private Point _cropEnd;
    private Point? _hover;

    public EditorCanvas(Bitmap picture)
    {
        Picture = picture;
        Resize();
        ClipToBounds = true;
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

    private int NextNumber => Marks.OfType<CounterMark>().Select(c => c.Number).DefaultIfEmpty(0).Max() + 1;

    /// <summary>The finished picture, with the marks drawn in.</summary>
    public Bitmap Export() => Marks.Count == 0 ? Picture : Imaging.Render(Picture.PixelSize, DrawAll);

    public void DrawAll(DrawingContext ctx)
    {
        ctx.DrawImage(Picture, new Rect(Picture.Size), new Rect(0, 0, Picture.PixelSize.Width, Picture.PixelSize.Height));
        foreach (var mark in Marks) mark.Draw(ctx);
    }

    private static readonly IBrush CropShade = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));

    public override void Render(DrawingContext ctx)
    {
        DrawAll(ctx);
        _drawing?.Draw(ctx);

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
        if (!point.Properties.IsLeftButtonPressed) return;
        var p = point.Position;

        switch (Tool)
        {
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
