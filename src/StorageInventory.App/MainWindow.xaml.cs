using System.Windows;
using StorageInventory.App.Services;
using StorageInventory.App.ViewModels;

namespace StorageInventory.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new FolderPicker());
        Loaded += (_, _) => SourceBox.Focus();
    }
}
