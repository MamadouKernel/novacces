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
        // Bleu « sûreté » — confiant, sobre, professionnel.
        brand: {
          50: "#eff6ff",
          100: "#dbeafe",
          200: "#bfdbfe",
          300: "#93c5fd",
          400: "#60a5fa",
          500: "#3b82f6",
          600: "#2563eb",
          700: "#1d4ed8",
          800: "#1e40af",
          900: "#1e3a8a",
          950: "#172554",
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
