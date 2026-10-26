using System.Reflection;
using Tempo.Core.Configuration;
using Tempo.Integrations.Google;

namespace Tempo.Tests.CalendarSync;

public sealed class GoogleClientTests
{
    [Fact]
    public void Consent_url_requests_only_the_readonly_scope()
    {
        var url = GoogleOAuthService.BuildConsentUrl(
            new GoogleOptions { ClientId = "cid", ClientSecret = "secret" }, state: "abc");

        var scope = Uri.UnescapeDataString(
            System.Web.HttpUtility.ParseQueryString(new Uri(url).Query).Get("scope") ?? "");
        Assert.Equal("https://www.googleapis.com/auth/calendar.readonly", scope);
        Assert.Contains("access_type=offline", url);
        Assert.Contains("state=abc", url);
    }

    [Fact]
    public void Calendar_client_implements_no_write_method()
    {
        string[] writeWords = ["insert", "update", "delete", "patch", "create", "move", "import"];
        var writers = typeof(GoogleCalendarClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => writeWords.Any(w => m.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Name)
            .ToList();

        Assert.Empty(writers);
    }
}
