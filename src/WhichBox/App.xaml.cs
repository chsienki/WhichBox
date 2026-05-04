using Microsoft.UI.Xaml;

namespace WhichBox;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        // Catch exceptions that bubble up to the WinUI dispatcher (XAML thread).
        // We only LOG -- we deliberately do not set e.Handled = true. Suppressing
        // here can leave the XAML tree in a corrupt state and turn a real crash
        // into a half-dead "disappeared" app, which is the failure mode we are
        // trying to diagnose.
        UnhandledException += (_, e) =>
        {
            Logger.Crash($"Application.UnhandledException (Handled was {e.Handled}): {e.Message}", e.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            Logger.Crash("App.OnLaunched", ex);
            throw;
        }
    }
}

