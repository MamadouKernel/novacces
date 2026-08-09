// Déclenche le téléchargement d'un contenu texte (ex. export CSV du journal,
// codes de récupération 2FA). mimeType est optionnel (défaut : CSV, pour ne
// pas changer le comportement des appels existants).
window.novaccesDownload = (filename, text, mimeType) => {
    const blob = new Blob([text], { type: mimeType || 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};

// Copie un texte dans le presse-papiers (ex. codes de récupération 2FA).
window.novaccesCopyToClipboard = (text) => navigator.clipboard.writeText(text);

// Bascule le thème clair/sombre (mode nuit poste de garde, SuretePortal.razor).
// Fonction nommée plutôt qu'un JS.InvokeVoidAsync("eval", ...) : la CSP de
// production (script-src 'self', sans 'unsafe-eval') rejette eval() et
// plantait le circuit Blazor Server à chaque bascule.
window.novaccesToggleDarkMode = () => document.documentElement.classList.toggle('dark');

// Comme novaccesDownload, mais pour un contenu BINAIRE reçu en base64 depuis
// .NET (ex. export ZIP d'un site) — un ZIP ne peut pas transiter tel quel via
// l'interop JS, qui sérialise les chaînes en UTF-16/JSON.
window.novaccesDownloadBase64 = (filename, base64Content, mimeType) => {
    const binary = atob(base64Content);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    const blob = new Blob([bytes], { type: mimeType || 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};

// Déconnexion après inactivité : la détection tourne entièrement en JS (pas
// un round-trip serveur à chaque mouvement de souris) — seul l'écoulement du
// délai rappelle .NET, une fois.
window.novaccesIdleTimer = (() => {
    const EVENTS = ['mousemove', 'mousedown', 'keydown', 'scroll', 'touchstart', 'click'];
    let timerId = null;
    let dotNetRef = null;
    let timeoutMs = 30 * 60 * 1000;

    function reset() {
        if (timerId) clearTimeout(timerId);
        timerId = setTimeout(() => {
            if (dotNetRef) dotNetRef.invokeMethodAsync('OnIdleTimeout');
        }, timeoutMs);
    }

    function start(ref, ms) {
        stop(); // évite d'empiler des écouteurs si start() est rappelé sans stop()
        dotNetRef = ref;
        timeoutMs = ms;
        EVENTS.forEach(e => document.addEventListener(e, reset, { passive: true }));
        reset();
    }

    function stop() {
        EVENTS.forEach(e => document.removeEventListener(e, reset));
        if (timerId) clearTimeout(timerId);
        timerId = null;
        dotNetRef = null;
    }

    return { start, stop };
})();

// Alerte sonore de dépassement de durée (§7) — Web Audio API : pas de
// fichier audio à héberger, fonctionne même hors ligne. Un seul AudioContext
// réutilisé (créer un AudioContext par bip finirait par être bloqué par le
// navigateur, qui plafonne le nombre d'instances simultanées).
// Niveau 3 (événement de sécurité) : bip plus aigu, répété deux fois.
window.novaccesBeep = (level) => {
    try {
        window.__novaccesAudioCtx = window.__novaccesAudioCtx || new (window.AudioContext || window.webkitAudioContext)();
        const ctx = window.__novaccesAudioCtx;
        if (ctx.state === 'suspended') ctx.resume();

        const playTone = (startAt, freq) => {
            const osc = ctx.createOscillator();
            const gain = ctx.createGain();
            osc.type = 'sine';
            osc.frequency.value = freq;
            // Attaque/relâche courtes : évite le "clic" d'un son coupé net.
            gain.gain.setValueAtTime(0, startAt);
            gain.gain.linearRampToValueAtTime(0.25, startAt + 0.02);
            gain.gain.linearRampToValueAtTime(0, startAt + 0.28);
            osc.connect(gain).connect(ctx.destination);
            osc.start(startAt);
            osc.stop(startAt + 0.3);
        };

        const now = ctx.currentTime;
        if (level >= 3) {
            playTone(now, 880);
            playTone(now + 0.35, 880);
        } else {
            playTone(now, 660);
        }
    } catch (err) {
        console.warn('Bip de dépassement indisponible:', err);
    }
};

// Notifications bureau système & WebPush (PWA)
window.novaccesPush = (() => {
    async function initServiceWorker() {
        if ('serviceWorker' in navigator) {
            try {
                await navigator.serviceWorker.register('/service-worker.js');
            } catch (err) {
                console.warn('Service Worker non enregistré:', err);
            }
        }
    }

    async function requestPermission() {
        if ('Notification' in window) {
            const result = await Notification.requestPermission();
            return result === 'granted';
        }
        return false;
    }

    function showNotification(title, body, icon) {
        if ('Notification' in window && Notification.permission === 'granted') {
            new Notification(title, {
                body: body,
                icon: icon || '/favicon.svg',
                tag: 'sigasacces-notify'
            });
        }
    }

    // applicationServerKey attend un Uint8Array, la clé VAPID publique arrive
    // en base64url depuis l'API — conversion standard (MDN).
    function urlBase64ToUint8Array(base64String) {
        const padding = '='.repeat((4 - (base64String.length % 4)) % 4);
        const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
        const raw = atob(base64);
        const bytes = new Uint8Array(raw.length);
        for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
        return bytes;
    }

    // Abonnement WebPush (§7, alerte de dépassement même onglet fermé) :
    // réutilise l'abonnement navigateur existant s'il y en a déjà un (ex.
    // reconnexion), sinon en crée un nouveau, puis l'enregistre côté API.
    // Idempotent — sûr à rappeler à chaque connexion.
    async function subscribe(apiBase, vapidPublicKey, accessToken) {
        if (!('serviceWorker' in navigator) || !('PushManager' in window) || !vapidPublicKey) return false;
        try {
            const reg = await navigator.serviceWorker.ready;
            let sub = await reg.pushManager.getSubscription();
            if (!sub) {
                sub = await reg.pushManager.subscribe({
                    userVisibleOnly: true,
                    applicationServerKey: urlBase64ToUint8Array(vapidPublicKey),
                });
            }
            const json = sub.toJSON();
            const response = await fetch(`${apiBase}/api/push/subscribe`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${accessToken}` },
                body: JSON.stringify({ endpoint: json.endpoint, keys: json.keys }),
            });
            return response.ok;
        } catch (err) {
            console.warn('Abonnement WebPush indisponible:', err);
            return false;
        }
    }

    initServiceWorker();

    return { requestPermission, showNotification, subscribe };
})();
