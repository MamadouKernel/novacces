/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "./Components/**/*.{razor,html,cshtml}",
    "./wwwroot/index.html",
  ],
  // Classes composées dynamiquement dans le C#/Razor (non littérales) — à
  // conserver explicitement pour qu'elles ne soient pas purgées.
  safelist: [
    "badge-valid", "badge-pending", "badge-used", "badge-onsite",
    "badge-revoked", "badge-expired", "badge-degraded", "badge-security",
    "v-granted", "v-denied", "v-security",
    "dot-on", "dot-off",
    "tile-warn", "tile-alert", "tile-in",
    "filter", "on", "row-security",
    "result-line", "ok", "ko", "dup",
  ],
  theme: {
    extend: {
      colors: {
        // ── Palette exacte de la maquette validée le 22/07/2026 ──
        // Marque : navy pétrole ; accent signature : ambre « sûreté ».
        brand: {
          50:  "#eef3f5",
          100: "#d5e1e6",
          200: "#aec6cf",
          300: "#7ca1ae",
          400: "#4f7d8d",
          500: "#2f5c6d",
          600: "#123649", // --navy2 (survol)
          700: "#0e2a3a", // --navy  (primaire)
          800: "#0b2230",
          900: "#081a24",
          950: "#05121a",
        },
        // Ambre hi-vis — accent d'action et d'alerte (--amber / --amber-ink).
        amber: {
          50:  "#fff8e8",
          100: "#fff3d6",
          200: "#ffe2a8",
          300: "#ffcf6e",
          400: "#f9ba33",
          500: "#f5a300", // --amber
          600: "#c98200",
          700: "#7a5200", // --amber-ink
          800: "#663f00",
          900: "#4d2f00",
        },
        // Neutres « papier » de la maquette.
        ink:   "#10161d",
        ink2:  "#3c4854",
        muted: "#6b7784",
        paper: "#f4f5f2",
        line:  "#dce0db",
        // Couleurs d'état (pastilles pi-*).
        ok:    "#0f8a3d",
        okbg:  "#e4f4e9",
        no:    "#c92a2a",
        nobg:  "#fbe9e9",
        onsite:   "#1b5e9e",
        onsitebg: "#e3eef9",
        exp:   "#7b3fa0",
        expbg: "#f3e8fb",
      },
      fontFamily: {
        // Public Sans (texte), Barlow Condensed (titres), IBM Plex Mono (codes).
        sans: [
          "Public Sans", "Inter", "ui-sans-serif", "system-ui",
          "-apple-system", "Segoe UI", "Roboto", "Arial", "sans-serif",
        ],
        display: [
          "Barlow Condensed", "ui-sans-serif", "system-ui",
          "Segoe UI", "Arial", "sans-serif",
        ],
        mono: [
          "IBM Plex Mono", "ui-monospace", "SFMono-Regular",
          "Menlo", "Consolas", "monospace",
        ],
      },
      boxShadow: {
        card: "0 1px 2px 0 rgb(15 23 42 / 0.04), 0 8px 24px -8px rgb(15 23 42 / 0.10)",
        pop: "0 20px 50px -12px rgb(15 23 42 / 0.25)",
      },
      keyframes: {
        "fade-in-up": {
          "0%": { opacity: "0", transform: "translateY(6px)" },
          "100%": { opacity: "1", transform: "translateY(0)" },
        },
      },
      animation: {
        "fade-in-up": "fade-in-up .25s ease-out",
      },
    },
  },
  plugins: [require("@tailwindcss/forms")],
};
