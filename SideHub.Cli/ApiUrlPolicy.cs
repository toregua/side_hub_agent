namespace SideHub.Cli;

/// <summary>
/// The SideHub API URL the CLI sends its token to: https, or http to the local machine only (development). Plain http
/// to any other host would hand the token to whoever sits on the network path.
/// </summary>
public static class ApiUrlPolicy
{
    /// <summary>Why the URL is refused, or null when it may be used.</summary>
    public static string? RejectionReason(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "must be an absolute http:// or https:// URL";
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            return "uses http:// (unencrypted) with a remote host: use https:// (http:// is only allowed for localhost)";
        return null;
    }
}
