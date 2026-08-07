using DWOriginsAssetTool.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace DWOriginsAssetTool;

public partial class NameCatalogWindow : Window
{
    private readonly GameNameCatalog _catalog;
    private readonly ObservableCollection<GameNameEntry> _visible = [];
    private readonly ObservableCollection<GameResourceBinding> _bindings = [];

    public NameCatalogWindow(GameNameCatalog catalog)
    {
        InitializeComponent(); _catalog = catalog; NameList.ItemsSource = _visible; BindingList.ItemsSource = _bindings;
        CategoryBox.Items.Add("全部分类");
        foreach (var category in catalog.Entries.Select(x => x.Category).Distinct().OrderBy(x => x)) CategoryBox.Items.Add(category);
        CategoryBox.SelectedIndex = 0;
        SummaryText.Text = $"来自 {catalog.DatabaseCount:N0} 个随游戏发布的 KIDS 数据库 · {catalog.Entries.Count:N0} 个正式名称 · {catalog.Bindings?.Count ?? 0:N0} 条已验证文件引用";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim(); var category = CategoryBox.SelectedItem?.ToString();
        var rows = _catalog.Entries.Where(x => (category is null or "全部分类" || x.Category == category) &&
            (query.Length == 0 || x.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))).Take(10000);
        _visible.Clear(); foreach (var row in rows) _visible.Add(row);
        ResultText.Text = $"显示 {_visible.Count:N0} / {_catalog.Entries.Count:N0} 条";
    }

    private void Filter_Changed(object sender, EventArgs e) { if (IsLoaded) ApplyFilter(); }
    private void NameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _bindings.Clear();
        if (NameList.SelectedItem is not GameNameEntry selected || _catalog.Bindings is null) return;
        foreach (var binding in _catalog.Bindings.Where(x => x.OfficialName == selected.OfficialName)) _bindings.Add(binding);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "JSON 文件|*.json", FileName = "DWOrigins.OfficialNames.json" };
        if (dialog.ShowDialog(this) != true) return;
        await KidsNameCatalogReader.SaveJsonAsync(_catalog, dialog.FileName); ResultText.Text = "已导出：" + dialog.FileName;
    }
}
