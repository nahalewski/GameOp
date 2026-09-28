using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using GameOp.Models;
using GameOp.Services;

namespace GameOp.Pages;

public partial class LibraryPage : Page
{
    public sealed record Row(GameEntry Game, string Title, string Subtitle, string Badge);
    public sealed record SettingLine(string Path, string Value);

    private readonly AppState _s = AppState.Instance;
    private GameEntry? _game;
    private bool _loading;

    public LibraryPage()
    {
        InitializeComponent();
        EmuFilter.Items.Add("All emulators");
        foreach (var e in _s.Db.Emulators) EmuFilter.Items.Add(new ComboBoxItem { Content = e.Name, Tag = e.Id });
        EmuFilter.SelectedIndex = 0;
        foreach (var t in TierInfo.All) TierPick.Items.Add(new ComboBoxItem { Content = t.Label(), Tag = t });
        _s.EmulatorsChanged += Refresh;
        Unloaded += (_, _) => _s.EmulatorsChanged -= Refresh;
        Refresh();
    }

    private void Refresh() => Dispatcher.BeginInvoke(() =>
    {
        DbInfo.Text = $"{_s.Db.Games.Count} games in the database (v{_s.Db.Version}, {_s.Db.Source}). " +
                      $"Profiles are tuned for {_s.Device.Name}.";
        OnFilter(this, new RoutedEventArgs());
    });

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        if (GameList is null) return;
        var q = Search.Text?.Trim() ?? "";
        var emu = (EmuFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var rows = _s.Db.Games
            .Where(g => emu is null || g.Emulator == emu)
            .Where(g => q.Length == 0 || g.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        g.Ids.Any(id => id.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Where(g => InstalledOnly.IsChecked != true || _s.IsInstalled(g))
            .Select(g => new Row(g, g.Title,
                $"{_s.Db.Emulator(g.Emulator)?.Name ?? g.Emulator} · {g.Demand} · {_s.TierForGame(g).Label()}",
                _s.IsInstalled(g) ? "✓ In library" : g.HasOverrides ? "Tuned" : ""))
            .ToList();
        GameList.ItemsSource = rows;
    }

    private void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        if (GameList.SelectedItem is not Row row) return;
        _game = row.Game;
        _loading = true;
        Details.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Collapsed;

        var g = row.Game;
        DTitle.Text = g.Title;
        DMeta.Text = $"{_s.Db.Emulator(g.Emulator)?.System} · {g.IdList}";
        DCompat.Text = $"Compatibility: {g.Compatibility ?? "unknown"}   ·   Demand: {g.Demand}   ·   Recommended: {g.Recommended.Label()}";
        DNotes.Text = g.Notes ?? "";
        DSource.NavigateUri = Uri.TryCreate(g.Source, UriKind.Absolute, out var u) ? u : null;
        DSource.IsEnabled = DSource.NavigateUri is not null;

        TierPick.SelectedIndex = (int)_s.TierForGame(g);

        TargetPick.Items.Clear();
        foreach (var a in _s.Emulators.Where(a => a.DbKey == g.Emulator))
            TargetPick.Items.Add(new ComboBoxItem { Content = a.IsDetected ? a.Name : $"{a.Name} (not found)", Tag = a });
        TargetPick.SelectedIndex = Math.Max(0, TargetPick.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => ((EmulatorAdapter)i.Tag).IsDetected));
        _loading = false;

        UpdatePreview();
        UpdatePack();
        UpdatePatches();
        if (g.Emulator == "rpcs3") _ = LoadRpcs3CompatAsync(g);
    }

    private void UpdatePack()
    {
        var pack = _game is null ? null : _s.PacksFor(_game).FirstOrDefault();
        PackRow.Visibility = pack is null ? Visibility.Collapsed : Visibility.Visible;
        if (pack is null) return;
        PackText.Text = _s.IsPackInstalled(pack)
            ? $"HD texture pack installed: {pack.Name}"
            : $"HD texture pack available: {pack.Name} ({pack.SizeText})";
        PackBtn.Content = _s.IsPackInstalled(pack) ? "Reinstall" : "Download HD textures";
        PackBtn.IsEnabled = _s.AdapterFor(pack.Emulator) is not null;
        PackBtn.Tag = pack;
    }

