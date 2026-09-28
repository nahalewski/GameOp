using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using GameOp.Models;
using GameOp.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace GameOp.Pages;

public partial class EmulatorsPage : Page
{
    private readonly AppState _s = AppState.Instance;

    public EmulatorsPage()
    {
        InitializeComponent();
        BulkTier.Items.Add(new ComboBoxItem { Content = "Each game's own level", Tag = null });
        foreach (var t in TierInfo.All) BulkTier.Items.Add(new ComboBoxItem { Content = t.Label(), Tag = t });
        BulkTier.SelectedIndex = 0;
        _s.EmulatorsChanged += Build;
        _s.UpdatesChanged += Build;
        Unloaded += (_, _) => { _s.EmulatorsChanged -= Build; _s.UpdatesChanged -= Build; };
        Build();
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        await _s.CheckUpdatesAsync(autoInstall: false);
        var available = _s.Updates.Count(u => u.Value.State == UpdateState.Available);
        await Notify(available == 0 ? "All your emulators are up to date." : $"{available} update(s) available. Use Update now on each card, or turn on automatic updates in Settings.");
    }

    private async void RunUpdate(EmulatorAdapter e)
    {
        try { await Notify(await _s.InstallOrUpdateAsync(e)); }
        catch (Exception ex) { await Notify($"{e.Name}: {ex.Message}"); }
    }

    private async void Rollback(EmulatorAdapter e)
    {
        var msg = EmulatorUpdater.Rollback(e);
        _s.Settings.InstalledVersions.Remove(e.Id);
        _s.SaveSettings();
        Log.Info($"{e.Name}: {msg}");
        await Notify(msg);
    }

    private Tier? Bulk => (BulkTier.SelectedItem as ComboBoxItem)?.Tag as Tier?;

    private void Build() => Dispatcher.BeginInvoke(() =>
    {
        Cards.Children.Clear();
        foreach (var e in _s.Emulators.OrderByDescending(e => e.IsDetected)) Cards.Children.Add(BuildCard(e));
    });

    private UIElement BuildCard(EmulatorAdapter e)
    {
        var games = _s.Db.GamesFor(e.DbKey).Count();
        var installed = _s.InstalledIds.TryGetValue(e.Id, out var ids) ? ids.Count : 0;
        var def = _s.Db.Emulator(e.DbKey);

        var info = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = e.Name, FontSize = 18, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock
        {
            Text = e.IsDetected ? "  ● Ready" : e.ExePath is not null ? "  ● Run it once to create configs" : "  ○ Not found",
            Foreground = (System.Windows.Media.Brush)FindResource(e.IsDetected ? "SystemFillColorSuccessBrush" : "TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 2, 0, 0)
        });
        info.Children.Add(title);
        info.Children.Add(Caption($"{def?.System ?? ""} · {games} games in database" +
                                  (installed > 0 ? $" · {installed} found in your library" : "") +
                                  (e.SupportsPerGame ? " · per-game profiles" : " · global settings only")));
        if (e.ExePath is not null) info.Children.Add(Caption($"App: {e.ExePath}"));
        if (e.ConfigRoot is not null) info.Children.Add(Caption($"Config: {e.ConfigRoot}"));

        var supported = EmulatorUpdater.IsSupported(e.Id);
        var update = _s.Updates.GetValueOrDefault(e.Id);
        if (update is not null)
        {
            var line = Caption("⟳ " + update.Message);
            if (update.State == UpdateState.Available)
                line.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorCautionBrush");
            info.Children.Add(line);
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (supported && e.ExePath is null)
            buttons.Children.Add(Btn($"Install to {_s.Settings.EmulatorInstallRoot}", SymbolRegular.ArrowDownload24, () => RunUpdate(e), primary: true));
        if (supported && update?.State == UpdateState.Available)
            buttons.Children.Add(Btn($"Update now ({update.Info?.Version})", SymbolRegular.ArrowDownload24, () => RunUpdate(e), primary: true));
        buttons.Children.Add(Btn(e.ExePath is null ? "Locate .exe" : "Change .exe", SymbolRegular.FolderOpen24, () => Locate(e)));
        if (e.IsDetected)
        {
            buttons.Children.Add(Btn("Apply level as default", SymbolRegular.Options24, () => ApplyDefaults(e), primary: true));
            if (e.SupportsPerGame)
                buttons.Children.Add(Btn($"Install all {games} game profiles", SymbolRegular.AppsList24, () => InstallAll(e)));
            buttons.Children.Add(Btn("Open config folder", SymbolRegular.Folder24, () =>
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{e.ConfigRoot}\"") { UseShellExecute = true })));
            buttons.Children.Add(Btn("Restore originals", SymbolRegular.ArrowUndo24, () => Restore(e)));
        }
        if (supported && e.ExePath is not null)
            buttons.Children.Add(Btn("Roll back update", SymbolRegular.History24, () => Rollback(e)));
        info.Children.Add(buttons);

