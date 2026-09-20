using MuServer.Shared.Localization;

namespace MuServer.AdminPanel;

/// <summary>
/// Per-circuit translator used as <c>@L["English text"]</c> in the Razor components. The language is
/// chosen by the visitor (cookie, else Accept-Language, else English) and handed over by
/// <see cref="Components.Routes"/>; missing translations fall back to English.
/// </summary>
public sealed class Localizer
{
    public const string CookieName = "muserver.lang";

    public string Language { get; set; } = Loc.English;

    public string this[string english] => Loc.T(Language, english);

    public string F(string english, params object?[] args) => Loc.F(Language, english, args);

    /// <summary>Resolves the visitor's language from the request (cookie first, then Accept-Language).</summary>
    public static string FromRequest(HttpContext? http)
    {
        if (http == null)
        {
            return Loc.English;
        }

        if (http.Request.Cookies.TryGetValue(CookieName, out var cookie) && !string.IsNullOrWhiteSpace(cookie))
        {
            return Loc.Normalize(cookie);
        }

        var accept = http.Request.Headers.AcceptLanguage.ToString();

        return Loc.Normalize(accept.Split(',', ';')[0]);
    }
}
