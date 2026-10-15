using Microsoft.UI.Xaml;

namespace Aperture.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 760));
    }
}
