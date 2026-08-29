# AI-Act Art. 50 — Transparenz im Pia.Wpf-Client

**Status:** Client-Maßnahmen M1, M2, M3, M6 und die Client-Seite von M8 gelandet (29.08.2026); Server-Seite von M8 per [Handoff](2026-08-29-ai-feedback-server-handoff.md) · **Owner:** Marco Altmann · **Written:** 2026-08-29 · **Origin:** [2026-08-27-ai-act-art50-readiness.md](2026-08-27-ai-act-art50-readiness.md) (Vermerk aus dem Pia-Server-Repo, hier als Schnappschuss abgelegt; die kanonische Fassung liegt dort)

Der Vermerk hat den Client gegen `main` 9ea2908c geprüft. Dieses Dokument hält fest, was der
Client-Code tatsächlich tut, was am 29.08.2026 geändert wurde, welche Kennzeichnungskonvention dabei
eingeführt wurde, und welche Negativbefunde („soweit technisch machbar", Art. 50 Abs. 2) zu
dokumentieren sind. § 7 ist der Nachtrag, der in die kanonische Fassung des Vermerks gehört.

> Technisch-organisatorische Bewertung, keine Rechtsberatung. Die Rechtsfragen sind in § 6 gesammelt.

---

## 1. Was der Vermerk über den Client anders sah

| Vermerk | Client-Stand vor dem 29.08.2026 |
|---|---|
| 3.5/M6: Modell je Antwort nicht ablesbar | Jede Antwort trug eine Fußzeile `N Tokens · Persona · Modell` (`Controls/Chat/FooterSummaryFormatter.cs`), persistiert als `SyncAssistantChatMessage.ModelName`. Der Wert war aber das **konfigurierte** `provider.ModelName`, nicht das vom Provider gemeldete Modell, und die Fußzeile entfiel ohne Usage-Daten. Der Anbieter fehlte. |
| 3.1/M1: „in der Anwendung nicht benannt" | Der Erstlauf-Assistent benennt Pia als „AI-powered desktop assistant" (`ViewStrings.resx`, `Wizard_Welcome_Description`), Seite 1 von 7. Dauerhaft (Fenstertitel, Einstellungen) fehlte die Benennung. |
| „Pia verarbeitet keine biometrischen Daten" | Die Sprecherzuordnung extrahiert Stimm-Embeddings der Meeting-Teilnehmer (`Services/LiveTranscription/SherpaEmbeddingExtractor.cs`, `AdaptiveSpeakerIdentificationService.cs`) — nur im Speicher je Sitzung, keine Persistenz; das Modell ordnet „Speaker N" anschließend Roster-Namen zu. Siehe § 6.3. |
| 3.6 nur serverseitig belegt | Der Client führt Agent-Runs und Routinen headless aus; Schreibzugriffe laufen über eine vorab erteilte Grant-Hülle (`Services/HeadlessRunLauncher.GrantEnvelope.cs`) bzw. Approval-Parks (`Services/FilesToolHandler.cs`, `PrepareWriteFile`). |
| M3 Sprachausgabe | Piper erzeugt WAV-Bytes im Speicher, Wiedergabe über NAudio (`Services/TtsService.cs`); kein Export, der Meeting-Bot tritt stumm ohne Mikrofon bei. Synthetische Sprache verlässt das Gerät nicht. |

Schärfste Lücke war der **Meeting-Bot gegenüber Dritten**: Teilnehmer sahen nur den frei editierbaren
Anzeigenamen (Standard `"{0}'s assistant"`), keinen Hinweis auf KI oder Transkription.

## 2. Umgesetzt am 29.08.2026

| Maßnahme | Umsetzung |
|---|---|
| **M1** Dritte im Meeting | Der Teams-Anzeigename erhält einen nicht editierbaren Zusatz `({KI-Suffix})` — `MeetingAttendee_DisplayName_AiSuffix`, en „AI notetaker", de „KI-Protokoll", fr „assistant IA" (`MeetingAttendeeService.WithAiSuffix`). Lange Namen werden gekürzt, damit der Zusatz die Teams-Grenze von 50 Zeichen überlebt. Der Join-Dialog zeigt den wirksamen Namen unter dem Eingabefeld (`MeetingAttendee_EffectiveDisplayName`). |
| **M1** Anwendung | Fenstertitel `Pia AI Assistant - {Mode} (v…)`. Neue Einstellungsseite **Info/About** (`Views/SettingsViews/AboutView.xaml`): Version, Herausgeber, KI-Hinweis, Links auf Impressum, Datenschutzerklärung, Dokumentation, Website (`Models/PiaLinks.cs`). |
| **M6** | Fußzeile nennt für BYOK-Provider `Anbieter · Modell`, wobei das Modell aus der Antwort (`ChatResponse.ModelId`) stammt und erst dann auf die Konfiguration zurückfällt; unabhängig von Usage-Daten. Pia Cloud wird als „Pia Cloud" genannt (Entscheidung Owner 29.08.2026, der Proxy wählt das Modell). Anbieter wird je Nachricht persistiert (`ProviderName`, SQLite-Spalte + Sync-DTO, additiv). `Models/AnswerProvenance.cs`. |
| **M2** | Maschinenlesbare Kennzeichnung aller Dateien, die Modellausgabe enthalten (§ 3): HTML-Export, Markdown-Chat-Export, Vault-Quellen (`pia-meeting/v1`, `pia-direct-transcript/v1`) und Pia-verwaltete Vault-Seiten (`VaultFrontmatter`). Sichtbar: HTML-Fußzeile „AI-generated content · Pia x.y · Anbieter · Modell", Markdown-Zeile `*AI-generated · Anbieter · Modell*` unter jeder Antwort. |
| **M3** | Sichtbarer Hinweis „KI-generierte Stimme" im Voice-Overlay während der Wiedergabe und als Badge an der Antwort, deren Vorlesen läuft. Negativbefund zur Dateimarkierung in § 4. |
| **M8** Client | Daumen hoch/runter an **Pia-Cloud-Antworten** (`AssistantMessage.IsRateable`); Daumen runter öffnet „Antwort melden" (Freitext, Antworttext optional, PII-tokenisiert wie Prompts) und sendet an `POST /api/ai-feedback` (`Services/AiFeedbackService.cs`). BYOK-Antworten haben keine Buttons — das Modell ist das des Nutzers. About-Seite nennt `entwicklung@neo42.de` als Interimsadresse für KI-Anliegen. Server-Seite: [Handoff](2026-08-29-ai-feedback-server-handoff.md). |

## 3. Kennzeichnungskonvention

Ein Vokabular für alle Formate, definiert in `Models/AiContentMarking.cs`:

| Schlüssel | Bedeutung | YAML-Frontmatter | HTML `<meta>` |
|---|---|---|---|
| `generator` | erzeugendes Programm mit Version, z. B. `Pia 1.3.1000` | `generator: Pia 1.3.1000` | `<meta name="generator" content="Pia 1.3.1000">` |
| `aiGenerated` | Datei enthält KI-erzeugten Text | `aiGenerated: true` | `<meta name="ai-generated" content="true">` |
| `aiModel` | Anbieter · Modell, wenn bekannt | (je Antwort im Fließtext) | `<meta name="ai-model" content="OpenAI · gpt-4o">` |

Die Schemata `pia-meeting/v1` und `pia-direct-transcript/v1` wurden **nicht** hochgezählt: kein Leser
prüft die Versionsnummer, die Schlüssel sind additiv, und ein Bump hätte nur Tests und Doku bewegt.
Für Text gibt es keinen etablierten Wasserzeichen- oder Provenienzstandard; C2PA/IPTC
(`digitalSourceType`) sind auf Medien zugeschnitten. Wenn die Kommission im Praxisleitfaden zu Art. 50
ein Textvokabular festlegt, ist diese eine Klasse die Stelle für die Umstellung.

## 4. Negativbefunde — „soweit technisch machbar"

- **Zwischenablage und Einfügen (Optimize, Kopieren):** Ausgabe ist reiner Text in fremde
  Anwendungen (`OptimizeViewModel`, `OutputService.PasteToPreviousWindowAsync`). Es gibt keinen
  Träger für Metadaten; Unicode-Wasserzeichen sind unzuverlässig und verändern den Text. Keine
  Kennzeichnung möglich; die Optimize-Ansicht zeigt Original und Ergebnis nebeneinander, die
  Fußzeile im Chat nennt das Modell.
- **Sprachausgabe:** Wiedergabe aus dem Speicher über die Soundkarte, keine Datei, kein Export, der
  Meeting-Bot spricht nicht. Kein Artefakt, das ein Wasserzeichen tragen könnte. Kennzeichnung
  erfolgt sichtbar in der Oberfläche (§ 2, M3).
- **Dateien, die Agent-Runs schreiben (`write_file`):** beliebige Formate (Code, Konfiguration), in
  die kein Marker eingefügt werden darf, ohne die Datei zu verändern. Jeder Schreibzugriff ist an eine
  Nutzerfreigabe oder eine vorab erteilte Grant-Hülle gebunden, und der Run-Verlauf hält fest, welche
  Dateien der Run geschrieben hat.

## 5. Zuarbeit für die Template-Frage (Vermerk § 3.2)

Built-in-Templates in `src/Pia.Shared/BuiltInTemplates.cs`:

| Template | Einordnung (Vorschlag Produkt, Rechtsfrage) |
|---|---|
| Grammar & Spelling Fix | Standardbearbeitung — Ausnahme Art. 50 Abs. 2 Satz 2 plausibel |
| Clarity & Grammar (Default) | Standardbearbeitung — plausibel |
| Business Email | Ton- und Genre-Transformation — Ausnahme zweifelhaft |
| Community Article | Genre-Transformation — zweifelhaft |
| Message to Friend | Ton-Transformation — zweifelhaft |
| C# Code Prompt | erzeugt ein neues Artefakt — Ausnahme nicht tragfähig |

Nutzerdefinierte Templates haben eine freie `StyleDescription` (`Services/SyncMapper.cs`) und lassen
sich zentral nicht einordnen. Da die Ausgabe ohnehin nicht markierbar ist (§ 4), bleibt hier nur die
Dokumentation.

## 6. Offene Entscheidungen und Rechtsfragen

### 6.1 KI-Beschwerdeweg (M8) — entschieden 29.08.2026

Gemeldet werden **nur Pia-Cloud-Antworten**: Dort hat neo42 das Modell gewählt und betrieben. Bei BYOK
ist das Modell das des Nutzers; eine Meldung an neo42 hätte keinen Adressaten, der etwas ändern könnte.
Der allgemeine Kontakt für Anliegen zum KI-System (Anbieterpflicht, unabhängig vom Modell) steht in der
About-Seite: **`entwicklung@neo42.de`**, Interimsadresse bis ein eigener Alias existiert.

| Szenario | Weg |
|---|---|
| Public Pia Cloud | Daumen/„Antwort melden" → `POST /api/ai-feedback`; Server erfasst gesondert und mailt die Interimsadresse |
| Self-hosted Server | derselbe Endpoint auf dem Kundenserver; Weiterleitung an neo42 als Server-Option, Standard an, abschaltbar |
| Nur WPF mit BYOK | keine Melde-Buttons; About-Seite nennt die Adresse |

Server-Seite: [2026-08-29-ai-feedback-server-handoff.md](2026-08-29-ai-feedback-server-handoff.md).

### 6.2 Grenze der Ausnahme je Template

Rechtsfrage; Zuarbeit in § 5.

### 6.3 Stimm-Embeddings

Der Vermerk stellt fest, Pia verarbeite keine biometrischen Daten. Der Client extrahiert
Stimm-Embeddings von Meeting-Teilnehmern zur Sprecherzuordnung (§ 1). Das sind biometrische Daten
i. S. v. Art. 4 Nr. 14 DSGVO; ob die Verarbeitung „zur eindeutigen Identifizierung" (Art. 9) erfolgt,
ist mit der Roster-Zuordnung im Meeting-Summary zu bewerten. Art. 50 Abs. 3 KI-VO (Emotionserkennung,
biometrische Kategorisierung) ist nach dieser Bewertung nicht einschlägig. Entlastend: nur im
Speicher je Sitzung, `MeetingSuppressSpeakerLabels`, Disclaimer im Overlay. **An den DSB.**

## 7. Nachtrag zum Vermerk (zur Übernahme in die kanonische Fassung)

- § 1 Tabelle, 3.5: Status **UMGESETZT (Client 29.08.2026)** — Fußzeile nennt Anbieter und
  gemeldetes Modell; Pia Cloud als „Pia Cloud".
- § 3.1: Ergänzen, dass der Erstlauf-Assistent das KI-System benennt (`Wizard_Welcome_Description`);
  Fenstertitel und Info-Seite seit 29.08.2026 dauerhaft.
- § 3.1 Sonderfall Meeting: Anzeigename trägt seit 29.08.2026 einen nicht editierbaren KI-Zusatz.
- § 3.2 Tabelle: Freier Text — Kennzeichnung in Exporten und Vault-Dateien (§ 3 hier);
  Zwischenablage/Einfügen — technisch nicht machbar, dokumentiert (§ 4 hier). Sprachausgabe —
  sichtbarer Hinweis; keine Datei, kein Artefakt (§ 4 hier).
- § 1 Satz „Pia verarbeitet keine biometrischen Daten zu diesen Zwecken": auf den Client erweitern
  (§ 6.3 hier), Bewertung durch den DSB anfordern.
- § 3.6: Client-Belege ergänzen (Grant-Hülle, Approval-Parks).
- § 4 Maßnahmen: M1, M6 erledigt; M2 für Dateiartefakte erledigt, Rest dokumentierter Negativbefund;
  M3 dokumentiert; M8 Kanalvorschlag § 6.1 hier.
