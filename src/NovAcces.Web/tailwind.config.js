/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "./Components/**/*.{razor,html,cshtml}",
    "./wwwroot/index.html",
  ],
  // Classes composées dynamiquement dans le C#/Razor (non littérales) — à
  // conserver explicitement pour qu'elles ne soient pas purgées.
  safelist: [
    "badge-valid", "badge-revoked", "badge-expired",
    "v-granted", "v-denied", "v-security",
    "dot-on", "dot-off", "tile-warn", "tile-alert",
  ],
  theme: {
    extend: {
      colors: {
        // Bleu nuit « sûreté » — autorité, confiance, sérieux institutionnel.
        // Registre visuel des forces de sûreté ; ne concurrence pas les
        // couleurs d'état (vert accordé / ambre refus / rouge incident).
        brand: {
          50: "#eef3ff",
          100: "#dbe4fe",
          200: "#bccdfc",
          300: "#90aaf8",
          400: "#5f7ff0",
          500: "#3b5be2",
          600: "#2544ad",
          700: "#1e3a8a",
          800: "#1b3172",
          900: "#16265a",
          950: "#0e1836",
        },
      },
      fontFamily: {
        sans: [
          "Inter", "ui-sans-serif", "system-ui", "-apple-system",
          "Segoe UI", "Roboto", "Helvetica Neue", "Arial", "sans-serif",
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
