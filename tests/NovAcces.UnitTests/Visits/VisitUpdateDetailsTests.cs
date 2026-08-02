using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using NovAcces.Domain.Exceptions;
using Xunit;

namespace NovAcces.UnitTests.Visits;

/// <summary>
/// Correction d'une erreur de saisie AVANT l'arrivée du visiteur — cas
/// d'usage remonté par le client : "je me trompe sur les infos, puis-je les
/// corriger ?". Restreint aux demandes VALID et pas encore arrivées : au-delà,
/// la correction doit passer par une révocation + nouvelle demande.
/// </summary>
public class VisitUpdateDetailsTests
{
    private static Visit CreateUniqueVisit(DateTimeOffset scheduledAt, DateTimeOffset now) =>
        Visit.Create("Jean-Marc Kouassi", "CFAO Motors", "Livraison", "host-1",
            AccessMode.Unique, scheduledAt, plannedDurationMinutes: 60,
            visitorPhone: "+2250700000000", visitorEmail: "jean@example.com", isExcluded: false, now: now);

    [Fact]
    public void UpdateVisitorDetails_ValidAndNotYetOnSite_Succeeds()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now.AddHours(2), now: now);

        visit.UpdateVisitorDetails("Jean-Marc Kouassy", "CFAO Motors CI", "Livraison urgente",
            "+2250700000001", "jean.kouassy@example.com");

        Assert.Equal("Jean-Marc Kouassy", visit.VisitorName);
        Assert.Equal("CFAO Motors CI", visit.VisitorCompany);
        Assert.Equal("Livraison urgente", visit.Motif);
        Assert.Equal("+2250700000001", visit.VisitorPhone);
        Assert.Equal("jean.kouassy@example.com", visit.VisitorEmail);

        // Ni le jeton de QR ni le statut ne doivent bouger : le QR déjà émis
        // reste valable tel quel après une simple correction de coordonnées.
        Assert.Equal(VisitStatus.Valid, visit.Status);
    }

    [Fact]
    public void UpdateVisitorDetails_VisitorAlreadyOnSite_Throws()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now);
        visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now, isOnExclusionList: false);
        Assert.True(visit.IsOnSite);

        var ex = Assert.Throws<DomainException>(() =>
            visit.UpdateVisitorDetails("Autre Nom", "Autre Société", "Autre motif", null, null));

        Assert.Contains("pas encore arrivée", ex.Message);
        // La tentative refusée ne doit rien avoir modifié.
        Assert.Equal("Jean-Marc Kouassi", visit.VisitorName);
    }

    [Fact]
    public void UpdateVisitorDetails_RevokedVisit_Throws()
    {
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now.AddHours(2), now: now);
        visit.Revoke("host-1", now);

        Assert.Throws<DomainException>(() =>
            visit.UpdateVisitorDetails("Autre Nom", "Autre Société", "Autre motif", null, null));
    }

    [Fact]
    public void UpdateVisitorDetails_ConsumedAfterFullCycle_Throws()
    {
        // Mode Unique déjà entré ET sorti : Status passe à Consumed, IsOnSite
        // redevient false — la correction doit rester bloquée malgré tout,
        // ce n'est plus une demande "pas encore arrivée".
        var now = DateTimeOffset.UtcNow;
        var visit = CreateUniqueVisit(scheduledAt: now, now: now);
        visit.Scan(CheckpointDirection.Entry, isBusinessDay: true, now, isOnExclusionList: false);
        visit.Scan(CheckpointDirection.Exit, isBusinessDay: true, now.AddMinutes(30), isOnExclusionList: false);
        Assert.False(visit.IsOnSite);
        Assert.Equal(VisitStatus.Consumed, visit.Status);

        Assert.Throws<DomainException>(() =>
            visit.UpdateVisitorDetails("Autre Nom", "Autre Société", "Autre motif", null, null));
    }
}
