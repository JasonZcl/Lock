using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lock.Core.Models;
using Lock.Services;

namespace Lock.Views;

public partial class InstalledAppPickerWindow : Window
{
    public sealed class Item
    {
        public required string Name { get; init; }
        public string? Publisher { get; init; }
        public string? ExePath { get; init; }
        public string Source { get; init; } = "";
        public ImageSource? Icon { get; init; }
        public bool HasExe => ExePath != null;
    }

    private List<Item> _all = [];

    public List<LockedApp> Selected { get; } = [];

    public InstalledAppPickerWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        CountText.Text = "正在扫描…";
        var apps = await Task.Run(InstalledAppScanner.Scan);

        _all = apps.Select(a => new Item
        {
            Name = a.Name,
            Publisher = a.Publisher,
            ExePath = a.ExePath,
            Source = a.Source,
            Icon = IconHelper.GetIcon(a.ExePath),
        }).ToList();

        ApplyFilter();
        SearchBox.Focus();
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var items = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                              || (i.Publisher?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                              || (i.ExePath?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        AppList.ItemsSource = items;
        CountText.Text = $"{items.Count(i => i.HasExe)} / {items.Count} 个可添加";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppList.SelectedItem is Item { HasExe: true }) Confirm();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        foreach (var item in AppList.SelectedItems.OfType<Item>().Where(i => i.HasExe))
        {
            Selected.Add(new LockedApp
            {
                ExeName = Path.GetFileName(item.ExePath!).ToLowerInvariant(),
                DisplayName = item.Name,
                FullPath = item.ExePath,
                MatchMode = MatchMode.FullPath,
            });
        }
        DialogResult = Selected.Count > 0;
    }
}
