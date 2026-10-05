# Adding a language: the web catalog

The button, the picker and contents view, and the settings page. Each user sees the language
Jellyfin is displayed in for them, falling back string by string to English.

1. Copy `en.json` to `<code>.json`, the code a **lower-case** language tag: `de`, `pt-br`.
2. Translate the values, never the keys. Keep every `{placeholder}`; move it where your grammar
   needs it.
3. Leave out what you are not sure of: a missing string shows in English, never blank.
4. Check it: `python3 tools/i18n_status.py <code>`, then `python3 -m unittest discover -s tests`.

Plain text only, no HTML. The full guide, with the words that have a fixed meaning and how to see
your translation in a running Jellyfin: [`docs/i18n.md`](../../../../docs/i18n.md).
