using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using JiranisokoTech.Application.Observability;
using Microsoft.Extensions.Hosting;

namespace JiranisokoTech.Infrastructure.Observability;

/// <summary>
/// Keeps a running total of every instrument, and writes it out for a scraper.
/// </summary>
/// <remarks>
/// A <see cref="MeterListener"/> rather than an exporter package. The instruments in
/// <see cref="Telemetry"/> are the standard .NET ones, so a real OpenTelemetry exporter
/// can be added later and will find them without anything changing — this is what stands
/// in until somebody wants a collector, and for a firm with one server it may be all
/// there ever is.
///
/// The totals live in memory and reset when the process does, which is correct rather
/// than a limitation: a counter in this format is a monotonic total that a scraper turns
/// into a rate, and a restart is exactly what the scraper is meant to notice.
///
/// Measurements arrive on whichever thread recorded them, so every read and write of the
/// totals is under a lock. It is a coarse lock over a dictionary of longs and it is not
/// worth anything cleverer: the writes are a handful a second and the read happens when
/// somebody scrapes.
/// </remarks>
public sealed class MetricsReader : IHostedService, IDisposable
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, double> _totals = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Instrument> _instruments = new(StringComparer.Ordinal);

    private MeterListener? _listener;

    /// <summary>
    /// What separates an instrument's name from its tags inside one dictionary key.
    /// </summary>
    /// <remarks>
    /// A control character, because it cannot occur in either half — an instrument name is
    /// dotted lower case and a tag is text somebody wrote. Named rather than written inline,
    /// where it would be an invisible character in a string literal that the next person to
    /// touch this file removes by accident.
    /// </remarks>
    private const char Separator = '\u0001';

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                // Only ours. Listening to every meter in the process would pull in the
                // runtime's own instruments and publish the shape of the host.
                if (instrument.Meter.Name == Telemetry.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        _listener.SetMeasurementEventCallback<long>(Record);
        _listener.SetMeasurementEventCallback<double>(Record);
        _listener.Start();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Dispose();
        _listener = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Everything counted so far, in the text format Prometheus scrapes.
    /// </summary>
    /// <remarks>
    /// Written by hand because the format is a handful of lines of specification and a
    /// library for it would be a dependency that does nothing else.
    ///
    /// <b>The help and type lines are written once per metric, not once per series</b>, and
    /// getting that wrong is what running this against a real scrape revealed. The first
    /// version emitted them beside every labelled series, so a metric with three providers
    /// carried three identical HELP lines — which a collector treats as a duplicate
    /// declaration and rejects the family over. The output looked plausible and was not
    /// ingestible, which is the worst combination available.
    ///
    /// <b>A histogram is reported as its running total, and named as one.</b> This reader
    /// keeps sums rather than buckets, so declaring one a histogram would promise quantiles
    /// that are not in the payload. A `_seconds_total` counter is what a sum of durations
    /// honestly is, and a collector can still derive an average from it against the run
    /// count beside it.
    /// </remarks>
    public string Scrape()
    {
        var page = new StringBuilder();

        lock (_gate)
        {
            var families = _totals
                .Select(entry =>
                {
                    var (name, labels) = Split(entry.Key);
                    return (Name: name, Labels: labels, Total: entry.Value);
                })
                .GroupBy(series => series.Name, StringComparer.Ordinal)
                .OrderBy(family => family.Key, StringComparer.Ordinal);

            foreach (var family in families)
            {
                var instrument = _instruments.GetValueOrDefault(family.Key);
                var exported = Exported(family.Key, instrument);

                page.Append("# HELP ").Append(exported).Append(' ')
                    .AppendLine(instrument?.Description ?? string.Empty);

                page.Append("# TYPE ").Append(exported).AppendLine(" counter");

                foreach (var series in family.OrderBy(one => one.Labels, StringComparer.Ordinal))
                {
                    page.Append(exported);

                    if (series.Labels is { Length: > 0 })
                    {
                        page.Append('{').Append(series.Labels).Append('}');
                    }

                    page.Append(' ')
                        .AppendLine(
                            series.Total.ToString("0.######", CultureInfo.InvariantCulture));
                }
            }
        }

        return page.ToString();
    }

    /// <summary>
    /// The name a scraper sees.
    /// </summary>
    /// <remarks>
    /// Dots become underscores, which the format requires. A histogram's name gains
    /// `_seconds_total`, because what is being reported is the sum of its measurements and
    /// a name that did not say so would invite somebody to read it as a latency.
    /// </remarks>
    private static string Exported(string name, Instrument? instrument)
    {
        var escaped = name.Replace('.', '_');

        return instrument is Histogram<double> or Histogram<long>
            ? escaped + "_seconds_total"
            : escaped;
    }

    private void Record<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
        where T : struct
    {
        var value = Convert.ToDouble(measurement, CultureInfo.InvariantCulture);
        var key = Key(instrument, tags);

        lock (_gate)
        {
            _instruments[instrument.Name] = instrument;
            _totals[key] = _totals.GetValueOrDefault(key) + value;
        }
    }

    /// <summary>
    /// The instrument's name plus its tags, as one dictionary key.
    /// </summary>
    /// <remarks>
    /// Tags are sorted, because the same measurement recorded with its tags in a different
    /// order is the same series — and without sorting it would become two, which is the
    /// kind of fault that only appears on a graph months later.
    /// </remarks>
    private static string Key(
        Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
        {
            return instrument.Name;
        }

        var written = new List<string>(tags.Length);

        foreach (var tag in tags)
        {
            written.Add($"{tag.Key}=\"{tag.Value}\"");
        }

        written.Sort(StringComparer.Ordinal);

        return $"{instrument.Name}{Separator}{string.Join(',', written)}";
    }

    private static (string Name, string? Labels) Split(string key)
    {
        var at = key.IndexOf(Separator);

        return at < 0 ? (key, null) : (key[..at], key[(at + 1)..]);
    }

    public void Dispose() => _listener?.Dispose();
}
