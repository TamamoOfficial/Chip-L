using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChipL.Core;

namespace ChipL;

/// <summary>热更新完成后的「本次更新了什么」说明窗口。</summary>
public partial class UpdateSummaryWindow : Window
{
    private readonly List<DbDiff> _diffs;

    /// <summary>用户点击「查看新增型号」时要定位的记录（为空表示不跳转）。</summary>
    public ChipRecord JumpTarget { get; private set; }
    public ChipModule JumpModule { get; private set; }

    /// <summary>用户请求跳转到某个新增型号（非模态窗口下代替 DialogResult）。</summary>
    public event EventHandler<ChipRecord> JumpRequested;

    public UpdateSummaryWindow(List<DbDiff> diffs)
    {
        InitializeComponent();
        _diffs = diffs;

        var totalAdded = diffs.Sum(d => d.Added.Count);
        var modules = string.Join("、", diffs.Select(d => SpecSchema.ModuleTitle(d.Module)).Distinct());

        TitleText.Text = totalAdded > 0
            ? $"数据库已更新 · 新增 {totalAdded:N0} 款型号"
            : "数据库已更新";
        SubtitleText.Text = modules + " · " + string.Join("，",
            diffs.Select(d => $"v{d.OldVersion} → v{d.NewVersion}"));

        BuildStats();
        BuildDetail();

        var first = diffs.FirstOrDefault(d => d.Added.Count > 0);
        JumpButton.IsEnabled = first != null;
        if (first == null)
        {
            JumpButton.Visibility = Visibility.Collapsed;
            FooterText.Text = "本次更新以修正/调整既有条目为主，没有新增型号。";
        }
        else
        {
            JumpModule = first.Module;
            JumpTarget = first.Added[0].Record;
        }
    }

    private void BuildStats()
    {
        foreach (var d in _diffs)
        {
            var name = SpecSchema.ModuleTitle(d.Module);
            AddStat(name, $"{d.OldCount:N0} → {d.NewCount:N0}", d.Delta);
            if (d.Added.Count > 0) AddStat("新增型号", d.Added.Count.ToString("N0"), d.Added.Count);
            if (d.NewSeries.Count > 0) AddStat("新增系列", d.NewSeries.Count.ToString("N0"), d.NewSeries.Count);
            if (d.RemovedCount > 0) AddStat("移除条目", d.RemovedCount.ToString("N0"), -d.RemovedCount);
        }
    }

