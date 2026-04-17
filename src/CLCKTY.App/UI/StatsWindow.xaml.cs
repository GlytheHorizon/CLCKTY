using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CLCKTY.App.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace CLCKTY.App.UI;

public partial class StatsWindow : Window
{
    private readonly StatsService _statsService;
    private readonly DispatcherTimer _refreshTimer;

    public StatsWindow(StatsService statsService)
    {
        InitializeComponent();
        _statsService = statsService;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += (_, _) => RefreshDisplay();

        Loaded += (_, _) =>
        {
            RefreshDisplay();
            _refreshTimer.Start();
        };

        Closing += (_, _) => _refreshTimer.Stop();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    public void RefreshDisplay()
    {
        LevelDisplay.Text = _statsService.Level.ToString();
        LevelText.Text = _statsService.Level.ToString();

        var currentXp = _statsService.CurrentXp;
        var xpNeeded = _statsService.XpToNextLevel;
        XpText.Text = $"{currentXp:N0} / {xpNeeded:N0} XP";

        // XP bar fill
        var xpBarParent = XpBarFill.Parent as Grid;
        if (xpBarParent != null)
        {
            var parentWidth = xpBarParent.ActualWidth;
            if (parentWidth > 0 && xpNeeded > 0)
            {
                var fraction = Math.Min(1.0, (double)currentXp / xpNeeded);
                XpBarFill.Width = parentWidth * fraction;
            }
        }

        MainTitleText.Text = _statsService.MainTitle;
        SecondaryTitleText.Text = _statsService.SecondaryTitle;

        TotalClackityText.Text = _statsService.TotalClackity.ToString("N0");
        TodayClackityText.Text = _statsService.TodayClackity.ToString("N0");
        WeekClackityText.Text = _statsService.ThisWeekClackity.ToString("N0");
        MonthClackityText.Text = _statsService.ThisMonthClackity.ToString("N0");
        KeyboardClicksText.Text = _statsService.TotalKeyboardClicks.ToString("N0");
        MouseClicksText.Text = _statsService.TotalMouseClicks.ToString("N0");

        RebuildTitlesList();
    }

    private void RebuildTitlesList()
    {
        var titles = _statsService.UnlockedTitles;
        TitlesPanel.Children.Clear();

        if (titles.Count == 0)
        {
            TitlesPanel.Children.Add(new TextBlock
            {
                Text = "No titles unlocked yet. Start clicking!",
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#5B8A76")),
                FontStyle = FontStyles.Italic,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 4)
            });
            return;
        }

        // Sort by rarity descending, then by unlock time
        var sorted = titles
            .OrderByDescending(t => StatsService.GetRarityRank(t.Rarity))
            .ThenByDescending(t => t.UnlockedAtUtc)
            .ToList();

        foreach (var title in sorted)
        {
            var rarityColor = StatsService.GetRarityColor(title.Rarity);
            var isMainTitle = title.Name == _statsService.MainTitle;

            var btn = new WpfButton
            {
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                Style = (Style)FindResource("TitleButtonStyle"),
                Tag = title,
                Padding = new Thickness(10, 8, 10, 8),
            };

            if (isMainTitle)
            {
                btn.BorderBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));
                btn.Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#1A4A38"));
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftPanel = new StackPanel();

            var namePanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            namePanel.Children.Add(new TextBlock
            {
                Text = title.Name,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(rarityColor)),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (isMainTitle)
            {
                namePanel.Children.Add(new TextBlock
                {
                    Text = " ★ EQUIPPED",
                    Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883")),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                });
            }

            leftPanel.Children.Add(namePanel);

            // Find definition for description
            var def = StatsService.TitleDefinitions.FirstOrDefault(d => d.Id == title.Id);
            if (def != null)
            {
                leftPanel.Children.Add(new TextBlock
                {
                    Text = def.IsSecret ? def.Description.Replace("[SECRET] ", "🔒 ") : def.Description,
                    Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#7BA998")),
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            Grid.SetColumn(leftPanel, 0);
            grid.Children.Add(leftPanel);

            // Rarity badge
            var rarityBorder = new Border
            {
                Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(rarityColor)) { Opacity = 0.15 },
                BorderBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(rarityColor)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };

            rarityBorder.Child = new TextBlock
            {
                Text = title.Rarity,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(rarityColor)),
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };

            Grid.SetColumn(rarityBorder, 1);
            grid.Children.Add(rarityBorder);

            btn.Content = grid;
            btn.Click += TitleButton_Click;

            TitlesPanel.Children.Add(btn);
        }
    }

    private void TitleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && btn.Tag is UnlockedTitle clickedTitle)
        {
            _statsService.MainTitle = clickedTitle.Name;
            RefreshDisplay();
        }
    }
}
