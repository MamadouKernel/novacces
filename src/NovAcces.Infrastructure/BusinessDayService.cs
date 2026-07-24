using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure;

/// <summary>
/// Jours fériés du site (section "BusinessDays" de la configuration), au format
/// "yyyy-MM-dd". Par site, la liste des jours fériés ivoiriens est paramétrable
/// (fériés à date fixe et fériés mobiles saisis chaque année).
/// </summary>
public sealed class BusinessDayOptions
{
    public List<string> Holidays { get; set; } = new();
}

public sealed class BusinessDayService : IBusinessDayService
{
    private readonly HashSet<DateOnly> _holidays;

    public BusinessDayService(IOptions<BusinessDayOptions> options)
    {
        _holidays = options.Value.Holidays
            .Select(h => DateOnly.TryParse(h, out var d) ? (DateOnly?)d : null)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .ToHashSet();
    }

    public bool IsBusinessDay(DateTimeOffset date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        return !_holidays.Contains(DateOnly.FromDateTime(date.UtcDateTime));
    }
}
