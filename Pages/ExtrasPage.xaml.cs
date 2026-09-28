using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using GameOp.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace GameOp.Pages;

public partial class ExtrasPage : Page
{
    private readonly AppState _s = AppState.Instance;
    private bool _loading = true;

    public ExtrasPage()
    {
        InitializeComponent();
        AutoAddons.IsChecked = _s.Settings.AutoUpdateAddons;
        AutoPacks.IsChecked = _s.Settings.AutoDownloadPacks;
        MaxGb.Value = _s.Settings.MaxAutoPackGb;

        PackFilter.Items.Add(new ComboBoxItem { Content = "All emulators", Tag = null });
        foreach (var emu in _s.Extras.Packs.Select(p => p.Emulator).Distinct())
            PackFilter.Items.Add(new ComboBoxItem { Content = _s.Db.Emulator(emu)?.Name ?? emu, Tag = emu });
        PackFilter.SelectedIndex = 0;

        foreach (var e in _s.Emulators.Where(e => e.IsDetected && e.TextureDir("X") is not null))
            FileEmu.Items.Add(new ComboBoxItem { Content = e.Name, Tag = e });
        FileEmu.SelectedIndex = FileEmu.Items.Count > 0 ? 0 : -1;

        _s.ExtrasChanged += Rebuild;
        _s.EmulatorsChanged += Rebuild;
        Unloaded += (_, _) => { _s.ExtrasChanged -= Rebuild; _s.EmulatorsChanged -= Rebuild; };
        _loading = false;
        Rebuild();
    }

    private void Rebuild() => Dispatcher.BeginInvoke(() => { BuildEnhancements(); BuildAddons(); BuildPacks(); });

    // ───────────── Enhancements ─────────────

    private void BuildEnhancements()
    {
        EnhancementHost.Children.Clear();
        foreach (var group in _s.Extras.Enhancements.GroupBy(x => x.Emulator))
        {
            var adapters = _s.Emulators.Where(e => e.DbKey == group.Key).ToList();
            var detected = adapters.Any(a => a.IsDetected);
            var panel = new StackPanel();
            foreach (var x in group)
            {
                var sw = new ToggleSwitch { IsChecked = _s.IsOn(x), IsEnabled = detected, VerticalAlignment = VerticalAlignment.Center };
                sw.Click += (_, _) => _s.SetEnhancement(x, sw.IsChecked == true);
                var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
                text.Children.Add(new TextBlock { Text = x.Name + (x.Heavy ? "   · heavier, skipped on Battery Saver" : ""), FontWeight = FontWeights.SemiBold });
                text.Children.Add(Caption(x.Description));
                var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(text);
                Grid.SetColumn(sw, 1);
                row.Children.Add(sw);
                panel.Children.Add(row);
            }
            var name = _s.Db.Emulator(group.Key)?.Name ?? group.Key;
            EnhancementHost.Children.Add(new CardExpander
            {
                Header = detected ? name : $"{name}  (not installed)",
                Content = panel,
                IsExpanded = detected,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }
    }

    // ───────────── Add-ons ─────────────

    private void BuildAddons()
    {
        AddonHost.Children.Clear();
        foreach (var a in _s.Extras.Addons)
        {
            var e = _s.AdapterFor(a.Emulator);
            var status = _s.AddonStatus.TryGetValue(a.Id, out var st) ? st.Status
                : _s.Settings.AddonVersions.ContainsKey(a.Id) ? "Installed" : e is null ? $"Needs {a.Emulator}" : "Not checked yet";
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = a.Name, FontSize = 16, FontWeight = FontWeights.SemiBold });
            info.Children.Add(Caption(a.Description));
            info.Children.Add(Caption("⟳ " + status));
            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (e is not null)
            {
                buttons.Children.Add(Btn(_s.Settings.AddonVersions.ContainsKey(a.Id) ? "Update / reinstall" : "Install", SymbolRegular.ArrowDownload24, async () =>
                {
                    try { await Notify(await _s.InstallAddonAsync(a)); }
                    catch (Exception ex) { await Notify($"{a.Name}: {ex.Message}"); }
                }, primary: true));
                var keep = new ToggleSwitch { Content = "Keep updated", IsChecked = a.Default && !_s.Settings.AddonsDisabled.Contains(a.Id), Margin = new Thickness(8, 0, 0, 8) };
                keep.Click += (_, _) =>
                {
                    if (keep.IsChecked == true) _s.Settings.AddonsDisabled.Remove(a.Id); else _s.Settings.AddonsDisabled.Add(a.Id);
                    _s.SaveSettings();
                };
                buttons.Children.Add(keep);
            }
            if (a.Repo is not null)
                buttons.Children.Add(Btn("Source", SymbolRegular.Link24, () => Open($"https://github.com/{a.Repo}")));
            info.Children.Add(buttons);
            AddonHost.Children.Add(new Card { Padding = new Thickness(20, 14, 20, 10), Margin = new Thickness(0, 0, 0, 6), Content = info });
        }
    }

    private void OnAutoAddons(object sender, RoutedEventArgs e)
    {
        _s.Settings.AutoUpdateAddons = AutoAddons.IsChecked == true;
        _s.SaveSettings();
    }

    private async void OnCheckAddons(object sender, RoutedEventArgs e) => await _s.CheckAddonsAsync(_s.Settings.AutoUpdateAddons);

    // ───────────── Texture packs ─────────────

