using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure;

/// <summary>
/// Jours fériés configurables globalement et, si nécessaire, par site.
/// Les dates sont fournies au format ISO yyyy-MM-dd.
/// </summary>
public sealed class BusinessDayOptions
{
    public List<string> Holidays { get; set; } = new();
    public Dictionary<string, List<string>> SiteHolidays { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BusinessDayService : IBusinessDayService
{
    private readonly HashSet<DateOnly> _holidays;
    private readonly IReadOnlyDictionary<string, HashSet<DateOnly>> _siteHolidays;
    private readonly ICurrentTenant? _tenant;

    public BusinessDayService(IOptions<BusinessDayOptions> options)
        : this(options, null)
    {
    }

    public BusinessDayService(IOptions<BusinessDayOptions> options, ICurrentTenant? tenant)
    {
        _holidays = Parse(options.Value.Holidays);
        _siteHolidays = options.Value.SiteHolidays
            .ToDictionary(kvp => kvp.Key, kvp => Parse(kvp.Value), StringComparer.OrdinalIgnoreCase);
        _tenant = tenant;
    }

    public bool IsBusinessDay(DateTimeOffset date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        var day = DateOnly.FromDateTime(date.UtcDateTime);
        if (_tenant?.IsResolved == true
            && _siteHolidays.TryGetValue(_tenant.SiteId, out var configured)
            && configured.Contains(day))
            return false;

        return !_holidays.Contains(day);
    }

    private static HashSet<DateOnly> Parse(IEnumerable<string>? values) =>
        (values ?? Enumerable.Empty<string>())
            .Select(value => DateOnly.TryParse(value, out var date) ? (DateOnly?)date : null)
            .Where(date => date is not null)
            .Select(date => date!.Value)
            .ToHashSet();
}