using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace ADFi.Web;

public static class DiscoverHtmlRenderer
{
    private static readonly Regex YouTubeVideoIdPattern = new(
        "^[A-Za-z0-9_-]{11}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string RenderIndex(IReadOnlyList<DiscoverSurface> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var html = new StringBuilder(
            """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>DISCOVER — ADFi</title>
              <style>
                :root{color-scheme:light dark;font:100%/1.6 system-ui,sans-serif}body{max-width:72rem;margin:auto;padding:1rem}a,button{min-height:2.75rem;display:inline-flex;align-items:center}.skip{position:absolute;left:-9999px}.skip:focus{left:1rem;background:Canvas;padding:.5rem}.grid{display:grid;gap:1.5rem}.card{border:2px solid CanvasText;border-radius:.5rem;padding:1rem}.player{aspect-ratio:16/9;width:100%;border:0}nav ul{display:flex;gap:1rem;list-style:none;padding:0}@media(prefers-reduced-motion:reduce){*{scroll-behavior:auto!important;transition:none!important}}
              </style>
            </head>
            <body>
              <a class="skip" href="#main">Skip to main content</a>
              <header><strong>ADFi</strong><nav aria-label="Primary"><ul><li><a href="/my-adfi">MY ADFi</a></li><li><a href="/discover" aria-current="page">DISCOVER</a></li></ul></nav></header>
              <main id="main"><h1>DISCOVER</h1><p>Public ADFied works with accessibility details, attribution, and community feedback.</p>
            """);

        if (projects.Count == 0)
        {
            html.Append("<p role=\"status\">No eligible public ADFied works are available yet.</p>");
        }
        else
        {
            html.Append("<div class=\"grid\" aria-label=\"Public ADFied works\">");
            foreach (var project in projects)
            {
                AppendProject(html, project);
            }

            html.Append("</div>");
        }

        html.Append("</main><footer><p><a href=\"/plans\">Compare Community and Creator plans</a></p></footer></body></html>");
        return html.ToString();
    }

    public static string RenderUnavailable() =>
        """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>DISCOVER unavailable — ADFi</title></head><body><a href="#main">Skip to main content</a><header><strong>ADFi</strong><nav aria-label="Primary"><a href="/my-adfi">MY ADFi</a> <a href="/discover" aria-current="page">DISCOVER</a></nav></header><main id="main"><h1>DISCOVER</h1><p role="alert">DISCOVER is temporarily unavailable. Please try again later.</p></main></body></html>
        """;

    private static void AppendProject(StringBuilder html, DiscoverSurface project)
    {
        if (!YouTubeVideoIdPattern.IsMatch(project.YouTubeVideoId))
        {
            throw new ArgumentException("A DISCOVER item requires a valid YouTube video ID.", nameof(project));
        }

        if (project.CanonicalAdfiUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("A DISCOVER item requires an HTTPS ADFi URL.", nameof(project));
        }

        var encoder = HtmlEncoder.Default;
        var title = encoder.Encode(project.Title);
        var videoId = encoder.Encode(project.YouTubeVideoId);
        html.Append("<article class=\"card\"><h2>").Append(title).Append("</h2>")
            .Append("<iframe class=\"player\" src=\"https://www.youtube-nocookie.com/embed/")
            .Append(videoId)
            .Append("\" title=\"Official YouTube player for ").Append(title)
            .Append("\" loading=\"lazy\" allowfullscreen></iframe><h3>Accessibility</h3><p>")
            .Append(encoder.Encode(project.AccessibilitySummary))
            .Append("</p><h3>Attribution</h3><p>")
            .Append(encoder.Encode(project.Attribution))
            .Append("</p><a href=\"")
            .Append(encoder.Encode(project.CanonicalAdfiUrl.AbsoluteUri))
            .Append("\">Open and share this ADFi project page</a></article>");
    }
}
