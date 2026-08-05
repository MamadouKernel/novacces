using NovAcces.Infrastructure.Persistence;
using Xunit;

namespace NovAcces.UnitTests.Security;

/// <summary>
/// IsValidBackupFileName est la SEULE porte d'entrée acceptée pour construire
/// un chemin disque à partir d'un nom fourni par l'appelant (téléchargement
/// d'une sauvegarde, DatabaseAdminEndpoints.cs) — ces tests couvrent
/// spécifiquement la protection contre la traversée de chemin.
/// </summary>
public sealed class PgDumpDatabaseBackupServiceTests
{
    [Theory]
    [InlineData("novacces_20260805_103045.dump")]
    [InlineData("novacces_20260101_000000.dump")]
    public void IsValidBackupFileName_AcceptsGeneratedFormat(string fileName)
    {
        Assert.True(PgDumpDatabaseBackupService.IsValidBackupFileName(fileName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32\\config\\sam")]
    [InlineData("novacces_20260805_103045.dump/../../secrets.env")]
    [InlineData("/etc/passwd")]
    [InlineData("novacces_20260805_103045.dump.exe")]
    [InlineData("autre_20260805_103045.dump")]
    [InlineData("novacces_2026080_103045.dump")]
    [InlineData("novacces_20260805_103045")]
    public void IsValidBackupFileName_RejectsAnythingOutsideTheGeneratedFormat(string? fileName)
    {
        Assert.False(PgDumpDatabaseBackupService.IsValidBackupFileName(fileName));
    }
}