    private void BuildPacks()
    {
        PackHost.Children.Clear();
        var filter = (PackFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var packs = _s.Extras.Packs.Where(p => filter is null || p.Emulator == filter).ToList();
        PackSummary.Text = $"{_s.Extras.Packs.Count} packs in the catalog · {_s.Settings.InstalledPacks.Count} installed";
        foreach (var p in packs)
        {
            var e = _s.AdapterFor(p.Emulator);
            var owned = e is not null && p.GameIds.Any(_s.OwnedIds(e).Contains);
            var status = _s.PackStatus.GetValueOrDefault(p.Id) ?? (_s.IsPackInstalled(p) ? "Installed" : owned ? "In your library" : "");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = p.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            info.Children.Add(Caption($"{p.Name} · {_s.Db.Emulator(p.Emulator)?.Name ?? p.Emulator} · {string.Join(", ", p.GameIds)} · {p.SizeText}" +
                                      (p.Author is null ? "" : $" · by {p.Author}")));
            if (!string.IsNullOrEmpty(p.Notes)) info.Children.Add(Caption(p.Notes));
            if (status.Length > 0) info.Children.Add(Caption("● " + status));
            grid.Children.Add(info);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (e is not null)
                buttons.Children.Add(Btn(_s.IsPackInstalled(p) ? "Reinstall" : "Download", SymbolRegular.ArrowDownload24, async () =>
                {
                    try { await Notify(await _s.InstallPackAsync(p)); }
                    catch (Exception ex) { await Notify($"{p.Name}: {ex.Message}"); }
                }, primary: !_s.IsPackInstalled(p)));
            if (p.SourcePage is not null) buttons.Children.Add(Btn("Source", SymbolRegular.Link24, () => Open(p.SourcePage)));
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
            PackHost.Children.Add(new Card { Padding = new Thickness(20, 12, 20, 12), Margin = new Thickness(0, 0, 0, 6), Content = grid });
        }
        if (packs.Count == 0)
            PackHost.Children.Add(Caption("No packs in the catalog for this emulator yet. Use 'Install a pack you downloaded yourself' above, or import a catalog."));
    }

    private void OnPackFilter(object sender, SelectionChangedEventArgs e) { if (!_loading) BuildPacks(); }

    private void OnAutoPacks(object sender, RoutedEventArgs e)
    {
        _s.Settings.AutoDownloadPacks = AutoPacks.IsChecked == true;
        _s.SaveSettings();
    }

    private void OnMaxGb(object sender, RoutedEventArgs e)
    {
        if (_loading || MaxGb.Value is not { } v) return;
        _s.Settings.MaxAutoPackGb = v;
        _s.SaveSettings();
    }

    private void OnFileEmu(object sender, SelectionChangedEventArgs e)
    {
        FileGame.Items.Clear();
        if ((FileEmu.SelectedItem as ComboBoxItem)?.Tag is not EmulatorAdapter a) return;
        foreach (var g in _s.Db.GamesFor(a.DbKey))
            foreach (var id in g.Ids)
                FileGame.Items.Add(new ComboBoxItem { Content = $"{id} – {g.Title}", Tag = id });
    }

    private (EmulatorAdapter? Emu, string? GameId) FileTarget()
    {
        var emu = (FileEmu.SelectedItem as ComboBoxItem)?.Tag as EmulatorAdapter;
        var id = (FileGame.SelectedItem as ComboBoxItem)?.Tag as string ?? FileGame.Text?.Split(' ')[0].Trim();
        return (emu, SafeId.IsValid(id) ? id!.ToUpperInvariant() : null);
    }

    private async void OnPackFile(object sender, RoutedEventArgs e)
    {
        var (emu, id) = FileTarget();
        if (emu is null || id is null) { await Notify("Pick an emulator and a game (or type a serial / game ID using only letters, digits and '-')."); return; }
        var dlg = new OpenFileDialog { Filter = "Texture pack|*.zip;*.7z;*.rar;*.tar;*.gz|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        await InstallFrom(dlg.FileName, emu, id);
    }

    private async void OnPackFolder(object sender, RoutedEventArgs e)
    {
        var (emu, id) = FileTarget();
        if (emu is null || id is null) { await Notify("Pick an emulator and a game first."); return; }
        var dlg = new OpenFolderDialog { Title = "Folder with the extracted texture pack" };
        if (dlg.ShowDialog() != true) return;
        await InstallFrom(dlg.FolderName, emu, id);
    }

    private async Task InstallFrom(string path, EmulatorAdapter emu, string id)
    {
        try { await Notify(await _s.InstallPackFromFileAsync(path, emu, id)); }
        catch (Exception ex) { await Notify(ex.Message); }
    }

    private async void OnImportCatalog(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Texture pack catalog (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var n = await ExtrasCatalog.ImportPacksAsync(dlg.FileName);
            _s.ReloadExtras();
            await Notify($"Imported {n} packs.");
        }
        catch (Exception ex) { await Notify(ex.Message); }
    }

    // ───────────── helpers ─────────────

    private static TextBlock Caption(string text) =>
        new() { Text = text, Style = (Style)Application.Current.FindResource("Caption"), Margin = new Thickness(0, 2, 0, 0) };

    private static Button Btn(string text, SymbolRegular icon, Action onClick, bool primary = false)
    {
        var b = new Button
        {
            Content = text, Icon = new SymbolIcon { Symbol = icon }, Margin = new Thickness(8, 0, 0, 8),
            Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static Task Notify(string text) =>
        new Wpf.Ui.Controls.MessageBox { Title = "GameOp", Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK" }.ShowDialogAsync();
}
