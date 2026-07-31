using Microsoft.Extensions.Options;
using NovAcces.Infrastructure;
using Xunit;

namespace NovAcces.UnitTests;

public class BusinessDayServiceTests
{
    private static BusinessDayService Create(params string[] holidays) =>
        new(Options.Create(new BusinessDayOptions { Holidays = holidays.ToList() }));

    [Fact]
    public void Weekend_IsNotBusinessDay()
    {
        var service = Create();
        // 2026-07-25 = samedi, 2026-07-26 = dimanche.
        Assert.False(service.IsBusinessDay(new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero)));
        Assert.False(service.IsBusinessDay(new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void ConfiguredHoliday_IsNotBusinessDay()
    {
        // 2026-08-07 = Fête nationale (vendredi) déclarée fériée.
        var service = Create("2026-08-07");
        Assert.False(service.IsBusinessDay(new DateTimeOffset(2026, 8, 7, 10, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void OrdinaryWeekday_IsBusinessDay()
    {
        var service = Create("2026-08-07");
        // 2026-07-24 = vendredi ordinaire.
        Assert.True(service.IsBusinessDay(new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero)));
    }
}
