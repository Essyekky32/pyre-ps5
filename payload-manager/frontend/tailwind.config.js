/** @type {import('tailwindcss').Config} */
export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  theme: {
    extend: {
      colors: {
        /* PYRE palette - kept under the original ps-* names so every existing
           utility class (145 uses of ps-blue alone) re-skins without touching
           a single component. Values mirror frontend/src/index.css :root. */
        'ps-blue': '#ff7a18',
        'ps-blue-glow': 'rgba(255, 122, 24, 0.35)',
        'ps-black': '#0c0a09',
        'ps-surface': '#150f0c',
        'ps-card': 'rgba(26, 16, 9, 0.85)',
        'ps-border': 'rgba(255, 174, 99, 0.12)',
        /* explicit ember/cream tokens for new PYRE-specific decoration */
        'pyre-ember': '#ff7a18',
        'pyre-ember-light': '#ffae63',
        'pyre-cream': '#f6ece3',
        'pyre-muted': '#c9a893',
        'pyre-brown': '#3a1d0a',
      },
      borderRadius: {
        'ps-xl': '1rem',
        'ps-2xl': '1.5rem',
        'ps-3xl': '2rem',
      },
      fontFamily: {
        'ps5': ["Inter", "system-ui", "sans-serif"],
      }
    },
  },
  plugins: [],
}
