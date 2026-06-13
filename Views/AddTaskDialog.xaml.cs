using System.Windows;
using M3U8Downloader.ViewModels;

namespace M3U8Downloader.Views;

public partial class AddTaskDialog : Window
{
    public AddTaskDialog()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (!DialogResult.HasValue)
                DialogResult = false;
        };
        Loaded += (_, _) =>
        {
            if (DataContext is AddTaskDialogViewModel vm)
                vm.CloseAction = () =>
                {
                    DialogResult = vm.DialogResult;
                    Close();
                };
        };
    }
}
