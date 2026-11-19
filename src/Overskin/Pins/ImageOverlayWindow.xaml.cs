using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using static Overskin.NativeMethods;

namespace Overskin;

/// <summary>
/// A picture floating above everything else. Doubles as tracing paper when click-through.
/// Every setting is a bindable property, so the main window can change it even while
/// the picture itself ignores the mouse.
/// </summary>
public partial class ImageOverlayWindow : Window, INotifyPropertyChanged
{
    /// <summary>Room left around the picture's box for the handles and for rotating outside it.</summary>
    private double _appliedPad;
    private const double MinSide = 24;

    private readonly BitmapSource _original;
    private readonly WindowManager _manager;
    private BitmapSource? _gray;
    private readonly double _aspect;
    private double _dpiScale = 1.0;

    // size of the picture itself, before rotation, in WPF units
    private double _imageW, _imageH;
    private double _angle;

    private bool _clickThrough;
    private bool _grabbed;
    private bool _locked;
    private bool _grayscale;

    public ImageOverlayWindow(BitmapSource image, string name, int opacityPercent, WindowManager manager)
    {
        InitializeComponent();
        _original = image;
        _manager = manager;
        DisplayName = name;
        Picture.Source = image;
        _aspect = (double)image.PixelWidth / image.PixelHeight;
        Opacity = Math.Clamp(opacityPercent, 10, 100) / 100.0;

        // provisional size; corrected for the real monitor DPI once the window has a handle
        _imageW = image.PixelWidth;
        _imageH = image.PixelHeight;
        ApplySize();
    }

    public IntPtr Handle { get; private set; }
    public string DisplayName { get; }
    public ImageSource Thumbnail => _original;
    public BitmapSource Original => _original;

    /// <summary>The file this picture came from (pasted and snipped pictures are saved to a cache file).</summary>
    public string? SourcePath { get; set; }

    /// <summary>True when <see cref="SourcePath"/> is a cache file the app owns and may delete.</summary>
    public bool OwnsSourceFile { get; set; }

    /// <summary>Set before showing to reopen exactly as it was last time.</summary>
    public PinnedImageState? RestoreState { get; set; }

    /// <summary>Set before showing to place the picture at actual size over this physical screen rectangle.</summary>
    public Int32Rect? PlaceAt { get; set; }

    /// <summary>Asks the app to load this picture into the Trace tab.</summary>
    public event Action<ImageOverlayWindow>? SendToTraceRequested;

    /// <summary>Raised when opacity changes, so the app can remember it for the next image.</summary>
    public event Action<int>? OpacityChanged;

    /// <summary>Raised when click-through is switched on from the picture's own menu.</summary>
    public event Action? ClickThroughEnabledFromMenu;

    // ---- bindable state ------------------------------------------------------------------

    public string Details
    {
        get
        {
            string text = $"{_original.PixelWidth} × {_original.PixelHeight}  ·  {OpacityPercent}%";
            if (Math.Abs(_angle) > 0.01) text += $"  ·  {_angle:0}°";
            return text;
        }
    }

    public double OpacityPercent
    {
        get => Math.Round(Opacity * 100);
        set
        {
            int percent = (int)Math.Clamp(Math.Round(value), 10, 100);
            if (percent == (int)OpacityPercent) return;
            Opacity = percent / 100.0;
            Raise();
            Raise(nameof(OpacityText));
            Raise(nameof(Details));
            OpacityChanged?.Invoke(percent);
        }
    }

    public string OpacityText => (int)OpacityPercent + "%";

    /// <summary>Clockwise rotation in degrees, kept within -180..180.</summary>
    public double Rotation
    {
        get => _angle;
        set
        {
            double angle = Math.Round(value) % 360;
            if (angle > 180) angle -= 360;
            if (angle < -180) angle += 360;
            if (Math.Abs(angle - _angle) < 0.01) return;

            _angle = angle;
            Spin.Angle = angle;
            ApplySize(keepCentre: true);
            Raise();
            Raise(nameof(RotationText));
            Raise(nameof(Details));
        }
    }

