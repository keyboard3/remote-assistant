using System.Windows;

namespace LunaDesktopHelper;

public partial class ActionSettingsWindow : Window
{
    public ActionSettings? SavedSettings { get; private set; }
    public string? SaveNotice { get; private set; }

    public ActionSettingsWindow(ActionSettings initialSettings, string? initialError)
    {
        InitializeComponent();
        TitleInput.Text = initialSettings.Title;
        HotkeyInput.Text = initialSettings.Hotkey;
        ErrorText.Text = initialError ?? "修饰键：Ctrl、Alt、Shift、Win。主键：A–Z、0–9、F1–F24、Space、Enter、Tab、Escape、Backspace、Delete、Insert、Home、End、PageUp/Down、方向键或 PrintScreen。";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SavedSettings = ActionSettingsStore.Save(TitleInput.Text, HotkeyInput.Text, out var backupPath);
            SaveNotice = backupPath is null ? "设置已保存。" : $"原设置文件无法读取，已备份到：{backupPath}";
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
