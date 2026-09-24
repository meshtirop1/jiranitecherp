using JiranisokoTech.Application.Notices;
using JiranisokoTech.Infrastructure.Mail;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Infrastructure.Notices;

/// <summary>
/// The configured address this application answers from.
/// </summary>
/// <remarks>
/// Configured rather than read from a request, for the reason MailOptions already gives: the
/// messages that matter most are sent by the outbox dispatcher, which has no request to read.
/// A link pointing at localhost because a background job had no better idea is a link nobody
/// can use.
///
/// When nothing is configured the path is handed back unchanged. That is honest — a relative
/// link is useless in an inbox and an invented one is worse, because it looks right and goes
/// somewhere else.
/// </remarks>
public sealed class WhereThisLives(IOptions<MailOptions> options) : IWhereThisLives
{
    public string? Reachable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var root = options.Value.BaseAddress?.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(root) || !path.StartsWith('/'))
        {
            return path;
        }

        return root + path;
    }
}