    private void AddStat(string label, string value, int tone)
    {
        var color = tone > 0 ? Color.FromRgb(0x46, 0xD3, 0x9A)
            : tone < 0 ? Color.FromRgb(0xE0, 0x6C, 0x6C)
            : Color.FromRgb(0x95, 0xA3, 0xB8);

        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color)
        };
        var stack = new StackPanel { Margin = new Thickness(0, 0, 24, 4) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("SubTextBrush"),
            Margin = new Thickness(0, 0, 0, 2)
        });
        stack.Children.Add(valueText);
        StatsPanel.Children.Add(stack);
    }

    /// <summary>
    /// 生成一句人话总结：
    /// 「本次更新 SoC 数据库，新增型号“X”（上代产品为“Y”）；另有 3 款新型号…」
    /// 没有任何新增时退回说明本次以修正为主。
    /// </summary>
    private static string BuildSummarySentence(string module, DbDiff d)
    {
        var parts = new List<string>();

        if (d.Added.Count == 0)
        {
            parts.Add($"本次更新 {module} 数据库，未新增型号");
            if (d.RemovedCount > 0) parts.Add($"移除 {d.RemovedCount} 条记录");
            return string.Join("，", parts) + "。";
        }

        // 主句：第一款新增型号 + 其上代
        var first = d.Added
            .OrderByDescending(a => a.Record.LaunchOrder)
            .First();
        var head = $"本次更新 {module} 数据库，新增型号“{first.Name}”";
        if (!string.IsNullOrEmpty(first.Predecessor))
            head += $"，上代产品为“{first.Predecessor}”";
        else
            head += "，未找到同系列上代产品";
        parts.Add(head);

        // 其余新增型号
        int rest = d.Added.Count - 1;
        if (rest > 0)
        {
            var others = d.Added
                .Where(a => !ReferenceEquals(a, first))
                .OrderByDescending(a => a.Record.LaunchOrder)
                .Take(3)
                .Select(a => "“" + a.Name + "”");
            var text = $"，另有 {rest} 款新型号";
            var names = string.Join("、", others);
            if (!string.IsNullOrEmpty(names)) text += $"（{names}{(rest > 3 ? " 等" : "")}）";
            parts.Add(text);
        }

        if (d.NewSeries.Count > 0)
            parts.Add($"，涉及 {d.NewSeries.Count} 个新增系列");
        if (d.RemovedCount > 0)
            parts.Add($"，移除 {d.RemovedCount} 条记录");

        return string.Join("", parts) + "。";
    }

    private void BuildDetail()
    {
        DiffPanel.Children.Clear();

        foreach (var d in _diffs)
        {
            if (!d.HasChanges) continue;
            var module = SpecSchema.ModuleTitle(d.Module);

            DiffPanel.Children.Add(new TextBlock
            {
                Text = $"{module} 数据库　v{d.OldVersion} → v{d.NewVersion}",
                Style = (Style)FindResource("SectionTitle"),
                Margin = new Thickness(0, DiffPanel.Children.Count == 0 ? 0 : 12, 0, 8)
            });

            // 一句人话总结：本次更新X数据库，新增型号“A”“B”，上代产品为“C”
            DiffPanel.Children.Add(new TextBlock
            {
                Text = BuildSummarySentence(module, d),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 0, 0, 6)
            });

            if (d.Added.Count > 0)
            {
                var bySeries = d.Added
                    .GroupBy(a => $"{a.Brand} · {a.Category} · {a.SerieName}")
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

                foreach (var g in bySeries)
                {
                    DiffPanel.Children.Add(new TextBlock
                    {
                        Text = g.Key,
                        FontSize = 11.5,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xA8, 0xD8)),
                        Margin = new Thickness(0, 8, 0, 4)
                    });

                    foreach (var item in g.OrderByDescending(a => a.Record.LaunchOrder).Take(6))
                        DiffPanel.Children.Add(BuildAddedRow(item));

                    if (g.Count() > 6)
                    {
                        DiffPanel.Children.Add(new TextBlock
                        {
                            Text = $"　…同系列还有 {g.Count() - 6} 款新增型号",
                            FontSize = 10.5,
                            Foreground = (Brush)FindResource("SubTextBrush"),
                            Margin = new Thickness(6, 2, 0, 4)
                        });
                    }
                }
            }

            if (d.NewSeries.Count > 0)
            {
                DiffPanel.Children.Add(new TextBlock
                {
                    Text = "新增系列：" + string.Join("、", d.NewSeries.Take(12))
                           + (d.NewSeries.Count > 12 ? $" 等 {d.NewSeries.Count} 个" : ""),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("SubTextBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0)
                });
            }

            if (d.RemovedCount > 0)
            {
                DiffPanel.Children.Add(new TextBlock
                {
                    Text = $"另有 {d.RemovedCount} 条记录在新版本中被移除。",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C)),
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }
        }
    }

    private FrameworkElement BuildAddedRow(AddedItem item)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x23, 0x31)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 4)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextBrush")
        });

        var meta = new List<string>();
        if (!string.IsNullOrEmpty(item.Predecessor)) meta.Add($"上代：{item.Predecessor}");
        var launch = ValueFormat.Launch(item.Record.Get("launch"));
        if (!string.IsNullOrWhiteSpace(launch)) meta.Add(launch);
        if (meta.Count > 0)
        {
            left.Children.Add(new TextBlock
            {
                Text = string.Join("　·　", meta),
                FontSize = 10.5,
                Foreground = (Brush)FindResource("SubTextBrush"),
                Margin = new Thickness(0, 2, 0, 0)
            });
        }
        grid.Children.Add(left);

        var jump = new TextBlock
        {
            Text = "查看 ›",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0x9C, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 2, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        jump.MouseLeftButtonUp += (_, _) =>
        {
            JumpModule = item.Record.Module;
            JumpTarget = item.Record;
            JumpRequested?.Invoke(this, item.Record);
            Close();
        };
        Grid.SetColumn(jump, 1);
        grid.Children.Add(jump);

        row.Child = grid;
        return row;
    }

    private void OnJump(object sender, RoutedEventArgs e)
    {
        if (JumpTarget != null) JumpRequested?.Invoke(this, JumpTarget);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