    public string RotationText => $"{_angle:0}°";

    public bool FlipHorizontal
    {
        get => Flip.ScaleX < 0;
        set { if (FlipHorizontal != value) { Flip.ScaleX = value ? -1 : 1; Raise(); } }
    }

    public bool FlipVertical
    {
        get => Flip.ScaleY < 0;
        set { if (FlipVertical != value) { Flip.ScaleY = value ? -1 : 1; Raise(); } }
    }

    public bool Grayscale
    {
        get => _grayscale;
        set
        {
            if (_grayscale == value) return;
            _grayscale = value;
            Picture.Source = value ? _gray ??= ImageLoader.ToGrayscale(_original) : _original;
            Raise();
        }
    }

    public bool Locked
    {
        get => _locked;
        set { if (_locked != value) { _locked = value; if (value) Transforming = false; Raise(); } }
    }

    public bool IsClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value || Handle == IntPtr.Zero) return;
            if (value) Transforming = false;
            _clickThrough = value;
            _grabbed = false;
            ApplyMouseTransparency(value);

            if (value) _manager.OwnClickThroughWindows.Add(Handle);
            else _manager.OwnClickThroughWindows.Remove(Handle);

            ShowFrame(false);
            Raise();
        }
    }

    /// <summary>
    /// While click-through is on, temporarily lets the mouse reach the picture again
    /// (the app turns this on while the grab keys are held).
    /// </summary>
    public void SetGrabbed(bool grabbed)
    {
        if (!_clickThrough || _grabbed == grabbed || Handle == IntPtr.Zero) return;
        _grabbed = grabbed;
        ApplyMouseTransparency(!grabbed);
        ShowFrame(grabbed);
    }

    public void ResetView()
    {
        var centre = new Point(Left + Width / 2, Top + Height / 2);
        _imageW = _original.PixelWidth / _dpiScale;
        _imageH = _original.PixelHeight / _dpiScale;
        _angle = 0;
        Spin.Angle = 0;
        ApplySize();
        Left = centre.X - Width / 2;
        Top = centre.Y - Height / 2;
        Raise(nameof(Rotation));
        Raise(nameof(RotationText));
        Raise(nameof(Details));
    }

    // ---- setup ---------------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;

        // keep it out of Alt+Tab
        TrySetExStyle(Handle, GetExStyle(Handle) | WS_EX_TOOLWINDOW, out _);

        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        if (RestoreState is { } state && ApplyState(state)) return;

        FitToScreen(shrinkToFit: PlaceAt == null);
        if (PlaceAt is { } r)
            SetWindowPos(Handle, IntPtr.Zero, r.X, r.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Puts back size, place and every option. False if the saved spot is off every screen.</summary>
    private bool ApplyState(PinnedImageState s)
    {
        double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        if (s.Width < MinSide || s.Left + 40 > vx + vw || s.Top + 40 > vy + vh || s.Left + s.Width < vx + 40 || s.Top < vy - 40)
            return false;

        _imageW = s.Width;
        _imageH = _imageW / _aspect;
        if (s.Height >= MinSide) _imageH = s.Height; // stretched with the side handles
        Rotation = s.Rotation;
        ApplySize();
        Left = s.Left;
        Top = s.Top;
        OpacityPercent = s.Opacity;
        FlipHorizontal = s.FlipHorizontal;
        FlipVertical = s.FlipVertical;
        Grayscale = s.Grayscale;
        Locked = s.Locked;
        IsClickThrough = s.ClickThrough;
        GridDivisions = s.Grid;
        return true;
    }

    /// <summary>Everything needed to reopen this picture as it is now.</summary>
    public PinnedImageState ToState() => new()
    {
        Path = SourcePath ?? "",
        Name = DisplayName,
        // where the picture's box is, not the room around it while transforming
        Left = Left + _appliedPad,
        Top = Top + _appliedPad,
        Width = _imageW,
        Height = _imageH,
        Opacity = OpacityPercent,
        Rotation = _angle,
        FlipHorizontal = FlipHorizontal,
        FlipVertical = FlipVertical,
        Grayscale = Grayscale,
        Locked = Locked,
        ClickThrough = IsClickThrough,
        Grid = _grid,
    };

    public void CopyToClipboard()
    {
        try
        {
            Clipboard.SetImage(_original);
            Toast.Show("Image copied.");
        }
        catch
        {
            Toast.Show("The clipboard is busy. Try again.", true);
        }
    }

    public void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save image",
            Filter = "PNG image|*.png",
            FileName = System.IO.Path.GetFileNameWithoutExtension(DisplayName) + ".png",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_original));
            using var file = System.IO.File.Create(dialog.FileName);
            encoder.Save(file);
            Toast.Show("Saved " + System.IO.Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            Toast.Show("Couldn't save: " + ex.Message, true);
        }
    }

    public void RequestTrace() => SendToTraceRequested?.Invoke(this);

    // ---- drawing grid --------------------------------------------------------------------

    private int _grid;
    private int _lastGrid = 4;

    /// <summary>Squares over the picture (0 = none), for copying it by hand one square at a time.</summary>
    public int GridDivisions
    {
        get => _grid;
        set
        {
            value = value is 0 or 3 or 4 or 6 or 8 ? value : 4;
            if (_grid == value) return;
            _grid = value;
            if (value > 0) _lastGrid = value;
            DrawGrid();
            Raise();
            Raise(nameof(ShowGrid));
        }
    }

    public bool ShowGrid
    {
        get => _grid > 0;
        set => GridDivisions = value ? _lastGrid : 0;
    }

    private void DrawGrid()
    {
        GridLines.Children.Clear();
        if (_grid <= 0 || _imageW <= 0) return;
        // each line twice, dark under light, so it reads on any picture
        var dark = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
        var light = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));
        dark.Freeze();
        light.Freeze();
        for (int i = 1; i < _grid; i++)
        {
            double x = _imageW * i / _grid, y = _imageH * i / _grid;
            foreach (var (brush, width) in new[] { (dark, 2.5), (light, 1.0) })
            {
                GridLines.Children.Add(new System.Windows.Shapes.Line { X1 = x, Y1 = 0, X2 = x, Y2 = _imageH, Stroke = brush, StrokeThickness = width });
                GridLines.Children.Add(new System.Windows.Shapes.Line { X1 = 0, Y1 = y, X2 = _imageW, Y2 = y, Stroke = brush, StrokeThickness = width });
            }
        }
    }

    private void GridPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int n)) GridDivisions = n;
    }

    /// <summary>Starts at one image pixel per screen pixel, shrunk if that would cover the screen.</summary>
    private void FitToScreen(bool shrinkToFit = true)
    {
        var area = SystemParameters.WorkArea;
        double w = _original.PixelWidth / _dpiScale, h = _original.PixelHeight / _dpiScale;
        double k = shrinkToFit ? Math.Min(1.0, Math.Min(area.Width * 0.7 / w, area.Height * 0.7 / h)) : 1.0;
        _imageW = Math.Max(MinSide, w * k);
        _imageH = _imageW / _aspect;
        ApplySize();
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    /// <summary>Nudges the window so several pinned images don't stack exactly.</summary>
    public void Cascade(int index)
    {
        double offset = 26 * (index % 8);
        Left += offset;
        Top += offset;
    }

    /// <summary>The window is the bounding box of the rotated picture.</summary>
    private void ApplySize(bool keepCentre = false)
    {
        double rad = _angle * Math.PI / 180;
        double cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
        double boxW = Math.Max(MinSide, _imageW * cos + _imageH * sin);
        double boxH = Math.Max(MinSide, _imageW * sin + _imageH * cos);

        // while the transform box shows, the window has room around the picture for rotating
        double pad = Pad;
        if (keepCentre && !double.IsNaN(Left) && !double.IsNaN(Top))
        {
            Left += (Width - boxW - 2 * pad) / 2;
            Top += (Height - boxH - 2 * pad) / 2;
        }
        _appliedPad = pad;

        Frame.Margin = new Thickness(pad);
        Width = boxW + 2 * pad;
        Height = boxH + 2 * pad;
        LayoutContent(new Point(pad + boxW / 2, pad + boxH / 2));
    }

    private void ApplyMouseTransparency(bool transparent)
    {
        long ex = GetExStyle(Handle);
        ex = transparent ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        TrySetExStyle(Handle, ex, out _);
    }

    private void ShowFrame(bool visible) =>
        Frame.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 0.6 : 0, TimeSpan.FromMilliseconds(visible ? 90 : 150)));

    // ---- mouse and keyboard --------------------------------------------------------------

    private void Resize(double factor, Point anchor)
    {
        double newW = _imageW * factor, newH = _imageH * factor;
        if (newW < MinSide || newH < MinSide || newW > SystemParameters.VirtualScreenWidth * 2) return;

        // the bounding box scales by the same factor, so the pixel under the cursor stays put
        Left -= anchor.X * (factor - 1);
        Top -= anchor.Y * (factor - 1);
        _imageW = newW;
        _imageH = newH;
        ApplySize();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        int direction = e.Delta > 0 ? 1 : -1;

        // while grabbed, Ctrl is part of the grab chord, so it can't also mean "opacity"
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !_grabbed;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (shift) Rotation += direction * 5;
        else if (ctrl) OpacityPercent += direction * 5;
        else if (!_locked) Resize(direction > 0 ? 1.08 : 1 / 1.08, e.GetPosition(this));
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // Opacity is the user's setting, so the entrance animates the content instead
        Root.Opacity = 0;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (!_clickThrough && !_transforming) ShowFrame(true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_grabbed) ShowFrame(false);
    }

    protected override void OnClosed(EventArgs e)
    {
        _manager.OwnClickThroughWindows.Remove(Handle);
        base.OnClosed(e);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpiScale = newDpi.DpiScaleX;
    }

    // ---- menu ----------------------------------------------------------------------------

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        int percent = (int)OpacityPercent;
        Opacity100.IsChecked = percent == 100;
        Opacity75.IsChecked = percent == 75;
        Opacity50.IsChecked = percent == 50;
        Opacity25.IsChecked = percent == 25;
        ClickThroughItem.IsChecked = _clickThrough;
        LockItem.IsChecked = _locked;
        FlipItem.IsChecked = FlipHorizontal;
        FlipVItem.IsChecked = FlipVertical;
        GrayItem.IsChecked = _grayscale;
        Grid0.IsChecked = _grid == 0;
        Grid3.IsChecked = _grid == 3;
        Grid4.IsChecked = _grid == 4;
        Grid6.IsChecked = _grid == 6;
        Grid8.IsChecked = _grid == 8;
    }

    private void OpacityPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int percent))
            OpacityPercent = percent;
    }

    private void ClickThrough_Click(object sender, RoutedEventArgs e)
    {
        IsClickThrough = !_clickThrough;
        if (_clickThrough) ClickThroughEnabledFromMenu?.Invoke();
    }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int degrees))
            Rotation += degrees;
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => Locked = !Locked;
    private void Flip_Click(object sender, RoutedEventArgs e) => FlipHorizontal = !FlipHorizontal;
    private void FlipV_Click(object sender, RoutedEventArgs e) => FlipVertical = !FlipVertical;
    private void Gray_Click(object sender, RoutedEventArgs e) => Grayscale = !Grayscale;
    private void Reset_Click(object sender, RoutedEventArgs e) => ResetView();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyToClipboard();
    private void Save_Click(object sender, RoutedEventArgs e) => SaveAs();
    private void Trace_Click(object sender, RoutedEventArgs e) => RequestTrace();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
