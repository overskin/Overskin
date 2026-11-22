using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Overskin;

/// <summary>
/// Turns a settings card (a Border holding a StackPanel whose first child is its heading) into
/// one that folds away to just its heading, with a short summary of its settings beside it.
/// Everything below the heading moves into one panel that is shown or hidden, so rows that hide
/// themselves keep doing so.
/// </summary>
public sealed class CardFold
{
    private readonly StackPanel _body;
    private readonly TextBlock _summary;
    private readonly RotateTransform _turn;
    private readonly Func<string> _describe;
    private readonly Action<bool> _changed;

    public bool IsOpen { get; private set; }

    private CardFold(Border card, bool open, Func<string> describe, Action<bool> changed)
    {
        _describe = describe;
        _changed = changed;
        var panel = (StackPanel)card.Child;
        var heading = (FrameworkElement)panel.Children[0];
        panel.Children.RemoveAt(0);

        // everything else (the rule and the rows) moves into the body
        _body = new StackPanel();
        while (panel.Children.Count > 0)
        {
            var child = panel.Children[0];
            panel.Children.RemoveAt(0);
            _body.Children.Add(child);
        }

        _summary = new TextBlock
        {
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");

        _turn = new RotateTransform(open ? 90 : 0);
        var chevron = new Icon
        {
            Data = (Geometry)Application.Current.FindResource("IcChevronRight"),
            Width = 12,
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _turn,
        };
        chevron.SetResourceReference(Overskin.Icon.ForegroundProperty, "TextDim");

        var bar = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, MinHeight = 22 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(heading);
        Grid.SetColumn(_summary, 1);
        bar.Children.Add(_summary);
        Grid.SetColumn(chevron, 2);
        bar.Children.Add(chevron);
        bar.MouseEnter += (_, _) => chevron.SetResourceReference(Overskin.Icon.ForegroundProperty, "Text");
        bar.MouseLeave += (_, _) => chevron.SetResourceReference(Overskin.Icon.ForegroundProperty, "TextDim");
        bar.MouseLeftButtonUp += (_, e) => { e.Handled = true; Toggle(); };
        AutomationProperties.SetName(bar, "Show or hide");

        panel.Children.Add(bar);
        panel.Children.Add(_body);
        IsOpen = open;
        Apply(animate: false);
    }

    /// <summary>Makes the card foldable. <paramref name="changed"/> hears whether it is now open.</summary>
    public static CardFold Make(Border card, bool open, Func<string> describe, Action<bool> changed) =>
        new(card, open, describe, changed);

    public void Toggle()
    {
        IsOpen = !IsOpen;
        Apply(animate: true);
        _changed(IsOpen);
    }

    /// <summary>Reads the settings again for the summary shown while folded.</summary>
    public void Refresh()
    {
        if (!IsOpen) _summary.Text = _describe();
    }

    private void Apply(bool animate)
    {
        _body.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
        _summary.Text = IsOpen ? "" : _describe();
        double angle = IsOpen ? 90 : 0;
        if (!animate)
        {
            _turn.Angle = angle;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        if (IsOpen)
        {
            _body.Opacity = 0;
            _body.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        }
    }
}
