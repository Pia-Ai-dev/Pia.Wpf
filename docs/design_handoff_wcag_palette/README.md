# Handoff: Farbpalette nach WCAG 1.4.3 — was davon WPF betrifft

**Stand:** 27.08.2026 · **Quelle:** Befund R12 der Website-Prüfung im Server-Repo
(`docs/dsb/2026-08-27-palette-wcag-1.4.3-vorschlag.md`), dort umgesetzt und nachgemessen.

Die Marketing-Website hatte vier Kontrastverstöße nach WCAG 1.4.3 (Grenzwert 4,5:1 für Fließtext,
3:1 für großen Text ab 24 px bzw. 18,66 px fett). Dieselbe Palette wird in diesem Repo an **einer**
Stelle wiederverwendet, deshalb dieser Handoff.

## Betroffen ist genau eine Datei

`src/Pia.Wpf/Services/MarkdownExportService.cs` — das eingebettete Stylesheet für den
Markdown-nach-HTML-Export. Es spiegelt die Web-Tokens unter eigenen Namen
(`--ink`, `--ink-soft`, `--ink-muted`, `--accent`).

**Nicht betroffen:** `Resources/Theme/PiaStyles.xaml`, `Resources/Styles/MarkdownStyles.xaml` und
der übrige XAML-Bestand — dort steht keiner der geprüften Hexwerte. Die WPF-Oberfläche selbst ist
von diesem Befund also nicht berührt.

## Der Verstoß

`--accent: #0078D4` wird als **Schriftfarbe** benutzt (Überschriften, Links, Wortmarke) und erreicht
auf `--bg: #FAFAF7` nur **4,33:1**. Im dunklen Schema ist nichts zu tun: `#60CDFF` liegt bei 10,87:1.

| Element | Größe | hell | dunkel | Schwelle | Befund |
|---|---|---|---|---|---|
| `a` | 17 px | **4,33** | 10,87 | 4,5 | **verfehlt** |
| `h4` | 16,8 px | **4,33** | 10,87 | 4,5 | **verfehlt** |
| `.pia-wordmark` | 17,6 px / 600 | **4,33** | 10,87 | 4,5 | **verfehlt** |
| `h3` | 20 px / 600 | **4,33** | 10,87 | 4,5 bzw. 3,0 | **Grenzfall** — 600 ist nicht eindeutig „fett" |
| `h2` | 24,8 px | 4,33 | 10,87 | 3,0 | erfüllt (großer Text) |
| `h1` | 35,2 px | 4,33 | 10,87 | 3,0 | erfüllt (großer Text) |

Alles andere im Stylesheet ist in Ordnung: `body`/`blockquote` auf `--ink-soft` 9,82:1,
`.pia-footer-link` 4,59:1, das Symbol im `.theme-toggle` 4,80:1, `--ink-soft` auf der getönten
Zitatfläche `#EEF4F5` 9,24:1.

## Vorher / Nachher

| Variable | heute hell | **neu hell** | heute dunkel | neu dunkel | Wirkung hell |
|---|---|---|---|---|---|
| **`--accent-text`** *(neu)* | — | **`#005A9E`** | — | `#60CDFF` | 4,33 → **6,79** |
| `--accent` *(bleibt Füll- und Rahmenfarbe)* | `#0078D4` | `#0078D4` | `#60CDFF` | `#60CDFF` | unverändert |
| `--ink-muted` | `#78716C` | `#6B6560` | `#A8A29E` | `#A8A29E` | 4,59 → **5,49** *(optional)* |
| `--ink` | `#1C1917` | unverändert | `#E7E5E4` | unverändert | — |
| `--ink-soft` | `#44403C` | unverändert | `#D6D3D1` | unverändert | — |

**Warum ein eigenes `--accent-text` statt `--accent` zu ändern:** `--accent` trägt im selben
Stylesheet den Zitatstreifen (`border-left: 3px solid`), die Rahmenfarbe beim Hover des
Theme-Schalters, `--selection` und `--shadow`. Als Fläche und Rahmen ist `#0078D4` einwandfrei
(1.4.11 verlangt 3:1, erreicht 4,33). Nur die Schriftverwendung muss dunkler werden — das ist
dieselbe Trennung, die die Website jetzt über das Token `accent-text` führt.

`--ink-muted` ist **optional**: 4,59:1 erfüllt AA. Der Vorschlag darüber schafft Reserve, falls
`--bg` je aufgehellt wird. Auf der Website wurde er mitgenommen.

## Umsetzung

Im `Styles`-Konstanten-String, `:root` und `html.dark`:

```css
:root {
    --accent:      #0078D4;   /* unverändert — Flächen, Rahmen, Auswahl, Schatten */
    --accent-text: #005A9E;   /* neu — nur Schrift; #0078D4 erreichte 4,33:1 auf #FAFAF7 */
    --ink-muted:   #6B6560;   /* war #78716C — optional, Reserve gegenüber 4,59:1 */
}
html.dark {
    --accent-text: #60CDFF;   /* dunkles Schema war nie betroffen */
}
```

Dann die drei Schriftverwendungen umhängen — `h1, h2, h3, h4`, `a` und `.pia-wordmark` von
`var(--accent)` auf `var(--accent-text)`. `a:hover { border-bottom-color }`, `blockquote`s
`border-left` und `.theme-toggle:hover { border-color }` bleiben auf `var(--accent)`.

## Was aus dem Website-Befund hier *nicht* gilt

- **Ein Hexwert für beide Schemata** (der schwerste Web-Verstoß): Das Export-Stylesheet hat für
  `--ink-muted` von Anfang an getrennte Werte je Schema (`#78716C` / `#A8A29E`). Die Falle, in die
  die Website gelaufen ist, existiert hier nicht.
- **Transluzente Kopfzeile:** kein Gegenstück im Export.
- **Badge-Farbe:** `#EA580C` kommt hier nicht vor.

## Gegenprobe

Nach der Änderung reicht eine Messung von `#005A9E` gegen `#FAFAF7` (erwartet 6,79:1) und
`#60CDFF` gegen `#0C0C0C` (10,87:1). Ein Export mit Überschriften, Links, Zitat und Tabelle in
beiden Schemata zeigt den Rest.
