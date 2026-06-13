using System.Windows;
using M3U8Downloader.ViewModels;

namespace M3U8Downloader.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (!DialogResult.HasValue)
                DialogResult = false;
        };
        Loaded += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.CloseAction = () =>
                {
                    DialogResult = vm.DialogResult;
                    Close();
                };
        };
    }
}
