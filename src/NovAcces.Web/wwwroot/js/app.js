// Déclenche le téléchargement d'un contenu texte (ex. export CSV du journal).
window.novaccesDownload = (filename, text) => {
    const blob = new Blob([text], { type: 'text/csv;charset=utf-8;' });
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
