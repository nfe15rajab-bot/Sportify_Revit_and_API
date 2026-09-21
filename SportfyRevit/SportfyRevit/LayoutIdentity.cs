using System.Security.Cryptography;
using System.Text;

namespace SportfyRevit
{
    /// <summary>
    /// A layout's identity: the first 16 hex characters of the SHA-256 of the bytes the web app posted (UTF-8). The web app can compute the same
    /// number from the text it sends (crypto.subtle.digest), and the add-in returns it in the answer to the post, so every result the add-in
    /// publishes can say which layout it was computed for and the web app can tell, at any moment, whether a result is still about the layout
    /// on screen. Free of Revit types on purpose (AddinCheck and ContractCheck test it).
    /// </summary>
    internal static class LayoutIdentity
    {
        public static string Of(string layoutJson)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(layoutJson ?? ""));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }
    }
}
