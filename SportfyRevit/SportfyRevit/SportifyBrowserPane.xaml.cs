using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SportfyRevit
{
    /// <summary>
    /// Hosts the Sportify web app inside a Revit dockable pane via
    /// WebView2 (real Chromium — the legacy WPF WebBrowser control is
    /// IE11-based and can't run this app's modern JS: async/await,
    /// template literals, optional chaining, fetch, all over the
    /// controller files). Points at the address the add-in's own web server
    /// (StaticWebServer) serves the bundled app on (8123), and that
    /// RoofBoundaryServer's origin list already treats as the app's address.
    ///
    /// WebView2 needs a folder of its own to keep its profile in. Left to
    /// itself it makes one NEXT TO THE HOST PROGRAM, here
    /// C:\Program Files\Autodesk\Revit 2025\Revit.exe.WebView2, which an
    /// ordinary user cannot write to: the pane then failed with
    /// "Zugriff verweigert (0x80070005 E_ACCESSDENIED)" whatever the web
    /// server was doing. So the environment is created here, with its folder
    /// under the user's profile (and a folder in Temp if even that fails).
    /// Errors are shown inside the pane, with a way out (the same app in the
    /// user's browser) and in the add-in's log, since a dockable pane has no
    /// dialog to return to.
    /// </summary>
    public partial class SportifyBrowserPane : UserControl
    {
        public const string DefaultUrl = "http://localhost:8123";

        /// <summary>Where WebView2 keeps its profile: %LOCALAPPDATA%\Sportify\WebView2 (writable for the user, kept between sessions), then %TEMP%\Sportify-WebView2.</summary>
        internal static string[] DataFolders() => new[]
        {
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Sportify", "WebView2"),
            Path.Combine(Path.GetTempPath(), "Sportify-WebView2"),
        };

        public SportifyBrowserPane()
        {
            InitializeComponent();
            OpenInBrowserButton.Click += (_, _) => OpenInBrowser();
            RetryButton.Click += async (_, _) => await InitializeBrowserAsync();
            Loaded += async (_, _) => await InitializeBrowserAsync();
        }

        private static void OpenInBrowser()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DefaultUrl) { UseShellExecute = true }); }
            catch (System.Exception ex) { SportifyLog.Warn("pane", "could not open " + DefaultUrl + " in the browser: " + ex.Message); }
        }

        private async Task InitializeBrowserAsync()
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            System.Exception? last = null;
            string tried = "";
            foreach (var folder in DataFolders())
            {
                // A control that failed to start cannot be started again with another environment: each attempt gets a new one.
                var view = new WebView2();
                Host.Children.Remove(Browser);
                Host.Children.Insert(0, view);
                Browser = view;
                try
                {
                    Directory.CreateDirectory(folder);
                    var environment = await CoreWebView2Environment.CreateAsync(null, folder);
                    await Browser.EnsureCoreWebView2Async(environment);
                    SportifyLog.Info("pane", "WebView2 " + environment.BrowserVersionString + " started, its data in " + folder);
                    await LoadAppAsync();
                    return;
                }
                catch (System.Exception ex)
                {
                    last = ex;
                    tried += "\n  " + folder + ": " + ex.Message + " (0x" + ex.HResult.ToString("X8") + ")";
                    SportifyLog.Warn("pane", "WebView2 could not start with its data in " + folder + ": 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                }
            }

            SportifyLog.Error("pane", "the Sportify pane could not start WebView2", last);
            ShowError(
                "Couldn't load the Sportify app inside Revit: " + (last?.Message ?? "WebView2 did not start") +
                "\n\nWebView2 was tried with its data in:" + tried +
                "\n\nCheck that the Microsoft Edge WebView2 Runtime is installed (present by default on Windows 10/11 with Edge). " +
                "The same app runs in your browser at " + DefaultUrl + " while Revit is open: use the button below. Details are in the add-in's log (%APPDATA%\\Sportify\\logs).");
        }

        private async Task LoadAppAsync()
        {
            // Clear the disk cache before every load. The add-in's build
            // copies fresh web files into the served folder on each rebuild,
            // and WebView2 otherwise keeps running the previous build's
            // JavaScript — silently, so a deployed fix appears not to work
            // and the exported JSON is missing fields whose code is sitting
            // right there on disk. Ctrl+F5 does not reliably reach this
            // control, so it cannot be left to the user.
            // These files are local; there is nothing to gain from caching.
            try
            {
                await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            }
            catch (System.Exception)
            {
                // Older WebView2 runtimes lack Profile/ClearBrowsingDataAsync.
                // The cache-busting query below still forces a fresh page.
            }

            // A page that does not load (the add-in's web server could not take port 8123, say) is said here rather than left as the browser's own error page.
            Browser.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess) return;
                SportifyLog.Warn("pane", "the Sportify app did not load from " + DefaultUrl + ": " + e.WebErrorStatus);
                ShowError("The Sportify app did not load from " + DefaultUrl + " (" + e.WebErrorStatus + ").\n\nThe add-in serves it itself; if another program holds port 8123 it cannot. See the add-in's log (%APPDATA%\\Sportify\\logs).");
            };

            // Belt and braces: a URL that changes every load can't be served
            // from cache even if the clear above was unavailable.
            Browser.CoreWebView2.Navigate($"{DefaultUrl}/?v={System.DateTime.UtcNow.Ticks}");
        }

        private void ShowError(string text)
        {
            Browser.Visibility = Visibility.Collapsed;
            ErrorText.Text = text;
            ErrorPanel.Visibility = Visibility.Visible;
        }
    }
}
