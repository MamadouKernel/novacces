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
    // Le journal global prend une ligne par requête API : il se compte vite en
    // millions. Pagination réelle (Skip/Take + Count) plutôt qu'un simple
    // plafond « N plus récents » — sinon rien au-delà de la première page
    // n'est jamais consultable depuis l'écran. Au-delà d'une page raisonnable,
    // c'est une extraction base (voir l'export CSV, toujours plafonné large).
    private const int DefaultAuditPageSize = 20;
    private const int MaxAuditPageSize = 200;
    private const int MaxAuditCsvRows = 100_000;

    public static RouteGroupBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit").WithTags("Audit")
            .RequireAuthorization();

        group.MapGet("/application", async (
            NovAccesIdentityDbContext db,
            int? page,
            int? pageSize,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? siteId,
            string? actor,
            CancellationToken ct) =>
        {
            var (p, size) = PaginationQuery.Normalize(page, pageSize, DefaultAuditPageSize, MaxAuditPageSize);
            IQueryable<ApplicationAuditEntry> query = FilterApplicationAudit(db.ApplicationAudit.AsNoTracking(), from, to, siteId, actor);
            query = query.OrderByDescending(e => e.Timestamp);

            var total = await query.CountAsync(ct);
            var entries = await query
                .Skip((p - 1) * size)
                .Take(size)
                .Select(e => new ApplicationAuditDto(
                    e.Id, e.Actor, e.Method, e.Path, e.StatusCode,
                    e.SiteId, e.IpAddress, e.Timestamp))
                .ToListAsync(ct);
            return Results.Ok(new PagedResultDto<ApplicationAuditDto>(entries, p, size, total));
        })
        .RequireAuthorization(NovAccesRoles.SuperAdmin)
        .WithName("ListApplicationAuditEntries")
        .WithSummary("Journal global de toutes les requêtes API, paginé (SuperAdmin uniquement).");

        // Export STREAMÉ : les lignes sont écrites au fur et à mesure dans la
        // réponse, jamais accumulées dans une liste puis dans un StringBuilder
        // puis dans un tableau d'octets (l'ancienne version matérialisait trois
        // fois le même contenu, avec un plafond à un million de lignes).
        group.MapGet("/application.csv", (
            HttpResponse response,
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
            query = query.Take(Math.Clamp(limit ?? MaxAuditCsvRows, 1, MaxAuditCsvRows));

            response.ContentType = "text/csv; charset=utf-8";
            response.Headers.ContentDisposition = "attachment; filename=\"novacces-audit.csv\"";

            return Results.Stream(async stream =>
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                await writer.WriteLineAsync("Id;Timestamp;Actor;Method;Path;StatusCode;SiteId;IpAddress");

                await foreach (var entry in query.AsAsyncEnumerable().WithCancellation(ct))
                {
                    await writer.WriteLineAsync(
                        $"{Csv(entry.Id.ToString("D"))};{Csv(entry.Timestamp.ToString("O"))};"
                        + $"{Csv(entry.Actor)};{Csv(entry.Method)};{Csv(entry.Path)};"
                        + $"{entry.StatusCode};{Csv(entry.SiteId)};{Csv(entry.IpAddress)}");
                }
            }, "text/csv; charset=utf-8");
        })
        .RequireAuthorization(NovAccesRoles.SuperAdmin)
        .WithName("ExportApplicationAuditCsv")
        .WithSummary("Exporte la traçabilité API en CSV, en flux (SuperAdmin uniquement).");

        group.MapGet("/", async (IAdminAuditLog audit, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, size) = PaginationQuery.Normalize(page, pageSize, DefaultAuditPageSize, MaxAuditPageSize);
            var (entries, total) = await audit.GetPagedAsync(p, size, ct);
            var dto = entries
                .Select(e => new AdminAuditDto(
                    e.Id, e.Actor, e.Action.ToString(), e.TargetId, e.Detail, e.Timestamp))
                .ToList();
            return Results.Ok(new PagedResultDto<AdminAuditDto>(dto, p, size, total));
        })
        .RequireAuthorization(NovAccesPolicies.ManageExclusions)
        .WithName("ListAuditEntries")
        .WithSummary("Journal d'audit des actions privilégiées du site, paginé (§8.5).");

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