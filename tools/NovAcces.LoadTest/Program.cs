using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovAcces.Shared.Dtos;

namespace NovAcces.LoadTest;

/// <summary>
/// Harnais de test de charge REQ-FIAB-06 ("≥ 100 scans/minute soutenus").
///
/// Provisionne N terminaux RÉELS (même parcours d'enrôlement que l'app agent
/// — ticket QR + preuve de possession de clé, voir DeviceEnrollmentEndpoints)
/// et N visites (une par terminal, code de secours), puis chaque terminal
/// scanne en boucle (alternance Entrée/Sortie) pendant la durée demandée,
/// mesurant latence (p50/p95/p99) et débit réel.
///
/// Design DÉLIBÉRÉMENT multi-terminaux : /api/scan est limité à 30 req/min
/// PAR (IP, terminal) — un client unique qui hammer plafonnerait
/// artificiellement à 30/min, ce qui ne mesurerait rien d'utile. Un site réel
/// a plusieurs postes physiques distincts ; ce harnais les simule tels quels.
///
/// Usage :
///   dotnet run --project tools/NovAcces.LoadTest -- \
///     --base-url https://localhost:54980 --site sicopa \
///     --admin-email admin@novacces.local --admin-password "..." \
///     --terminals 5 --duration-seconds 120
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var opts = Options.Parse(args);

        using var handler = new HttpClientHandler
        {
            // Dev local uniquement (certificat auto-signé Kestrel) — voir --insecure-tls.
            ServerCertificateCustomValidationCallback = opts.InsecureTls
                ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                : null,
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(opts.BaseUrl) };

        Console.WriteLine($"== Test de charge REQ-FIAB-06 — {opts.Terminals} terminaux, {opts.DurationSeconds}s, cible {opts.BaseUrl} ==");

        Console.WriteLine("Connexion admin...");
        var adminToken = await LoginAsync(http, opts.AdminEmail, opts.AdminPassword);

        Console.WriteLine($"Provisionnement du site « {opts.Site} » (idempotent)...");
        await EnsureSiteAsync(http, adminToken, opts.Site);

        Console.WriteLine("Compte hôte de test (idempotent)...");
        var hostEmail = $"loadtest.hote@{opts.Site}.local";
        var hostPassword = "LoadTest!2026Charge";
        await EnsureHostAsync(http, adminToken, hostEmail, hostPassword, opts.Site);
        var hostToken = await LoginAsync(http, hostEmail, hostPassword);

        Console.WriteLine($"Provisionnement de {opts.Terminals} terminal(aux) réel(s) (enrôlement complet)...");
        var terminals = new List<(string ApiKey, string ManualCode)>();
        for (var i = 0; i < opts.Terminals; i++)
        {
            var label = $"loadtest-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{i}";
            var terminalId = await CreateTerminalAsync(http, adminToken, label, opts.Site);
            var ticket = await CreateEnrollmentTicketAsync(http, adminToken, terminalId);
            var apiKey = await ActivateDeviceAsync(http, ticket);
            var manualCode = await CreateVisitAsync(http, hostToken, $"ChargeTest-{i}");
            terminals.Add((apiKey, manualCode));
            Console.WriteLine($"  Terminal {i + 1}/{opts.Terminals} prêt.");
        }

        var targetTotalPerMin = terminals.Count * opts.RequestsPerMinutePerTerminal;
        Console.WriteLine(
            $"Lancement de la charge ({terminals.Count} terminaux en parallèle, {opts.RequestsPerMinutePerTerminal}/min "
            + $"chacun, {opts.DurationSeconds}s, cible agrégée ~{targetTotalPerMin}/min)...");
        var results = new System.Collections.Concurrent.ConcurrentBag<RequestResult>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(opts.DurationSeconds));

        var stopwatch = Stopwatch.StartNew();
        var workers = terminals.Select(t => RunWorkerAsync(http, t.ApiKey, t.ManualCode, opts.RequestsPerMinutePerTerminal, results, cts.Token));
        await Task.WhenAll(workers);
        stopwatch.Stop();

        var report = BuildReport(results.ToList(), stopwatch.Elapsed, terminals.Count);
        Console.WriteLine();
        Console.WriteLine(report);

        if (!string.IsNullOrWhiteSpace(opts.OutputPath))
        {
            await File.WriteAllTextAsync(opts.OutputPath, report);
            Console.WriteLine($"\nRapport écrit dans {opts.OutputPath}");
        }

        return 0;
    }

    private static async Task RunWorkerAsync(
        HttpClient http, string apiKey, string manualCode, int requestsPerMinute,
        System.Collections.Concurrent.ConcurrentBag<RequestResult> results, CancellationToken ct)
    {
        // Cadence un terminal réel : un agent ne scanne pas en boucle serrée,
        // et surtout, un hammering sans pause ne mesurerait que la limite de
        // débit (30/min/terminal) au lieu du débit soutenable réel — voir la
        // première tentative sans pacing, gardée dans le commit pour mémoire.
        var interval = TimeSpan.FromMilliseconds(60_000.0 / Math.Max(1, requestsPerMinute));
        var direction = "Entry";
        while (!ct.IsCancellationRequested)
        {
            var iterationStart = Stopwatch.StartNew();
            var sw = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/scan/manual-code");
                request.Headers.Add("X-Api-Key", apiKey);
                request.Content = JsonContent.Create(new ScanManualCodeRequestDto(manualCode, direction));
                using var response = await http.SendAsync(request, ct);
                sw.Stop();
                results.Add(new RequestResult(sw.Elapsed, (int)response.StatusCode, null));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                sw.Stop();
                results.Add(new RequestResult(sw.Elapsed, 0, ex.GetType().Name));
            }

            direction = direction == "Entry" ? "Exit" : "Entry";

            var remaining = interval - iterationStart.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                try { await Task.Delay(remaining, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static string BuildReport(List<RequestResult> results, TimeSpan elapsed, int terminalCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Résultat — test de charge REQ-FIAB-06");
        sb.AppendLine();
        sb.AppendLine($"- Date : {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- Terminaux simulés : {terminalCount}");
        sb.AppendLine($"- Durée réelle : {elapsed.TotalSeconds:F1} s");
        sb.AppendLine($"- Requêtes totales : {results.Count}");

        if (results.Count == 0)
        {
            sb.AppendLine("- Aucune requête n'a abouti.");
            return sb.ToString();
        }

        var throughputPerMin = results.Count / elapsed.TotalMinutes;
        sb.AppendLine($"- Débit observé : **{throughputPerMin:F1} requêtes/minute** ({results.Count / elapsed.TotalSeconds:F2}/s)");

        var byStatus = results.GroupBy(r => r.StatusCode).OrderBy(g => g.Key);
        sb.AppendLine();
        sb.AppendLine("### Répartition par statut HTTP");
        foreach (var g in byStatus)
        {
            var label = g.Key == 0 ? $"Exception ({g.First(r => r.ExceptionType != null).ExceptionType})" : g.Key.ToString();
            sb.AppendLine($"- {label} : {g.Count()} ({100.0 * g.Count() / results.Count:F1} %)");
        }

        var latenciesMs = results.Where(r => r.StatusCode != 0).Select(r => r.Latency.TotalMilliseconds).OrderBy(x => x).ToList();
        if (latenciesMs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Latence (requêtes ayant obtenu une réponse HTTP)");
            sb.AppendLine($"- p50 : {Percentile(latenciesMs, 0.50):F0} ms");
            sb.AppendLine($"- p95 : {Percentile(latenciesMs, 0.95):F0} ms");
            sb.AppendLine($"- p99 : {Percentile(latenciesMs, 0.99):F0} ms");
            sb.AppendLine($"- max : {latenciesMs[^1]:F0} ms");
        }

        var rateLimited = results.Count(r => r.StatusCode == 429);
        if (rateLimited > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"⚠ {rateLimited} requête(s) refusée(s) par la limite de débit (429) — attendu si le débit "
                + $"visé dépasse 30 req/min par terminal (politique \"sensitive\", voir Program.cs API). "
                + $"Plafond théorique site pour ce test : {terminalCount} terminaux × 30/min = {terminalCount * 30}/min.");
        }

        return sb.ToString();
    }

    private static double Percentile(List<double> sortedValues, double p)
    {
        if (sortedValues.Count == 1) return sortedValues[0];
        var index = p * (sortedValues.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper) return sortedValues[lower];
        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * (index - lower);
    }

    // ---- Provisionnement (réutilise les DTOs réels de l'API, aucune duplication de contrat) ----

    private static async Task<string> LoginAsync(HttpClient http, string email, string password)
    {
        var resp = await http.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(email, password));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<LoginResponseDto>()
            ?? throw new InvalidOperationException("Réponse de connexion vide.");
        return body.AccessToken;
    }

    private static async Task EnsureSiteAsync(HttpClient http, string adminToken, string siteId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/sites");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        request.Content = JsonContent.Create(new { SiteId = siteId });
        using var resp = await http.SendAsync(request);
        // Idempotent : un site déjà provisionné répond souvent en erreur — ignoré volontairement.
    }

    private static async Task EnsureHostAsync(HttpClient http, string adminToken, string email, string password, string siteId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        request.Content = JsonContent.Create(new RegisterUserRequestDto(email, password, "Hôte — Test de charge", "Hote", siteId));
        using var resp = await http.SendAsync(request);
        // Idempotent : compte déjà existant -> erreur ignorée volontairement.
    }

    private static async Task<Guid> CreateTerminalAsync(HttpClient http, string adminToken, string label, string siteId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/terminals");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        request.Content = JsonContent.Create(new CreateTerminalRequestDto(label, new[] { siteId }));
        using var resp = await http.SendAsync(request);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CreateTerminalResponseDto>()
            ?? throw new InvalidOperationException("Réponse de création de terminal vide.");
        return body.Id;
    }

    private static async Task<string> CreateEnrollmentTicketAsync(HttpClient http, string adminToken, Guid terminalId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/terminals/{terminalId:D}/enrollment-ticket");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        using var resp = await http.SendAsync(request);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<EnrollmentTicketResponseDto>()
            ?? throw new InvalidOperationException("Réponse de ticket d'enrôlement vide.");

        using var doc = JsonDocument.Parse(body.QrPayload);
        return doc.RootElement.GetProperty("ticket").GetString()
            ?? throw new InvalidOperationException("Ticket absent du QrPayload.");
    }

    private static async Task<string> ActivateDeviceAsync(HttpClient http, string ticket)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceInstanceId = Guid.NewGuid().ToString("D");
        var publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        var message = Encoding.UTF8.GetBytes($"{ticket}|{deviceInstanceId}");
        var signature = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var proofSignature = Base64UrlEncode(signature);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/device-enrollments/activate");
        request.Content = JsonContent.Create(new DeviceEnrollmentRequestDto(ticket, deviceInstanceId, publicKeyPem, proofSignature));
        using var resp = await http.SendAsync(request);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DeviceEnrollmentActivationDto>()
            ?? throw new InvalidOperationException("Réponse d'activation vide.");
        return body.ApiKey;
    }

    private static async Task<string> CreateVisitAsync(HttpClient http, string hostToken, string visitorName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/visits");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", hostToken);
        request.Content = JsonContent.Create(new CreateVisitRequestDto(
            visitorName, "Test de charge", "REQ-FIAB-06", "Unique",
            DateTimeOffset.UtcNow, 480, null, null));
        using var resp = await http.SendAsync(request);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CreateVisitResponseDto>()
            ?? throw new InvalidOperationException("Réponse de création de visite vide.");
        return body.ManualCode ?? throw new InvalidOperationException("Code de secours absent de la réponse.");
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed record RequestResult(TimeSpan Latency, int StatusCode, string? ExceptionType);