    /// <summary>Xenia Canary: list this game's community patches with on/off switches.</summary>
    private void UpdatePatches()
    {
        PatchHost.Children.Clear();
        PatchSection.Visibility = Visibility.Collapsed;
        if (_game?.Emulator != "xenia" || _s.AdapterFor("xenia")?.ConfigRoot is not { } root) return;
        var dir = System.IO.Path.Combine(root, "patches");
        if (!System.IO.Directory.Exists(dir)) return;

        var files = System.IO.Directory.EnumerateFiles(dir, "*.patch.toml")
            .Where(f => _game.Ids.Any(id => System.IO.Path.GetFileName(f).StartsWith(id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var file in files)
        {
            PatchHost.Children.Add(new TextBlock { Text = System.IO.Path.GetFileNameWithoutExtension(file).Replace(".patch", ""), FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 2) });
            foreach (var p in XeniaPatches.Patches(System.IO.File.ReadAllLines(file)).ToList())
            {
                var sw = new Wpf.Ui.Controls.ToggleSwitch { Content = p.Name, IsChecked = p.Enabled, Margin = new Thickness(0, 2, 0, 2) };
                var name = p.Name;
                sw.Click += (_, _) =>
                {
                    var lines = System.IO.File.ReadAllLines(file);
                    System.IO.File.WriteAllLines(file, XeniaPatches.SetEnabled(lines, new HashSet<string> { name }, sw.IsChecked == true));
                    Log.Ok($"Xenia patch '{name}' {(sw.IsChecked == true ? "on" : "off")}");
                };
                PatchHost.Children.Add(sw);
            }
        }
        if (PatchHost.Children.Count > 0) PatchSection.Visibility = Visibility.Visible;
    }

    private async void OnPack(object sender, RoutedEventArgs e)
    {
        if (PackBtn.Tag is not TexturePack pack) return;
        PackBtn.IsEnabled = false;
        try { ShowMessage("HD textures", await _s.InstallPackAsync(pack, _game)); }
        catch (Exception ex) { ShowMessage("HD textures", ex.Message); }
        finally { UpdatePack(); }
    }

    private async Task LoadRpcs3CompatAsync(GameEntry g)
    {
        var status = await GameDatabase.Rpcs3CompatAsync(g.Ids[0]);
        if (status is not null && _game == g)
            DCompat.Text = $"RPCS3 live status: {status}   ·   Demand: {g.Demand}   ·   Recommended: {g.Recommended.Label()}";
    }

    private Tier SelectedTier => (TierPick.SelectedItem as ComboBoxItem)?.Tag is Tier t ? t : Tier.Balanced;
    private EmulatorAdapter? Target => (TargetPick.SelectedItem as ComboBoxItem)?.Tag as EmulatorAdapter;

    private void OnTierPick(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _game is null) return;
        _s.SetTierForGame(_game, SelectedTier);
        UpdatePreview();
    }

    private void OnTargetPick(object sender, SelectionChangedEventArgs e) { if (!_loading) UpdatePreview(); }

    private void UpdatePreview()
    {
        if (_game is null) return;
        var tier = SelectedTier;
        var plan = PowerService.PlanFor(_s.Device, tier);
        DPower.Text = $"Power while playing: {plan.Spl} W sustained, {plan.Fppt} W boost (auto-applied when the game is in the foreground).";
        var t = Target;
        DTarget.Text = t is null ? "" :
            !t.IsDetected ? $"{t.Name} wasn't found. Set its path on the Emulators page." :
            t.SupportsPerGame ? $"Writes a per-game profile in {t.ConfigRoot}" :
            $"{t.Name} has no per-game configs, so this changes its global settings.";
        ApplyBtn.IsEnabled = MaxBtn.IsEnabled = t?.IsDetected == true;

        var settings = _s.BuildSettings(_game.Emulator, tier, _game);
        SettingsPreview.ItemsSource = Json.Flatten(settings).Select(x => new SettingLine(x.Path, x.Value)).ToList();
    }

    private void OnApply(object sender, RoutedEventArgs e) => Apply(SelectedTier);

    private void OnMax(object sender, RoutedEventArgs e)
    {
        TierPick.SelectedIndex = (int)Tier.Max;
        Apply(Tier.Max);
    }

    private void Apply(Tier tier)
    {
        if (_game is null || Target is not { IsDetected: true } target) return;
        try
        {
            var r = _s.ApplyGame(target, _game, tier);
            var msg = r.Files.Count > 0
                ? $"Wrote {r.Files.Count} file(s):\n{string.Join("\n", r.Files)}"
                : string.Join("\n", r.Warnings);
            ShowMessage(r.Files.Count > 0 ? "Profile applied" : "Nothing written", msg);
        }
        catch (Exception ex) { ShowMessage("Couldn't apply", ex.Message); }
    }

    private async void OnPowerOnly(object sender, RoutedEventArgs e)
    {
        var tier = SelectedTier;
        await Task.Run(() => _s.ApplyPower(tier, $"{_game?.Title}"));
    }

    private static void ShowMessage(string title, string text) =>
        _ = new Wpf.Ui.Controls.MessageBox { Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK" }.ShowDialogAsync();

    private void OnSource(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
