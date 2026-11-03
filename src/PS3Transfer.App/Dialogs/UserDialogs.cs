using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace PS3Transfer.App.Dialogs;

public interface IUserDialogs
{
    bool Confirm(string title, string message);
    void Tell(string title, string message);
    string[]? PickFiles();
    string? PickFolder();
    string? PickPart();
    string? PickSave(string suggestedFileName);
}

public sealed class WindowDialogs : IUserDialogs
{
    private readonly Window _owner;

    public WindowDialogs(Window owner) => _owner = owner;

    public bool Confirm(string title, string message) => Prompt.Show(_owner, title, message, "Continue", "Cancel");

    public void Tell(string title, string message) => Prompt.Show(_owner, title, message, "OK", null);

    public string[]? PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add ISOs, PKGs, or package parts",
            Filter = "PS3 and ROM files|*.iso;*.pkg;*.rap;*.edat;*.cue;*.bin;*.mdf;*.mds;*.img;*.nes;*.fds;*.sfc;*.smc;*.gb;*.gbc;*.gba;*.md;*.gen;*.smd;*.sms;*.gg;*.32x;*.pce;*.ngp;*.ngc;*.a26;*.a78;*.lnx;*.n64;*.z64;*.v64;*.nds;*.zip;*.7z;*.wad;*.adf|All files|*.*",
            Multiselect = true,
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileNames : null;
    }

    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Add a folder of games or an extracted package" };
        return dialog.ShowDialog(_owner) == true ? dialog.FolderName : null;
    }

    public string? PickPart()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose one piece of the split set",
            Filter = "Split pieces|*.0;*.66600;*.001|All files|*.*",
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public string? PickSave(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the joined file",
            FileName = suggestedFileName,
            Filter = "All files|*.*",
            OverwritePrompt = false,
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }
}

internal static class Prompt
{
    public static bool Show(Window? owner, string title, string message, string accept, string? cancel)
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = Brush(0x00, 0x00, 0x00),
            Foreground = Brush(0xE7, 0xE9, 0xEA),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            ShowInTaskbar = false,
        };

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(new TextBlock
        {
            Text = message,
            Margin = new Thickness(0, 12, 0, 18),
            Foreground = Brush(0x71, 0x76, 0x7B),
            TextWrapping = TextWrapping.Wrap,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (cancel is not null)
        {
            var cancelButton = MakeButton(cancel, primary: false);
            cancelButton.Click += (_, _) => { window.DialogResult = false; };
            buttons.Children.Add(cancelButton);
        }

        var acceptButton = MakeButton(accept, primary: true);
        acceptButton.Margin = new Thickness(8, 0, 0, 0);
        acceptButton.IsDefault = true;
        acceptButton.Click += (_, _) => { window.DialogResult = true; };
        buttons.Children.Add(acceptButton);
        root.Children.Add(buttons);
        window.Content = new Border
        {
            Background = Brush(0x00, 0x00, 0x00),
            BorderBrush = Brush(0x2F, 0x33, 0x36),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Child = root,
        };
        return window.ShowDialog() == true;
    }

    private static Button MakeButton(string text, bool primary)
    {
        var background = primary ? Brush(0xFF, 0xFF, 0xFF) : Brush(0x00, 0x00, 0x00);
        var border = primary ? Brush(0xFF, 0xFF, 0xFF) : Brush(0x2F, 0x33, 0x36);
        var foreground = primary ? Brush(0x00, 0x00, 0x00) : Brush(0xE7, 0xE9, 0xEA);
        var shell = new System.Windows.FrameworkElementFactory(typeof(Border));
        shell.SetValue(Border.BackgroundProperty, background);
        shell.SetValue(Border.BorderBrushProperty, border);
        shell.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(0));
        shell.SetValue(Border.PaddingProperty, new Thickness(18, 8, 18, 8));
        var label = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        shell.AppendChild(label);
        return new Button
        {
            Content = text,
            Foreground = foreground,
            FontWeight = FontWeights.Bold,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = new System.Windows.Controls.ControlTemplate(typeof(Button)) { VisualTree = shell },
        };
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