internal sealed class Options
{
    public string BaseUrl { get; private init; } = "https://localhost:54980";
    public string Site { get; private init; } = "sicopa";
    public string AdminEmail { get; private init; } = "admin@novacces.local";
    public string AdminPassword { get; private init; } = "";
    public int Terminals { get; private init; } = 5;
    public int DurationSeconds { get; private init; } = 120;
    public int RequestsPerMinutePerTerminal { get; private init; } = 25;
    public bool InsecureTls { get; private init; } = true;
    public string? OutputPath { get; private init; }

    public static Options Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                continue;

            var key = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            map[key] = hasValue ? args[++i] : "true";
        }

        return new Options
        {
            BaseUrl = map.GetValueOrDefault("base-url", "https://localhost:54980").TrimEnd('/'),
            Site = map.GetValueOrDefault("site", "sicopa"),
            AdminEmail = map.GetValueOrDefault("admin-email", "admin@novacces.local"),
            AdminPassword = map.GetValueOrDefault("admin-password", ""),
            Terminals = int.TryParse(map.GetValueOrDefault("terminals"), out var t) ? Math.Clamp(t, 1, 50) : 5,
            DurationSeconds = int.TryParse(map.GetValueOrDefault("duration-seconds"), out var d) ? Math.Clamp(d, 5, 3600) : 120,
            RequestsPerMinutePerTerminal = int.TryParse(map.GetValueOrDefault("requests-per-minute-per-terminal"), out var r) ? Math.Clamp(r, 1, 30) : 25,
            InsecureTls = !map.ContainsKey("no-insecure-tls"),
            OutputPath = map.GetValueOrDefault("output"),
        };
    }
}
