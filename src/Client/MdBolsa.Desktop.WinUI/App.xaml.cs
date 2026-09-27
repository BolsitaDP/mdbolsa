using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MdBolsa_Desktop_WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        // First-chance logging, and only in a debug build. This app dies to a
        // *native* stowed exception (0xc000027b) that never surfaces as a managed
        // one, so the Application event log says "something went wrong in
        // Microsoft.UI.Xaml.dll" and nothing else. With this, the last thing the app
        // tried before it went is written down, which is the difference between
        // bisecting the XAML by guesswork and bisecting it by evidence.
        //
        // Cheap enough to leave on for development, and compiled out of Release.
#if DEBUG
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            try
            {
                var log = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "mdbolsa-firstchance.log");

                File.AppendAllText(
                    log,
                    $"{DateTime.Now:HH:mm:ss.fff} {e.Exception.GetType().Name}: {e.Exception.Message}\r\n");
            }
            catch (IOException)
            {
                // A diagnostic that cannot write is not worth failing over.
            }
        };
#endif

        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
