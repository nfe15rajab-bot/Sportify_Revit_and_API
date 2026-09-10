using System.Windows;
using System.Windows.Controls;

namespace SportfyRevit
{
    /// <summary>
    /// Hosts the Sportify web app inside a Revit dockable pane via
    /// WebView2 (real Chromium — the legacy WPF WebBrowser control is
    /// IE11-based and can't run this app's modern JS: async/await,
    /// template literals, optional chaining, fetch, all over the
    /// controller files). Points at the port Program.cs's CORS policy
    /// and RoofBoundaryServer's own comments already treat as the app's
    /// address (8123) — not the 5173 this session happened to preview
    /// on — but it's one constant to change if your actual dev server
    /// runs elsewhere. Errors (dev server not running, WebView2 Runtime
    /// missing) are shown inside the pane itself rather than a dialog,
    /// since a dockable pane has no dialog to return to.
    /// </summary>
    public partial class SportifyBrowserPane : UserControl
    {
        public const string DefaultUrl = "http://localhost:8123";

        public SportifyBrowserPane()
        {
            InitializeComponent();
            Loaded += async (_, _) => await InitializeBrowserAsync();
        }

        private async System.Threading.Tasks.Task InitializeBrowserAsync()
        {
            try
            {
                await Browser.EnsureCoreWebView2Async();
                Browser.CoreWebView2.Navigate(DefaultUrl);
            }
            catch (System.Exception ex)
            {
                Browser.Visibility = Visibility.Collapsed;
                ErrorText.Visibility = Visibility.Visible;
                ErrorText.Text =
                    "Couldn't load the Sportify app: " + ex.Message +
                    "\n\nCheck that the web app's dev server is running at " + DefaultUrl +
                    ", and that the Microsoft Edge WebView2 Runtime is installed " +
                    "(present by default on most Windows 10/11 machines with Edge).";
            }
        }
    }
}
