using System.ComponentModel;
using System.Windows;
using M3U8Downloader.Models;
using M3U8Downloader.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace M3U8Downloader.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainWindowViewModel>();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (DataContext is MainWindowViewModel vm)
        {
            var activeTasks = vm.Tasks.Count(t => t.Status is TaskStatus.Downloading or TaskStatus.Merging or TaskStatus.Parsing);
            if (activeTasks > 0)
            {
                var result = MessageBox.Show(
                    $"{activeTasks} task(s) are currently downloading or merging.\nAre you sure you want to quit?",
                    "Confirm Exit",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