        return new Card { Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 10), Content = info };
    }

    private static TextBlock Caption(string text) =>
        new() { Text = text, Style = (Style)Application.Current.FindResource("Caption"), Margin = new Thickness(0, 2, 0, 0) };

    private static Button Btn(string text, SymbolRegular icon, Action onClick, bool primary = false)
    {
        var b = new Button
        {
            Content = text, Icon = new SymbolIcon { Symbol = icon }, Margin = new Thickness(0, 0, 8, 8),
            Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private async void Locate(EmulatorAdapter e)
    {
        var dlg = new OpenFileDialog
        {
            Title = $"Locate {e.Name}",
            Filter = $"{e.Name}|{string.Join(";", e.ExeNames)};*.exe|All programs|*.exe"
        };
        if (dlg.ShowDialog() != true) return;
        _s.Settings.EmulatorPaths[e.Id] = dlg.FileName;
        _s.SaveSettings();
        await _s.DetectEmulatorsAsync();
    }

    private async void ApplyDefaults(EmulatorAdapter e)
    {
        var tier = Bulk ?? _s.CurrentTier;
        await Task.Run(() => _s.ApplyGlobal(e, tier));
        await Notify($"{e.Name}: {tier.Label()} set as the default. Check the Dashboard activity log for details.");
    }

    private async void InstallAll(EmulatorAdapter e)
    {
        var bulk = Bulk;
        var (games, files) = await Task.Run(() => _s.InstallAllProfiles(e, bulk));
        var note = e.Id == "pcsx2" ? "\n\nPCSX2 profiles need the game's .iso in a PCSX2 game folder, so games that weren't found were skipped." : "";
        await Notify($"Wrote {files} profile file(s) for {games} game(s) at {(bulk?.Label() ?? "each game's own level")}.{note}");
    }

    private async void Restore(EmulatorAdapter e)
    {
        var confirm = await new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Restore {e.Name}?",
            Content = "Puts back the original config files from before GameOp changed them and deletes the per-game profiles GameOp created.",
            PrimaryButtonText = "Restore", CloseButtonText = "Cancel"
        }.ShowDialogAsync();
        if (confirm != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        var n = Backup.RestoreOriginals(e.ConfigRoot!);
        Log.Ok($"{e.Name}: restored {n} file(s)");
        await Notify($"Restored {n} file(s).");
    }

    private async void OnAddRoot(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder that contains your emulators" };
        if (dlg.ShowDialog() != true) return;
        if (!_s.Settings.ExtraSearchRoots.Contains(dlg.FolderName)) _s.Settings.ExtraSearchRoots.Add(dlg.FolderName);
        _s.SaveSettings();
        await _s.DetectEmulatorsAsync();
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await _s.DetectEmulatorsAsync();

    private static Task Notify(string text) =>
        new Wpf.Ui.Controls.MessageBox { Title = "GameOp", Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK" }.ShowDialogAsync();
}
