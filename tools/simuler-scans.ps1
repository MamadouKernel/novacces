#requires -Version 7
<#
    Simule une série de scans (entrées / sorties / refus) via l'API NovAcces,
    pour peupler les dashboards en démo — SANS l'app MAUI.

    - Aucune donnée sensible en dur : la clé API du terminal est lue depuis les
      user-secrets du projet Api.
    - Idempotent : provisionne le site et crée le compte hôte de démo si besoin.

    Prérequis : l'API doit tourner (F5 dans Visual Studio) sur https://localhost:54980.
    Usage :  pwsh ./tools/simuler-scans.ps1
#>

$ErrorActionPreference = 'Stop'
$api  = 'https://localhost:54980'
$site = 'sicopa'   # le site qui a une clé API de terminal en user-secrets
$irm  = @{ SkipCertificateCheck = $true }
function J($o) { $o | ConvertTo-Json -Depth 20 -Compress }

# --- Clé API du terminal (depuis les user-secrets, jamais en dur) ---
$secret = dotnet user-secrets list --project src/NovAcces.Api 2>$null |
          Select-String 'ApiKeys:Terminals:0:Key = (.+)'
$apiKey = if ($secret) { $secret.Matches.Groups[1].Value.Trim() } else { $null }
if (-not $apiKey) {
    Write-Error "Clé API terminal introuvable dans les user-secrets (ApiKeys:Terminals:0:Key)."
    exit 1
}

# --- 1) Admin ---
$admin = Invoke-RestMethod @irm -Uri "$api/api/auth/login" -Method Post -ContentType 'application/json' `
    -Body (J @{ Email = 'admin@novacces.local'; Password = 'ChangeMoi!2026Dev' })
$A = @{ Authorization = "Bearer $($admin.accessToken)" }

# --- 2) Site + compte hôte de démo (idempotent) ---
try { Invoke-RestMethod @irm -Uri "$api/api/admin/sites" -Method Post -Headers $A -ContentType 'application/json' -Body (J @{ SiteId = $site }) | Out-Null } catch {}
$hoteEmail = "hote.demo@$site.local"; $hotePwd = 'Hote!2026Demo'
try { Invoke-RestMethod @irm -Uri "$api/api/auth/register" -Method Post -Headers $A -ContentType 'application/json' `
        -Body (J @{ Email = $hoteEmail; Password = $hotePwd; DisplayName = 'Hôte Démo'; Role = 'Hote'; SiteId = $site }) | Out-Null } catch {}
$hote = Invoke-RestMethod @irm -Uri "$api/api/auth/login" -Method Post -ContentType 'application/json' `
    -Body (J @{ Email = $hoteEmail; Password = $hotePwd })
$H = @{ Authorization = "Bearer $($hote.accessToken)" }

# --- 3) Créer des visites + capturer les QR ---
$noms = 'Amara Traoré', 'Bakary Coulibaly', 'Chantal Kouassi', 'Fatou Bamba', 'Ismaël Ouattara'
$qrs = @()
foreach ($n in $noms) {
    $v = Invoke-RestMethod @irm -Uri "$api/api/visits/" -Method Post -Headers $H -ContentType 'application/json' `
        -Body (J @{ VisitorName = $n; VisitorCompany = 'SIPRA'; Motif = 'Démonstration'; Mode = 'Unique';
                    ScheduledAt = (Get-Date).ToUniversalTime().ToString('o'); PlannedDurationMinutes = 60;
                    VisitorPhone = $null; VisitorEmail = $null })
    $qrs += [pscustomobject]@{ Nom = $n; Qr = $v.signedQrPayload }
}

# --- 4) Scanner : entrée pour tous, 2 sorties, 1 doublon (= refus/sécurité) ---
function Scan($qr, $dir) {
    Invoke-RestMethod @irm -Uri "$api/api/scan/" -Method Post -Headers @{ 'X-Api-Key' = $apiKey } `
        -ContentType 'application/json' -Body (J @{ SignedQrPayload = $qr; Direction = $dir; AgentId = "poste-$dir" })
}

Write-Host "`n--- Scans simulés ($site) ---" -ForegroundColor Cyan
foreach ($q in $qrs) { $r = Scan $q.Qr 'Entry'; "  ENTREE  {0,-20} -> {1}" -f $q.Nom, $r.verdictCode }
$r = Scan $qrs[0].Qr 'Exit';  "  SORTIE  {0,-20} -> {1}" -f $qrs[0].Nom, $r.verdictCode
$r = Scan $qrs[1].Qr 'Exit';  "  SORTIE  {0,-20} -> {1}" -f $qrs[1].Nom, $r.verdictCode
$r = Scan $qrs[2].Qr 'Entry'; "  DOUBLON {0,-20} -> {1}" -f $qrs[2].Nom, $r.verdictCode

Write-Host "`nTerminé. Connecte-toi en Sûreté (site '$site') pour voir le flux, la courbe et les présents." -ForegroundColor Green
