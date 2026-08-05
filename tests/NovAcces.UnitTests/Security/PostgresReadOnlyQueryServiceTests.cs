using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Persistence;
using Xunit;

namespace NovAcces.UnitTests.Security;

/// <summary>
/// Couvre la barrière 1 (rejet du texte) de la console SQL SuperAdmin — la
/// plus faible des trois défenses (voir PostgresReadOnlyQueryService), mais
/// la seule testable sans connexion PostgreSQL réelle. Les barrières 2
/// (sous-requête) et 3 (transaction READ ONLY) sont vérifiées par
/// construction PostgreSQL, pas par ce service.
/// </summary>
public sealed class PostgresReadOnlyQueryServiceTests
{
    [Theory]
    [InlineData("SELECT 1", "SELECT 1")]
    [InlineData("  SELECT 1  ", "SELECT 1")]
    [InlineData("SELECT 1;", "SELECT 1")]
    [InlineData("SELECT 1;  ", "SELECT 1")]
    public void NormalizeSingleStatement_AcceptsOneStatement_TrimsTrailingSemicolon(string input, string expected)
    {
        Assert.Equal(expected, PostgresReadOnlyQueryService.NormalizeSingleStatement(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeSingleStatement_RejectsEmpty(string? input)
    {
        Assert.Throws<InvalidReadOnlyQueryException>(() => PostgresReadOnlyQueryService.NormalizeSingleStatement(input));
    }

    [Theory]
    [InlineData("SELECT 1; DROP TABLE visits;")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("DELETE FROM visits; SELECT 1")]
    public void NormalizeSingleStatement_RejectsStackedStatements(string input)
    {
        Assert.Throws<InvalidReadOnlyQueryException>(() => PostgresReadOnlyQueryService.NormalizeSingleStatement(input));
    }
}
