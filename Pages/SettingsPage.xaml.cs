using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GameOp.Services;
using Microsoft.Win32;

namespace GameOp.Pages;

public partial class SettingsPage : Page
{
    private readonly AppState _s = AppState.Instance;
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        AutoPower.IsChecked = _s.Settings.AutoPowerPerGame;
        Gamepad.IsChecked = _s.Settings.GamepadNavigation;
        AutoUpdate.IsChecked = _s.Settings.AutoUpdateEmulators;
        Prerelease.IsChecked = _s.Settings.IncludePrereleases;
        InstallRoot.Text = _s.Settings.EmulatorInstallRoot;
        RyzenAdj.Text = _s.Settings.RyzenAdjPath ?? "";
        DbUrl.Text = _s.Settings.DatabaseUrl ?? "";
        HwText.Text = $"Hardware: {_s.HardwareModel} · {_s.HardwareCpu}";

        DevicePick.Items.Add(new ComboBoxItem { Content = "Auto-detect", Tag = null });
        foreach (var d in _s.Db.Devices) DevicePick.Items.Add(new ComboBoxItem { Content = $"{d.Name} ({d.Apu})", Tag = d.Id });
        DevicePick.SelectedItem = DevicePick.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == _s.Settings.ForcedDeviceId) ?? DevicePick.Items[0];
        UpdateDbText();
        _loading = false;
    }

    private void UpdateDbText() =>
        DbText.Text = $"Version {_s.Db.Version} ({_s.Db.Source}) · {_s.Db.Games.Count} games · " +
                      string.Join(", ", _s.Db.Games.GroupBy(g => g.Emulator).Select(g => $"{g.Key} {g.Count()}"));

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        _s.Settings.AutoPowerPerGame = AutoPower.IsChecked == true;
        _s.Settings.GamepadNavigation = Gamepad.IsChecked == true;
        _s.Settings.AutoUpdateEmulators = AutoUpdate.IsChecked == true;
        _s.Settings.IncludePrereleases = Prerelease.IsChecked == true;
        _s.SaveSettings();
    }

    private void OnInstallRoot(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Where should GameOp install new emulators?" };
        if (dlg.ShowDialog() != true) return;
        _s.Settings.EmulatorInstallRoot = InstallRoot.Text = dlg.FolderName;
        _s.SaveSettings();
    }

    private void OnDevice(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _s.Settings.ForcedDeviceId = (DevicePick.SelectedItem as ComboBoxItem)?.Tag as string;
        _s.SaveSettings();
        Log.Info("Device model changed; restart GameOp to apply.");
    }

    private void OnRyzenAdj(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "ryzenadj.exe|ryzenadj.exe" };
        if (dlg.ShowDialog() != true) return;
        _s.Settings.RyzenAdjPath = RyzenAdj.Text = dlg.FileName;
        _s.SaveSettings();
    }

    private void OnClearRyzenAdj(object sender, RoutedEventArgs e)
    {
        _s.Settings.RyzenAdjPath = null;
        RyzenAdj.Text = "";
        _s.SaveSettings();
    }

    private async void OnUpdateDb(object sender, RoutedEventArgs e)
    {
        var url = DbUrl.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        {
            Log.Warn("Enter an https:// URL for the database.");
            return;
        }
        _s.Settings.DatabaseUrl = url;
        _s.SaveSettings();
        try
        {
            var n = await _s.Db.UpdateFromUrlAsync(url);
            Log.Ok($"Database updated: {n} games from {u.Host}");
        }
        catch (Exception ex) { Log.Warn($"Database update failed: {ex.Message}"); }
        UpdateDbText();
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Game database (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var n = _s.Db.ImportFile(dlg.FileName);
            Log.Ok($"Imported {n} games from {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex) { Log.Warn($"Import failed: {ex.Message}"); }
        UpdateDbText();
    }

    private void OnResetDb(object sender, RoutedEventArgs e)
    {
        _s.Db.ResetToBundled();
        Log.Info("Database reset to the built-in version.");
        UpdateDbText();
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.UserDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.UserDir}\"") { UseShellExecute = true });
    }
}
