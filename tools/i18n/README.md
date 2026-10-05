# Adding a language: the notes catalog

The `.REMOVED.txt` note that replaces a program or macro document in an archive. Used by the
helper script (`--language <code>`, or `LANG`); archives the plugin builds get English notes.

1. Copy `en.json` to `<code>.json`, the code a **lower-case** language tag: `de`, `pt-br`.
2. Translate the values, never the keys. Keep every `{placeholder}` and the `\n` line breaks, and
   keep lines under about 95 characters.
3. Check it: `python3 tools/i18n_status.py <code>`, then build the sample library and read a note
   (the commands are in the guide).

The full guide: [`docs/i18n.md`](../../docs/i18n.md).
