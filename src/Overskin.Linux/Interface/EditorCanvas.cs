using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;

namespace Overskin.Linux.Interface;

/// <summary>The picture and everything drawn on it, sized in picture pixels (the editor scales it to fit).</summary>
public sealed class EditorCanvas : Control
{
    public Bitmap Picture { get; }
    public List<Mark> Marks { get; } = new();
    private readonly Stack<List<Mark>> _redo = new();

    public Tool Tool { get; set; } = Tool.Arrow;
    public Color Colour { get; set; } = Color.Parse("#EF4444");
    public double PenWidth { get; set; } = 4;
    public int PixelBlock { get; set; } = 12;

    /// <summary>Asks the window to put a text box here (picture pixels).</summary>
    public event Action<Point>? TextRequested;
    public event Action? Changed;

    private Mark? _drawing;

    public EditorCanvas(Bitmap picture)
    {
        Picture = picture;
        Width = picture.PixelSize.Width;
        Height = picture.PixelSize.Height;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public PixelSize PixelSize => Picture.PixelSize;

    public void Add(Mark mark)
    {
        if (mark is PatchMark patch && patch.Pixels == null) Bake(patch);
        Marks.Add(mark);
        _redo.Clear();
        InvalidateVisual();
        Changed?.Invoke();
    }

    public bool CanUndo => Marks.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Undo()
    {
        if (Marks.Count == 0) return;
        var last = Marks[^1];
        Marks.RemoveAt(Marks.Count - 1);
        _redo.Push(new List<Mark> { last });
        InvalidateVisual();
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        Marks.AddRange(_redo.Pop());
        InvalidateVisual();
        Changed?.Invoke();
    }

    private void Bake(PatchMark patch)
    {
        var area = new PixelRect((int)patch.Area.X, (int)patch.Area.Y, (int)Math.Ceiling(patch.Area.Width), (int)Math.Ceiling(patch.Area.Height))
            .Intersect(new PixelRect(Picture.PixelSize));
        if (area.Width < 1 || area.Height < 1) return;
        patch.Pixels = Imaging.Pixelate(Picture, area, PixelBlock);
    }

    private int NextNumber => Marks.OfType<CounterMark>().Select(c => c.Number).DefaultIfEmpty(0).Max() + 1;

    /// <summary>The finished picture, with the marks drawn in.</summary>
    public Bitmap Export() => Imaging.Render(Picture.PixelSize, DrawAll);

    public void DrawAll(DrawingContext ctx)
    {
        ctx.DrawImage(Picture, new Rect(Picture.Size), new Rect(0, 0, Picture.PixelSize.Width, Picture.PixelSize.Height));
        foreach (var mark in Marks) mark.Draw(ctx);
    }

    public override void Render(DrawingContext ctx)
    {
        DrawAll(ctx);
        _drawing?.Draw(ctx);
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
            case Tool.Pen:
            case Tool.Highlighter:
                var stroke = new StrokeMark { Colour = Colour, Width = PenWidth, Highlighter = Tool == Tool.Highlighter };
                stroke.Points.Add(p);
                _drawing = stroke;
                break;
            case Tool.Pixelate:
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
        if (_drawing == null) return;
        var p = e.GetPosition(this);
        p = new Point(Math.Clamp(p.X, 0, Width), Math.Clamp(p.Y, 0, Height));
        switch (_drawing)
        {
            case StrokeMark s: s.Points.Add(p); break;
            case ShapeMark sh:
                // Shift keeps squares square and arrows straight
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) p = Constrain(sh.From, p, sh.Kind == Tool.Arrow);
                sh.To = p;
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_drawing == null) return;
        e.Pointer.Capture(null);
        var done = _drawing;
        _drawing = null;

        if (done is ShapeMark shape)
        {
            var size = new Rect(shape.From, shape.To).Normalize();
            if (size.Width < 3 && size.Height < 3) { InvalidateVisual(); return; }
            if (Tool == Tool.Pixelate)
            {
                Add(new PatchMark { Area = size });
                return;
            }
        }
        Add(done);
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
