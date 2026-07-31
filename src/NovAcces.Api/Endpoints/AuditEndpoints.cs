using System.Text;
using Microsoft.EntityFrameworkCore;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Identity;
using NovAcces.Shared.Auth;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Consultation et extraction des journaux d'audit. Le journal métier est
/// per-site pour la Sûreté/Admin ; le journal transversal de toutes les requêtes
/// API est global et réservé au SuperAdmin.
/// </summary>
public static class AuditEndpoints
{
    public static RouteGroupBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit").WithTags("Audit")
            .RequireAuthorization("ManageExclusions");

        group.MapGet("/application", async (
            NovAccesIdentityDbContext db,
            int? limit,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? siteId,
            string? actor,
            CancellationToken ct) =>
        {
            IQueryable<ApplicationAuditEntry> query = FilterApplicationAudit(db.ApplicationAudit.AsNoTracking(), from, to, siteId, actor);
            query = query.OrderByDescending(e => e.Timestamp);
            if (limit is { } requested)
                query = query.Take(Math.Clamp(requested, 1, 100_000));

            var entries = await query
                .Select(e => new ApplicationAuditDto(
                    e.Id, e.Actor, e.Method, e.Path, e.StatusCode,
                    e.SiteId, e.IpAddress, e.Timestamp))
                .ToListAsync(ct);
            return Results.Ok(entries);
        })
        .RequireAuthorization(NovAccesRoles.SuperAdmin)
        .WithName("ListApplicationAuditEntries")
        .WithSummary("Journal global de toutes les requêtes API (SuperAdmin uniquement).");

        group.MapGet("/application.csv", async (
            NovAccesIdentityDbContext db,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? siteId,
            string? actor,
            int? limit,
            CancellationToken ct) =>
        {
            IQueryable<ApplicationAuditEntry> query = FilterApplicationAudit(db.ApplicationAudit.AsNoTracking(), from, to, siteId, actor);
            query = query.OrderByDescending(e => e.Timestamp);
            if (limit is { } requested)
                query = query.Take(Math.Clamp(requested, 1, 1_000_000));

            var entries = await query.ToListAsync(ct);
            var csv = new StringBuilder();
            csv.AppendLine("Id;Timestamp;Actor;Method;Path;StatusCode;SiteId;IpAddress");
            foreach (var entry in entries)
            {
                csv.Append(Csv(entry.Id.ToString("D"))).Append(';')
                    .Append(Csv(entry.Timestamp.ToString("O"))).Append(';')
                    .Append(Csv(entry.Actor)).Append(';')
                    .Append(Csv(entry.Method)).Append(';')
                    .Append(Csv(entry.Path)).Append(';')
                    .Append(entry.StatusCode).Append(';')
                    .Append(Csv(entry.SiteId)).Append(';')
                    .Append(Csv(entry.IpAddress)).AppendLine();
            }

            var bytes = Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(csv.ToString()))
                .ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", "novacces-audit.csv");
        })
        .RequireAuthorization(NovAccesRoles.SuperAdmin)
        .WithName("ExportApplicationAuditCsv")
        .WithSummary("Exporte toute la traçabilité API en CSV (SuperAdmin uniquement).");

        group.MapGet("/", async (IAdminAuditLog audit, int? limit, CancellationToken ct) =>
        {
            var entries = await audit.GetRecentAsync(Math.Clamp(limit ?? 100, 1, 500), ct);
            var dto = entries
                .Select(e => new AdminAuditDto(
                    e.Id, e.Actor, e.Action.ToString(), e.TargetId, e.Detail, e.Timestamp))
                .ToList();
            return Results.Ok(dto);
        })
        .WithName("ListAuditEntries")
        .WithSummary("Journal d'audit des actions privilégiées du site (§8.5).");

        return group;
    }

    private static IQueryable<ApplicationAuditEntry> FilterApplicationAudit(
        IQueryable<ApplicationAuditEntry> query,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? siteId,
        string? actor)
    {
        if (from is { } start)
            query = query.Where(e => e.Timestamp >= start);
        if (to is { } end)
            query = query.Where(e => e.Timestamp <= end);
        if (!string.IsNullOrWhiteSpace(siteId))
            query = query.Where(e => e.SiteId == siteId);
        if (!string.IsNullOrWhiteSpace(actor))
            query = query.Where(e => e.Actor == actor);
        return query;
    }

private static string Csv(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}