using JiranisokoTech.Application.Abstractions;

namespace JiranisokoTech.Infrastructure;

/// <summary>The real clock. UTC everywhere; a time zone is a display concern.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}
