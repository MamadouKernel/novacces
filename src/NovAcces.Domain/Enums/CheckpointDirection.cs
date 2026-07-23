namespace NovAcces.Domain.Enums;

/// <summary>
/// Sens du poste de contrôle au moment du scan (cf. démonstration du 22/07/2026 :
/// un poste "Entrée" ne peut pas produire de sortie, et inversement).
/// </summary>
public enum CheckpointDirection
{
    Entry = 0,
    Exit = 1
}
