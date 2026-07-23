namespace NovAcces.Domain.Exceptions;

/// <summary>Violation d'une règle métier NovAccès (jamais une erreur technique).</summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}
