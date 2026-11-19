using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overskin;

/// <summary>
/// After recording steps: check each one, reword it or drop it, then make the guide as one
/// picture (for the editor), an HTML page, Markdown or a PDF.
/// </summary>
public sealed class StepsWindow : Window
{
    private readonly List<RecordedStep> _steps;
    private readonly Color _accent;
    private readonly string _folder;
    private readonly Action<BitmapSource, string> _openPicture;
    private readonly Action<string> _saved;
    private readonly StackPanel _list = new();
    private readonly TextBox _title;
    private readonly TextBlock _count = new();

    public IReadOnlyList<RecordedStep> Steps => _steps;
    internal string Heading => string.IsNullOrWhiteSpace(_title.Text) ? "Steps" : _title.Text.Trim();

    public StepsWindow(List<RecordedStep> steps, Color accent, string folder, Action<BitmapSource, string> openPicture, Action<string> saved)
    {
        _steps = steps;
        _accent = accent;
        _folder = folder;
        _openPicture = openPicture;
        _saved = saved;

        Title = "Steps";
        Width = 760;
        Height = 760;
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 12;
        SetResourceReference(ForegroundProperty, "Text");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // title bar
        var bar = new Grid { Height = 44, Background = Brushes.Transparent };
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        heading.Children.Add(new Icon { Data = (Geometry)FindResource("IcListOrdered"), Width = 16, Height = 16, Foreground = (Brush)FindResource("Accent") });
        heading.Children.Add(new TextBlock { Text = "Steps", FontSize = 13.5, FontWeight = FontWeights.Bold, Margin = new Thickness(9, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
        _count.Style = (Style)FindResource("Dim");
        _count.Margin = new Thickness(12, 1, 0, 0);
        _count.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(_count);
        bar.Children.Add(heading);
        var close = new Button { Style = (Style)FindResource("BtnCaptionClose"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Close",
            Content = new Icon { Data = (Geometry)FindResource("IcX"), Width = 14, Height = 14 } };
        close.Click += (_, _) => Close();
        bar.Children.Add(close);

        // the guide's title
        _title = new TextBox { Style = (Style)FindResource("TextField"), Text = "Steps", Width = 360, HorizontalAlignment = HorizontalAlignment.Right };
        var titleRow = new Grid { Height = 36, Margin = new Thickness(16, 0, 16, 6) };
        titleRow.Children.Add(new TextBlock { Text = "Title", Style = (Style)FindResource("Label") });
        titleRow.Children.Add(_title);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list, Padding = new Thickness(16, 4, 16, 8) };
        scroll.SetResourceReference(BackgroundProperty, "Stage");

        // actions
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(ActionButton("IcImage", "Picture", "One tall picture, opened in the editor", OpenPicture));
        actions.Children.Add(ActionButton("IcFileText", "HTML", "One web page with the pictures inside it", () => Export("HTML page|*.html", ".html", StepGuide.SaveHtml)));
        actions.Children.Add(ActionButton("IcFileText", "Markdown", "A .md file and a folder of pictures", () => Export("Markdown|*.md", ".md", StepGuide.SaveMarkdown)));
        var pdf = ActionButton("IcFileDown", "PDF", "One page per step", () => Export("PDF|*.pdf", ".pdf", StepGuide.SavePdf));
        pdf.Style = (Style)FindResource("BtnAccent");
        actions.Children.Add(pdf);
        var footer = new Border { Height = 52, Padding = new Thickness(14, 0, 14, 0), BorderThickness = new Thickness(0, 1, 0, 0), Child = actions };
        footer.SetResourceReference(Border.BorderBrushProperty, "Line");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(bar);
        Grid.SetRow(titleRow, 1);
        root.Children.Add(titleRow);
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        var frame = new Border { BorderThickness = new Thickness(1), Child = root, ClipToBounds = true };
        frame.SetResourceReference(Border.BackgroundProperty, "Bg");
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        frame.SetResourceReference(Border.CornerRadiusProperty, "RadiusWindow");
        Content = frame;

        Fill();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int round = 2;
        NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
    }

    private Button ActionButton(string icon, string text, string tip, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Icon { Data = (Geometry)FindResource(icon), Width = 13, Height = 13, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = text });
        var b = new Button { Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip, Content = content };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>One card per step: number, picture with the click marked, and the sentence to edit.</summary>
    private void Fill()
    {
        _list.Children.Clear();
        _count.Text = $"{_steps.Count} step{(_steps.Count == 1 ? "" : "s")}";
        for (int i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i];
            var card = new Border { Style = (Style)FindResource("Card") };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(_accent), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = (i + 1).ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            grid.Children.Add(badge);

            var picture = new Image { Source = StepTutorial.Marked(step, _accent), Stretch = Stretch.Uniform, MaxHeight = 150, HorizontalAlignment = HorizontalAlignment.Left };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            Grid.SetColumn(picture, 1);
            grid.Children.Add(picture);

            var text = new TextBox { Style = (Style)FindResource("TextField"), Text = StepTutorial.Caption(step), TextWrapping = TextWrapping.Wrap, AcceptsReturn = false,
                Height = double.NaN, MinHeight = 54, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            text.TextChanged += (_, _) => step.Text = text.Text;
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);

            var remove = new Button { Style = (Style)FindResource("BtnIcon"), Width = 28, Height = 26, ToolTip = "Leave this step out", VerticalAlignment = VerticalAlignment.Top,
                Content = new Icon { Data = (Geometry)FindResource("IcTrash"), Width = 13, Height = 13 } };
            remove.Click += (_, _) =>
            {
                _steps.Remove(step);
                Fill();
            };
            Grid.SetColumn(remove, 3);
            grid.Children.Add(remove);

            card.Child = grid;
            _list.Children.Add(card);
        }
    }

    private bool HaveSteps()
    {
        if (_steps.Count > 0) return true;
        Toast.Show("There are no steps left.");
        return false;
    }

    private void OpenPicture()
    {
        if (!HaveSteps()) return;
        _openPicture(ImageLoader.Normalize(StepTutorial.Build(_steps, _accent, Heading)), Heading);
    }

    private void Export(string filter, string extension, Action<IReadOnlyList<RecordedStep>, string, Color, string> save)
    {
        if (!HaveSteps()) return;
        string name = string.Concat(Heading.Split(Path.GetInvalidFileNameChars())).Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the guide",
            Filter = filter,
            FileName = (name.Length == 0 ? "Steps" : name) + extension,
            InitialDirectory = Directory.Exists(_folder) ? _folder : null,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Cursor = Cursors.Wait;
            save(_steps, Heading, _accent, dialog.FileName);
            Toast.Show("Saved " + Path.GetFileName(dialog.FileName));
            _saved(dialog.FileName);
        }
        catch (Exception ex)
        {
            Toast.Show("Couldn't save the guide: " + ex.Message, true);
        }
        finally
        {
            Cursor = null;
        }
    }
}
