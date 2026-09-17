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
                    await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(
                        Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.DiskCache);
                }
                catch (System.Exception)
                {
                    // Older WebView2 runtimes lack Profile/ClearBrowsingDataAsync.
                    // The cache-busting query below still forces a fresh page.
                }

                // Belt and braces: a URL that changes every load can't be served
                // from cache even if the clear above was unavailable.
                Browser.CoreWebView2.Navigate(
                    $"{DefaultUrl}/?v={System.DateTime.UtcNow.Ticks}");
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
