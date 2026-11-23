using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Reap.Core;
using Windows.UI;
using Windows.UI.Text;

namespace Reap.App;

/// <summary>代码搭界面用的小工具：颜色、文字、卡片、徽标。</summary>
public static class UI
{
    public static readonly Color Accent = ColorHelper.FromArgb(255, 97, 102, 245);
    public static readonly Color Danger = ColorHelper.FromArgb(255, 232, 76, 76);
    public static readonly Color Busy = ColorHelper.FromArgb(255, 51, 184, 115);
    public static readonly Color Idle = ColorHelper.FromArgb(255, 245, 158, 46);

    public static Color ColorOf(AgentKind agent) => agent switch
    {
        AgentKind.Claude => ColorHelper.FromArgb(255, 217, 120, 87),
        AgentKind.Codex => ColorHelper.FromArgb(255, 18, 158, 128),
        _ => ColorHelper.FromArgb(255, 128, 107, 242),
    };

    public static SolidColorBrush Brush(Color c, double opacity = 1) =>
        new(Color.FromArgb((byte)(c.A * opacity), c.R, c.G, c.B));

    /// <summary>系统主题画刷，跟随亮色/暗色。</summary>
    public static Brush Theme(string key) => (Brush)Application.Current.Resources[key];

    public static Brush Secondary => Theme("TextFillColorSecondaryBrush");

    public static TextBlock Text(string text, double size = 14, FontWeight? weight = null, Brush? color = null,
                                 bool mono = false, bool wrap = false)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (color is not null) t.Foreground = color;
        if (mono) t.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        return t;
    }

    public static FontIcon Icon(string glyph, double size = 14, Brush? color = null)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = size };
        if (color is not null) icon.Foreground = color;
        return icon;
    }

    public static StackPanel Row(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    public static StackPanel Column(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    public static Border Card(UIElement child, double padding = 16) => new()
    {
        Child = child,
        Padding = new Thickness(padding),
        CornerRadius = new CornerRadius(8),
        Background = Theme("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Theme("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
    };

    public static Border Pill(string text, Color color, string? glyph = null)
    {
        var row = Row(4);
        if (glyph is not null) row.Children.Add(Icon(glyph, 10, Brush(color)));
        row.Children.Add(Text(text, 12, FontWeights.SemiBold, Brush(color)));
        return new Border
        {
            Child = row,
            Padding = new Thickness(8, 2, 8, 3),
            CornerRadius = new CornerRadius(10),
            Background = Brush(color, 0.15),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static Border AgentBadge(AgentKind agent, bool compact = false)
    {
        var text = compact ? agent.ShortName()[..1] : agent.ShortName() + (agent.Rule().Experimental ? " 实验" : "");
        var badge = Pill(text, ColorOf(agent));
        ToolTipService.SetToolTip(badge, agent.DisplayName());
        return badge;
    }

    public static Grid StatCard(string title, string value, string glyph, Color tint)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(8), Background = Brush(tint, 0.15),
            Child = new FontIcon { Glyph = glyph, FontSize = 18, Foreground = Brush(tint) },
        };
        var text = Column(0, Text(value, 22, FontWeights.SemiBold), Text(title, 12, color: Secondary));
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>一排等宽的统计卡片。</summary>
    public static Grid StatRow(params Grid[] cards)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        for (var i = 0; i < cards.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var card = Card(cards[i], 14);
            Grid.SetColumn(card, i);
            grid.Children.Add(card);
        }
        return grid;
    }

    public static Button Button(string text, Action onClick, bool accent = false, bool danger = false, string? glyph = null)
    {
        object content = glyph is null ? text : Row(6, Icon(glyph, 12), Text(text, 14));
        var button = new Button { Content = content, VerticalAlignment = VerticalAlignment.Center };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        if (danger) button.Foreground = Brush(Danger);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Button IconButton(string glyph, string tip, Action onClick, Color? color = null)
    {
        var button = new Button
        {
            Content = Icon(glyph, 14, color is { } c ? Brush(c) : null),
            Padding = new Thickness(8, 6, 8, 6),
            Background = Brush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static UIElement Empty(string glyph, string title, string message, Color tint)
    {
        var panel = Column(10,
            Icon(glyph, 44, Brush(tint)),
            Text(title, 20, FontWeights.SemiBold),
            Text(message, 14, color: Secondary, wrap: true));
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.MaxWidth = 420;
        panel.Margin = new Thickness(0, 48, 0, 48);
        foreach (var child in panel.Children)
            if (child is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)panel.Children[2]).TextAlignment = TextAlignment.Center;
        return panel;
    }

    public static async Task<bool> Confirm(XamlRoot root, string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = Text(message, 14, wrap: true),
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static void Copy(string text)
    {
        var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    public static void Open(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    public static class Glyph
    {
        public const string Refresh = "";
        public const string Cancel = "";
        public const string Settings = "";
        public const string Warning = "";
        public const string Chat = "";
        public const string Drive = "";
        public const string Copy = "";
        public const string Folder = "";
        public const string Globe = "";
        public const string Check = "";
        public const string Moon = "";
        public const string Memory = "";
        public const string Leaf = "";
        public const string Branch = "";
        public const string Box = "";
        public const string ChevronRight = "";
        public const string Lock = "";
        public const string Pulse = "";
    }
}
