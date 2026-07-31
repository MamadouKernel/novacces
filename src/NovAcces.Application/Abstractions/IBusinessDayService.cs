namespace NovAcces.Application.Abstractions;

/// <summary>
/// Détermine si une date est un jour ouvré, pour le mode « 30 jours ouvrés »
/// (REQ-F-05). Un jour ouvré n'est ni un week-end, ni un jour férié du site.
/// </summary>
public interface IBusinessDayService
{
    bool IsBusinessDay(DateTimeOffset date);
}
