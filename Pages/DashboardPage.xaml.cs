using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GameOp.Models;
using GameOp.Services;

namespace GameOp.Pages;

public partial class DashboardPage : Page
{
    private readonly AppState _s = AppState.Instance;
    private readonly Dictionary<Tier, ToggleButton> _cards = [];
    private Tier _selected;

    public DashboardPage()
    {
        InitializeComponent();
        LogList.ItemsSource = Log.Entries;
        _selected = _s.CurrentTier;
        BuildDevice();
        BuildTierCards();
        UpdateAuto();
        _s.PropertyChanged += OnStateChanged;
        Unloaded += (_, _) => _s.PropertyChanged -= OnStateChanged;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => { UpdateAuto(); if (e.PropertyName == nameof(AppState.CurrentTier)) Select(_s.CurrentTier); });

    private void BuildDevice()
    {
        var d = _s.Device;
        DeviceName.Text = d.Name;
        DeviceApu.Text = d.IsAlly ? $"{d.Apu} · {_s.HardwareModel}" : $"{_s.HardwareModel} · {_s.HardwareCpu}";
        foreach (var chip in new[] { d.Cpu, d.Gpu, d.Ram, d.Display, d.BatteryWh > 0 ? $"{d.BatteryWh} Wh" : "" }.Where(c => c.Length > 1))
        {
            SpecChips.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6),
                Background = (System.Windows.Media.Brush)FindResource("ControlFillColorSecondaryBrush"),
                Child = new TextBlock { Text = chip, FontSize = 12 }
            });
        }
        var (ac, pct) = PowerService.Battery();
        BatteryText.Text = pct < 0 ? (ac ? "🔌 AC power" : "") : $"{(ac ? "🔌" : "🔋")} {pct}%";
        var hz = DisplayService.CurrentRefresh();
        RefreshText.Text = hz > 0 ? $"Display {hz} Hz" : "";
    }

    private void BuildTierCards()
    {
        foreach (var tier in TierInfo.All)
        {
            var plan = PowerService.PlanFor(_s.Device, tier);
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = tier.Label(), FontSize = 17, FontWeight = FontWeights.SemiBold });
            content.Children.Add(new TextBlock
            {
                Text = $"{plan.Spl} W  ·  boost {plan.Fppt} W  ·  {(tier == Tier.Battery ? 60 : 120)} Hz",
                FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6)
            });
            content.Children.Add(new TextBlock { Text = tier.Blurb(), Style = (Style)FindResource("Caption") });

            var card = new ToggleButton { Style = (Style)FindResource("TierCard"), Content = content, Tag = tier };
            card.Click += (_, _) => Select(tier);
            _cards[tier] = card;
            TierGrid.Children.Add(card);
        }
        Select(_selected);
    }

    private void Select(Tier tier)
    {
        _selected = tier;
        foreach (var (t, card) in _cards) card.IsChecked = t == tier;
    }

    private void UpdateAuto()
    {
        AutoBar.Message = _s.Settings.AutoPowerPerGame
            ? (string.IsNullOrEmpty(_s.AutoStatus) ? "Watching for games…" : _s.AutoStatus)
            : "Off – turn it on in Settings to switch TDP automatically when a known game is in the foreground.";
        AutoBar.Severity = _s.AutoStatus.StartsWith("Playing") ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Informational;
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        try
        {
            await _s.SetTierAsync(_selected, IncludeEmulators.IsChecked == true);
            var hz = DisplayService.CurrentRefresh();
            RefreshText.Text = hz > 0 ? $"Display {hz} Hz" : "";
        }
        finally
        {
            ApplyButton.IsEnabled = true;
            Busy.Visibility = Visibility.Collapsed;
        }
    }
}
