using System.Text.RegularExpressions;
using JiranisokoTech.Application.Observability;
using JiranisokoTech.Tests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// A failure can be followed from the reference a person reads out to every line it logged.
/// </summary>
/// <remarks>
/// Section 45 asks for request tracing and error tracking. Two things stood between a reported
/// error and its cause. Nothing listened to the application's own trace source, so every span
/// the dispatchers and jobs opened was null and background work had no trace id at all. And the
/// error page's reference was the whole W3C header rather than the trace id a log line holds, so
/// it matched nothing anybody would search for.
///
/// The host puts the trace id on every log record by default, and the JSON log settings include
/// it by writing scopes; the first test holds both, because losing either quietly cuts the thread.
/// </remarks>
public partial class TracingTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    [Fact]
    public void Every_log_record_carries_its_trace_and_span()
    {
        var settings = Path.Combine(
            AppContext.BaseDirectory[..AppContext.BaseDirectory.IndexOf("tests", StringComparison.Ordinal)],
            "src", "JiranisokoTech.Web", "appsettings.Production.json");

        Assert.Contains("\"IncludeScopes\": true", File.ReadAllText(settings));

        var tracking = factory.Services
            .GetRequiredService<IOptions<LoggerFactoryOptions>>()
            .Value.ActivityTrackingOptions;

        Assert.True(tracking.HasFlag(ActivityTrackingOptions.TraceId));
        Assert.True(tracking.HasFlag(ActivityTrackingOptions.SpanId));
    }

    /// <summary>
    /// A span the application opens in the background is really opened.
    /// </summary>
    /// <remarks>
    /// With no listener <c>StartActivity</c> returns null, and every <c>using var span</c> in
    /// the dispatchers and the scheduler did nothing.
    /// </remarks>
    [Fact]
    public void The_applications_own_spans_are_created()
    {
        _ = factory.Services;

        using var span = Telemetry.Source.StartActivity("a background task");

        Assert.NotNull(span);
        Assert.NotEqual(default, span!.TraceId);
    }

    /// <summary>
    /// The reference on the error page is a trace id, the thing a log line holds.
    /// </summary>
    [Fact]
    public async Task The_error_page_reference_is_the_trace_id()
    {
        using var browser = factory.CreateBrowser();

        var page = await (await browser.GetAsync("/error")).Content.ReadAsStringAsync();

        Assert.Matches(TraceId(), page);
    }

    [GeneratedRegex(@"<code>[0-9a-f]{32}</code>")]
    private static partial Regex TraceId();
}
