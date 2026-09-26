using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MdBolsa_Desktop_WinUI;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Window.Current is null in this app (verified: the folder picker reported
        // having no window to parent its dialog to), so the window's id is captured
        // here - where the window definitively exists - for anything that needs to
        // own a native dialog. See AppSession.WindowId.
        AppSession.WindowId = AppWindow.Id;

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }
}
