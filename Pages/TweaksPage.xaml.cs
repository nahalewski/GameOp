using System.Windows;
using System.Windows.Controls;
using GameOp.Services;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace GameOp.Pages;

public partial class TweaksPage : Page
{
    private readonly AppState _s = AppState.Instance;

    public TweaksPage()
    {
        InitializeComponent();

        AddRegTweak(WindowsTweaks, "Game Mode", "Windows prioritises the game and pauses Windows Update activity while you play.",
            TweakService.GameMode, v => _s.Settings.TweakGameMode = v);
        AddRegTweak(WindowsTweaks, "Turn off background recording (Game DVR)", "Stops Xbox Game Bar from recording in the background, which costs GPU time and battery.",
            TweakService.DisableGameDvr, v => _s.Settings.TweakDisableGameDvr = v);
        AddRegTweak(WindowsTweaks, "Hardware-accelerated GPU scheduling", "Lower latency on RDNA 3 graphics. Takes effect after a restart.",
            TweakService.Hags, v => _s.Settings.TweakHags = v);
        AddRegTweak(WindowsTweaks, "Turn off Memory Integrity (security trade-off)",
            "Core isolation costs roughly 5–10% in some games and emulators. Turning it off lowers protection against kernel-level malware. Takes effect after a restart.",
            TweakService.DisableMemoryIntegrity, v => _s.Settings.TweakDisableMemoryIntegrity = v, warn: true);

        AddSetting(TierTweaks, "Windows power mode", "Best power efficiency on Battery Saver, Balanced, then Best performance.",
            _s.Settings.TweakPowerModePerTier, v => _s.Settings.TweakPowerModePerTier = v);
        AddSetting(TierTweaks, "CPU boost", "Off on Battery Saver (big battery gain for emulators that are GPU-bound), aggressive otherwise.",
            _s.Settings.TweakBoostPerTier, v => _s.Settings.TweakBoostPerTier = v);
        AddSetting(TierTweaks, "Refresh rate", "60 Hz on Battery Saver, 120 Hz on the other levels.",
            _s.Settings.TweakRefreshPerTier, v => _s.Settings.TweakRefreshPerTier = v);

        var ram = _s.Device.Ram.StartsWith("24") ? "8 GB" : "6 GB";
        foreach (var tip in new[]
        {
            $"Set the GPU memory (UMA frame buffer) to {ram} in Armoury Crate SE → Settings → Performance. RPCS3, Xenia and Switch emulators need the room.",
            "Keep the AMD Adrenalin and Armoury Crate SE drivers up to date. The ASUS ACPI driver that GameOp uses for TDP control ships with them.",
            "In AMD Software, turn on Radeon Super Resolution only for games that run below 1080p. Emulators upscale better internally.",
            "Store games on the internal SSD or a fast microSD (A2/V30). Slow cards cause stutter in PS3, Xbox 360 and Switch emulation.",
        })
            Tips.Children.Add(new TextBlock { Text = "•  " + tip, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
    }

    private void AddRegTweak(Panel host, string title, string description, RegTweak[] tweaks, Action<bool> remember, bool warn = false)
    {
        var sw = new ToggleSwitch { IsChecked = TweakService.IsApplied(tweaks) };
        sw.Click += async (_, _) =>
        {
            var on = sw.IsChecked == true;
            if (on && warn)
            {
                var ok = await new Wpf.Ui.Controls.MessageBox
                {
                    Title = title, Content = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = "Turn it off anyway", CloseButtonText = "Cancel"
                }.ShowDialogAsync();
                if (ok != Wpf.Ui.Controls.MessageBoxResult.Primary) { sw.IsChecked = false; return; }
            }
            try
            {
                var reboot = TweakService.Set(tweaks, on);
                remember(on);
                _s.SaveSettings();
                Log.Ok($"{title}: {(on ? "applied" : "reverted")}{(reboot ? " (restart to finish)" : "")}");
            }
            catch (Exception ex)
            {
                sw.IsChecked = !on;
                Log.Warn($"{title}: {ex.Message}");
            }
        };
        host.Children.Add(Row(title, description, sw, warn));
    }

    private void AddSetting(Panel host, string title, string description, bool value, Action<bool> set)
    {
        var sw = new ToggleSwitch { IsChecked = value };
        sw.Click += (_, _) => { set(sw.IsChecked == true); _s.SaveSettings(); };
        host.Children.Add(Row(title, description, sw, false));
    }

    private static UIElement Row(string title, string description, UIElement control, bool warn)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        var desc = new TextBlock { Text = description, Style = (Style)Application.Current.FindResource("Caption"), Margin = new Thickness(0, 2, 0, 0) };
        if (warn) desc.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("SystemFillColorCautionBrush");
        text.Children.Add(desc);
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        ((FrameworkElement)control).VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return new Card { Padding = new Thickness(20, 14, 20, 14), Margin = new Thickness(0, 0, 0, 6), Content = grid };
    }
}
