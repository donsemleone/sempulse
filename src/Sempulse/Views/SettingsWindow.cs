using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Sempulse.Controls;
using Sempulse.Services;

namespace Sempulse.Views;

// Panel settings. Every change is applied and saved immediately.
internal sealed class SettingsWindow : Window
{
    readonly Func<PanelSettings> current;
    readonly Action<Func<PanelSettings, PanelSettings>> change;
    readonly Action quit;
    readonly StackPanel content = new();

    public SettingsWindow(Func<PanelSettings> current, Action<Func<PanelSettings, PanelSettings>> change, Action quit)
    {
        this.current = current;
        this.change = change;
        this.quit = quit;
        Title = "Sempulse ayarları";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 320;
        SizeToContent = SizeToContent.Height;
        FontFamily = Ui.Font;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;

        var close = Ui.IconButton("X", 16, "Kapat", Hide, 0);
        close.Style = (Style)FindResource("SettingsToggle");
        close.Padding = new Thickness(0);
        var title = Ui.Text("Panel ayarları", 14, Ui.Primary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 15) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(content);
        Content = new Border
        {
            Background = Ui.Frozen(Color.FromArgb(0xF7, 30, 30, 35)),
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(17),
            Child = root,
        };
        Rebuild();
    }

    // Rows depend on the layout (edge and thickness only make sense for the bar).
    public void Rebuild()
    {
        var settings = current();
        content.Children.Clear();
        content.Children.Add(Choice("Yerleşim", settings.Mode,
            [(PanelMode.Widget, "Floating panel"), (PanelMode.Bar, "Edge bar")], value => s => s with { Mode = value }));
        if (settings.Mode == PanelMode.Bar)
            content.Children.Add(Choice("Kenar", settings.Position,
                [(BarEdge.Top, "Üst"), (BarEdge.Bottom, "Alt"), (BarEdge.Left, "Sol"), (BarEdge.Right, "Sağ")], value => s => s with { Position = value }));
        content.Children.Add(Choice("Medya kontrolü", settings.MediaMode,
            [(MediaLayout.Separate, "Spotify ve diğer medya ayrı"), (MediaLayout.Unified, "Tek aktif medya")], value => s => s with { MediaMode = value }));
        content.Children.Add(Choice("Masaüstü kedisi", settings.Pet, [(true, "Açık"), (false, "Kapalı")], value => s => s with { Pet = value }));
        if (settings.Mode == PanelMode.Bar)
            content.Children.Add(Range("Bar kalınlığı", settings.Thickness, 40, 120, " px", value => s => s with { Thickness = value }));
        content.Children.Add(Range("Floating genişliği", settings.WidgetWidth, 520, 900, " px", value => s => s with { WidgetWidth = value }));
        content.Children.Add(Range("Opaklık", settings.Opacity, 0, 100, "%", value => s => s with { Opacity = value }));

        var quitButton = new Button
        {
            Content = "Uygulamayı kapat",
            Cursor = Cursors.Hand,
            Focusable = false,
            Template = QuitTemplate(),
        };
        quitButton.Click += (_, _) => quit();
        content.Children.Add(quitButton);
    }

    static TextBlock Label(string text)
    {
        var label = Ui.Text(text.ToUpper(CultureInfo.GetCultureInfo("tr-TR")), 11, Ui.Secondary);
        label.Style = (Style)Application.Current.FindResource("SettingLabel");
        return label;
    }

    UIElement Choice<T>(string label, T selected, (T Value, string Text)[] options, Func<T, Func<PanelSettings, PanelSettings>> apply)
    {
        var combo = new ComboBox { Style = (Style)FindResource("DarkCombo") };
        foreach (var (value, text) in options)
        {
            var item = new ComboBoxItem { Content = text, Tag = value, FontFamily = Ui.Font, FontSize = 12 };
            combo.Items.Add(item);
            if (Equals(value, selected)) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T value } && !Equals(value, selected))
                Dispatcher.BeginInvoke(() => change(apply(value)));
        };
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        group.Children.Add(Label(label));
        group.Children.Add(combo);
        return group;
    }

    UIElement Range(string label, int value, int min, int max, string unit, Func<int, Func<PanelSettings, PanelSettings>> apply)
    {
        var caption = Label($"{label} ({value}{unit})");
        var slider = new Slider
        {
            Style = (Style)FindResource("AccentSlider"),
            Minimum = min, Maximum = max, Value = value,
            SmallChange = 1, LargeChange = 5, IsSnapToTickEnabled = true, TickFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var number = new TextBox
        {
            Style = (Style)FindResource("DarkNumber"),
            Text = value.ToString(CultureInfo.InvariantCulture),
            Margin = new Thickness(9, 0, 0, 0),
        };
        int committed = value;
        void Show(int draft)
        {
            caption.Text = $"{label} ({draft}{unit})".ToUpper(CultureInfo.GetCultureInfo("tr-TR"));
            number.Text = draft.ToString(CultureInfo.InvariantCulture);
        }
        void Commit(int draft)
        {
            int next = Math.Clamp(draft, min, max);
            Show(next);
            slider.Value = next;
            if (next == committed) return;
            committed = next;
            change(apply(next));
        }
        // Dragging previews the number; the panel changes when the slider is released, as before.
        slider.ValueChanged += (_, args) => Show((int)args.NewValue);
        slider.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(() => Commit((int)slider.Value));
        slider.LostMouseCapture += (_, _) => Dispatcher.BeginInvoke(() => Commit((int)slider.Value));
        number.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            Commit(int.TryParse(number.Text, out int typed) ? typed : min);
            Keyboard.ClearFocus();
        };
        number.LostKeyboardFocus += (_, _) => Commit(int.TryParse(number.Text, out int typed) ? typed : min);

        var row = new DockPanel();
        DockPanel.SetDock(number, Dock.Right);
        row.Children.Add(number);
        row.Children.Add(slider);
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        group.Children.Add(caption);
        group.Children.Add(row);
        return group;
    }

    static ControlTemplate QuitTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Ui.Frozen(Color.FromArgb(0x24, 0xEF, 0x44, 0x44)));
        border.SetValue(Border.BorderBrushProperty, Ui.Frozen(Color.FromArgb(0x4D, 0xEF, 0x44, 0x44)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.PaddingProperty, new Thickness(8));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextProperty, "Uygulamayı kapat");
        text.SetValue(TextBlock.ForegroundProperty, Ui.Frozen(Color.FromRgb(0xFF, 0x89, 0x89)));
        text.SetValue(TextBlock.FontSizeProperty, 13.0);
        text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(text);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }
}
