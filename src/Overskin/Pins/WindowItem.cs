using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Overskin;

/// <summary>One row in the window list. Setters apply straight to the real window.</summary>
public sealed class WindowItem : INotifyPropertyChanged
{
    private readonly WindowManager _manager;
    private readonly Action<string> _onError;
    private readonly Action _onChanged;

    private string _title;
    private ImageSource? _icon;
    private bool _pinned;
    private int _opacity = 100;
    private bool _clickThrough;
    private bool _modified;

    public WindowItem(WindowManager manager, WindowInfo info, Action<string> onError, Action onChanged)
    {
        _manager = manager;
        _onError = onError;
        _onChanged = onChanged;
        Handle = info.Handle;
        _title = info.Title;
        _icon = info.Icon;
        Sync();
    }

    public IntPtr Handle { get; }

    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; Raise(); } }
    }

    public ImageSource? Icon
    {
        get => _icon;
        set { if (!ReferenceEquals(_icon, value)) { _icon = value; Raise(); Raise(nameof(HasIcon)); } }
    }

    public bool HasIcon => _icon != null;

    public bool IsPinned
    {
        get => _pinned;
        set
        {
            if (_pinned == value) return;
            Report(_manager.SetPinned(Handle, value));
        }
    }

    public double Opacity
    {
        get => _opacity;
        set
        {
            int rounded = (int)Math.Round(value);
            if (_opacity == rounded) return;
            Report(_manager.SetOpacity(Handle, rounded));
        }
    }

    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value) return;
            Report(_manager.SetClickThrough(Handle, value));
        }
    }

    public bool IsModified => _modified;
    public string OpacityText => _opacity + "%";

    /// <summary>Short summary shown on collapsed rows, e.g. "60% · click-through".</summary>
    public string Badge
    {
        get
        {
            var parts = new List<string>(2);
            if (_opacity < 100) parts.Add(_opacity + "%");
            if (_clickThrough) parts.Add("click-through");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Re-reads the real state. Call after anything else may have changed the window.</summary>
    public void Sync()
    {
        var s = _manager.GetState(Handle);
        _pinned = s.Pinned;
        _opacity = s.Opacity;
        _clickThrough = s.ClickThrough;
        _modified = s.Modified;
        Raise(nameof(IsPinned));
        Raise(nameof(Opacity));
        Raise(nameof(OpacityText));
        Raise(nameof(ClickThrough));
        Raise(nameof(IsModified));
        Raise(nameof(Badge));
    }

    private void Report(OpResult result)
    {
        if (!result.Ok && result.Error != null) _onError(result.Error);
        Sync();
        _onChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
