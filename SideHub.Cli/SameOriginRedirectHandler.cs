using System.Net;

namespace SideHub.Cli;

/// <summary>
/// Follows redirects only to the origin (scheme, host, port) the request was sent to. The API client sends
/// X-Agent-Token on every request, and HttpClient's own redirect handling only strips Authorization: a 3xx to another
/// host would hand it the token. A redirect elsewhere is returned as is, and fails like any other non-2xx response.
/// The inner handler must not follow redirects itself.
/// </summary>
public sealed class SameOriginRedirectHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const int MaxRedirects = 10;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        for (var hops = 0; hops < MaxRedirects; hops++)
        {
            var target = RedirectTarget(request, response);
            if (target is null) return response;

            // 307/308 replay the request as is; the others turn it into a GET without a body (as browsers do).
            var keepMethod = response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            var next = new HttpRequestMessage(keepMethod ? request.Method : RedirectMethod(request.Method), target)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy,
                Content = keepMethod ? request.Content : null,
            };
            foreach (var header in request.Headers)
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);

            response.Dispose();
            request = next;
            response = await base.SendAsync(request, ct);
        }
        return response;
    }

    /// <summary>Where a same-origin redirect points, or null when the response isn't one to follow.</summary>
    public static Uri? RedirectTarget(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            return null;
        if (request.RequestUri is not { } from || response.Headers.Location is not { } location)
            return null;

        var to = location.IsAbsoluteUri ? location : new Uri(from, location);
        return Uri.Compare(from, to, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
            ? to
            : null;
    }

    private static HttpMethod RedirectMethod(HttpMethod method) =>
        method == HttpMethod.Head ? HttpMethod.Head : HttpMethod.Get;
}
