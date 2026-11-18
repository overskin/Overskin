using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Overskin.Linux.Core;
using Overskin.Linux.Platform;

namespace Overskin.Linux.Interface;

/// <summary>
/// A picture that stays on top of everything. Drag to move, scroll to zoom,
/// Ctrl+scroll for see-through, double-click or Esc to close.
/// </summary>
public sealed class PinWindow : Window
{
    private readonly Bitmap _picture;
    private double _zoom = 1;
    private bool _through;

    /// <summary>Every pin that's open, for "make pins clickable again".</summary>
    public static readonly List<PinWindow> All = new();

    /// <summary>Lets clicks pass through to whatever is under the pin, for tracing over it.</summary>
    public bool ClickThrough
    {
        get => _through;
        set
        {
            _through = value;
            if (!Input.SetClickThrough(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, value) && value)
                Desktop.Notify("Can't let clicks through here", "This desktop doesn't allow it for Overskin's windows.", App.IconFile);
        }
    }

    public PinWindow(Bitmap picture)
    {
        _picture = picture;
        Title = "Overskin pin";
        Icon = App.Icon;
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Colours.Accent, 0.8),
            BorderThickness = new Thickness(1),
            Child = new Image { Source = picture, Stretch = Stretch.Fill },
        };
        ApplyZoom();

        var menu = new ContextMenu();
        void Item(string text, Action click)
        {
            var m = new MenuItem { Header = text };
            m.Click += (_, _) => click();
            menu.Items.Add(m);
        }
        Item("Copy", () => _ = Desktop.CopyImageAsync(_picture, this));
        Item("Save", () =>
        {
            var path = App.Settings.NewFilePath();
            Imaging.SavePng(_picture, path);
            Desktop.Notify("Pin saved", Path.GetFileName(path), path);
        });
        Item("Open in the editor", () => new EditorWindow(_picture, null).Show());
        menu.Items.Add(new Separator());
        Item("Actual size", () => { _zoom = 1; ApplyZoom(); });
        Item("100% opaque", () => Opacity = 1);
        Item("75% opaque", () => Opacity = 0.75);
        Item("50% opaque", () => Opacity = 0.5);
        Item("25% opaque", () => Opacity = 0.25);
        Item("Let clicks through, to trace over it", () =>
        {
            if (Opacity > 0.8) Opacity = 0.5;
            ClickThrough = true;
            Desktop.Notify("Clicks now pass through the pin", "To move or close it again: tray menu → Make pins clickable again.", App.IconFile);
        });
        menu.Items.Add(new Separator());
        Item("Close", Close);
        ContextMenu = menu;
        Opened += (_, _) => All.Add(this);
        Closed += (_, _) => All.Remove(this);

        PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) { Close(); return; }
            BeginMoveDrag(e);
        };
        PointerWheelChanged += (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                Opacity = Math.Clamp(Opacity + e.Delta.Y * 0.05, 0.15, 1);
            else
            {
                _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1), 0.1, 8);
                ApplyZoom();
            }
            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control)) _ = Desktop.CopyImageAsync(_picture, this);
        };
    }

    private void ApplyZoom()
    {
        double scaling = Screens.Primary?.Scaling ?? 1;
        Width = Math.Max(24, _picture.PixelSize.Width / scaling * _zoom + 2);
        Height = Math.Max(24, _picture.PixelSize.Height / scaling * _zoom + 2);
    }
}
