using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using ProcStudio.Core;

namespace ProcStudio.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel();
        DataContext = ViewModel;

        Loaded += MainWindow_Loaded;
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += async (_, _) => await ViewModel.RefreshAsync();
        _refreshTimer.Start();
    }

    public MainViewModel ViewModel { get; }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }

    private void ProcessTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        ViewModel.SelectTreeNode(e.NewValue as ObservableProcessTreeNode);
    }

    private void ProfileComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ViewModel.ApplySelectedProfile();
    }

    private void ExportJson_OnClick(object sender, RoutedEventArgs e)
        => SaveContent("snapshot.json", "JSON|*.json", ViewModel.ExportJson());

    private void ExportCsv_OnClick(object sender, RoutedEventArgs e)
        => SaveContent("snapshot.csv", "CSV|*.csv", ViewModel.ExportCsv());

    private void ExportHtml_OnClick(object sender, RoutedEventArgs e)
        => SaveContent("snapshot.html", "HTML|*.html", ViewModel.ExportHtml());

    private void SaveContent(string fileName, string filter, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            MessageBox.Show(this, "Nenhum snapshot disponivel para exportacao.", "ProcStudio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = fileName,
            Filter = filter,
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, content, Encoding.UTF8);
        MessageBox.Show(this, $"Relatorio salvo em {dialog.FileName}", "ProcStudio", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
