# AI-Act-Readiness Art. 50 — Transparenzpflichten

**Stand:** 27.08.2026 · **Prüfbasis:** Branch `feature/connector-abstraction-phase1`, Client-Stand
`C:\projects\Pia.Wpf` (`main`, 9ea2908c) · **Anlass:** Screening neo42 vom 03.07.2026, § 9.1

Die Transparenzpflichten des Art. 50 der Verordnung (EU) 2024/1689 (KI-Verordnung) gelten seit
**02.08.2026**. Dieser Vermerk ist die Bewertung, die das Screening (§ 9.1) als Nachweisleistung
verlangt. Er stellt den geprüften Quellstand fest, ordnet ihn den Absätzen des Art. 50 zu und benennt,
was offen ist.

> Dieser Vermerk ist eine technisch-organisatorische Bewertung und **ersetzt keine anwaltliche
> Beratung**. Zwei Einordnungen — die Rollenfrage (§ 2) und die Reichweite der Ausnahme in Art. 50
> Abs. 2 Satz 2 (§ 3.2) — sind Rechtsfragen und als solche gekennzeichnet.

---

## 1. Zusammenfassung

| Punkt | Absatz | Status |
|---|---|---|
| 3.1 Erkennbare Interaktion mit einem KI-System | Abs. 1 | **LÜCKE** — auf der Website erkennbar, in der Anwendung nicht benannt |
| 3.2 Kennzeichnung maschinell erzeugter Inhalte | Abs. 2 | **LÜCKE** — Text und Sprachausgabe tragen keine Markierung |
| 3.3 Rollen je Funktion und Tarif | Art. 3 Nr. 3/4 | **BEWERTET** — Einordnung vorgeschlagen, Bestätigung durch Recht offen |
| 3.4 Zweckbestimmung | Abs. 1, Art. 3 Nr. 12 | **TEILWEISE** — vorhanden, aber verstreut und nicht als Zweckbestimmung ausgewiesen |
| 3.5 Modell- und Anbieterinformationen | Abs. 1, Abs. 5 | **LÜCKE, gut schließbar** — serverseitig erfasst, im Client nicht angezeigt |
| 3.6 Menschliche Kontrolle | — (Art. 14 mittelbar) | **ERFÜLLT** |
| 3.7 Protokollierung und Löschfristen | — (Art. 12/19 mittelbar) | **ERFÜLLT und belegt** |
| 3.8 Beschwerdeprozess | Abs. 5 mittelbar | **TEILWEISE** — Kanäle existieren, sind aber nicht als KI-Beschwerdeweg ausgewiesen |

**Kein Punkt ist einschlägig für Art. 50 Abs. 3** (Emotionserkennung, biometrische Kategorisierung) —
Pia verarbeitet keine biometrischen Daten zu diesen Zwecken. **Art. 50 Abs. 4** (Deepfakes, Texte zur
Unterrichtung der Öffentlichkeit) richtet sich an Betreiber und trifft neo42 nicht in der Rolle als
Anbieter; er kann Kunden treffen und gehört deshalb in die Betreiberhinweise (§ 4, Maßnahme M5).

---

## 2. Rolleneinordnung

Art. 3 Nr. 3 KI-VO definiert den **Anbieter** als denjenigen, der ein KI-System entwickelt und unter
eigenem Namen in Verkehr bringt; Art. 3 Nr. 4 den **Betreiber** als denjenigen, der es unter eigener
Verantwortung verwendet.

| Bestandteil | Rolle neo42 | Begründung aus dem Quellstand |
|---|---|---|
| Anwendung Pia (Client) | **Anbieter** | Wird von neo42 entwickelt und unter eigenem Namen verbreitet |
| Pia Cloud / AI-Proxy | **Anbieter** des Systems; Auftragsverarbeiter der Daten | `src/Pia.Server/AiProxy/` vermittelt Anfragen an Fremdmodelle, erzeugt selbst keine Modellausgabe |
| Basismodelle (OpenAI, Anthropic, Mistral, Google …) | **kein Anbieter** | Die GPAI-Pflichten nach Art. 53 ff. liegen bei den Modellanbietern |
| BYOK-Betrieb | **Anbieter** des Systems, **nicht** Betreiber | Der Kunde stellt den Modellzugang; die Systemverantwortung bleibt bei neo42 |
| Selbst gehosteter Server beim Kunden | **Anbieter**; der Kunde ist **Betreiber** | Die Betreiberpflichten (u. a. Art. 50 Abs. 4) treffen dann den Kunden |
| MCP-Erweiterungen Dritter | **offen** | Hängt an E3 — je nach Ausgestaltung liegt eine eigene Systemgrenze vor |

**Rechtsfrage, nicht abschließend bewertbar:** Ob der AI-Proxy neo42 über die Anbieterrolle hinaus in
eine GPAI-nahe Pflichtenstellung bringt, weil er Systemprompts, Personas und Guardrails auf die
Fremdmodellausgabe anwendet (`AiProxyService.cs`, Zeile ~206 setzt einen zusammengesetzten Prompt).
Diese Einordnung ist mit **E3** zu klären; sie ist die einzige Rollenfrage, die dieser Vermerk **nicht**
schließt.

---

## 3. Die acht Prüfpunkte

### 3.1 Erkennbare Interaktion mit einem KI-System (Art. 50 Abs. 1)

**Feststellung.** Auf der Website ist es durchgängig erkennbar: `index.html` und `llms.txt` bezeichnen
Pia als „AI assistant", die Preis- und Feature-Seiten nennen Modelle und Anbieter.

**In der Anwendung nicht.** Der Fenstertitel lautet `Pia - {Mode} (v{Version})`
(`MainWindowViewModel.cs:85`) und benennt kein KI-System. In den lokalisierten Oberflächentexten
(`Resources/Strings/CommonStrings*.resx`) kommt weder „KI-Assistent" noch „AI assistant" vor —
**0 Treffer in der deutschen und der englischen Fassung**.

**Bewertung.** Die Ausnahme des Art. 50 Abs. 1 („offensichtlich … für eine angemessen informierte,
aufmerksame und umsichtige natürliche Person") ist hier gut vertretbar: Wer eine Anwendung installiert,
die als KI-Assistent vermarktet wird, Modelle auswählt und Anbieterschlüssel einträgt, weiß, womit er
interagiert. Vertretbar ist aber nicht nachgewiesen, und der Nachweis kostet fast nichts — deshalb
Maßnahme **M1**.

**Nicht offensichtlich ist ein Sonderfall:** der Meeting-Assistent
(`Views/MeetingAttendeeOverlay.xaml`) tritt gegenüber Dritten auf, die Pia nicht installiert haben und
die Vermarktung nie gesehen haben. Für diese Personen greift die Offensichtlichkeitsausnahme nicht.

### 3.2 Kennzeichnung maschinell erzeugter Inhalte (Art. 50 Abs. 2)

**Feststellung.** Pia erzeugt in drei Formen synthetische Inhalte:

| Form | Erzeugung | Kennzeichnung im Quellstand |
|---|---|---|
| Freier Text (Assistent, Chat) | Fremdmodell über AI-Proxy oder BYOK | **keine** |
| Textumformulierung (Optimize-Templates) | dito | **keine** |
| Sprachausgabe (TTS) | **lokal**, Piper (`Services/TtsService.cs`, `PiperSharp`) | **keine** |

Art. 50 Abs. 2 verlangt eine Markierung **in maschinenlesbarem Format** und die Erkennbarkeit als
künstlich erzeugt.

**Bewertung — zwei getrennte Fälle.**

- **Textumformulierung**: Die Ausnahme in Art. 50 Abs. 2 Satz 2 („unterstützende Funktion für
  Standardbearbeitung … verändert die Eingabedaten nicht wesentlich") greift hier plausibel. Das
  Standardtemplate ist „Clarity & Grammar"; das ist Standardbearbeitung im Wortsinn. **Rechtsfrage:**
  Wo die Grenze zwischen Standardbearbeitung und wesentlicher Veränderung liegt, ist je Template zu
  bewerten — bei freier Umformulierung nach Stilbeschreibung
  (`AiProxyService.cs:275 ff.` erzeugt Prompts aus Stilbeschreibungen) ist die Ausnahme **nicht**
  tragfähig.
- **Freier Text und Sprachausgabe**: Die Ausnahme greift nicht. Hier besteht eine Kennzeichnungspflicht.
  Die Verordnung begrenzt sie auf das technisch Machbare („effective, interoperable, robust and reliable
  as far as technically feasible"); für lokal erzeugte Piper-Sprachausgabe existiert derzeit kein
  etablierter Wasserzeichenstandard, was zu dokumentieren ist, aber die Pflicht nicht aufhebt.

**Das ist die gewichtigste Lücke dieses Vermerks** — Maßnahmen **M2** und **M3**.

### 3.3 Rollen je Funktion und Tarif

Siehe § 2. Die Tarifdimension ist **derzeit gegenstandslos**: Pro und Enterprise sind auf
`pricing.html` als *Coming Soon* ausgewiesen, produktiv ist nur Free. Die Rolleneinordnung ist damit
heute tarifunabhängig. Mit dem ersten zahlungspflichtigen Tarif ist sie erneut zu prüfen — dieselbe
Zäsur, die R11 als Launch-Blocker beschreibt.

### 3.4 Zweckbestimmung (Art. 3 Nr. 12)

**Feststellung.** Eine Zweckbestimmung im Sinne der Verordnung — der vom Anbieter beabsichtigte
Verwendungszweck einschließlich Kontext und Bedingungen — existiert inhaltlich, aber verteilt:
Feature-Seiten, `llms-full.txt`, `docs.pia-ai.de` und das Designdokument
`docs/plans/2026-01-07-ai-assistant-design-v2.md`. Kein Dokument weist sich als Zweckbestimmung aus,
und keines benennt die **Grenzen** des bestimmungsgemäßen Gebrauchs.

**Bewertung.** Für Art. 50 ist die Zweckbestimmung mittelbar relevant (sie bestimmt den Kontext, in dem
die Offensichtlichkeitsausnahme zu beurteilen ist). Eigenständig relevant wird sie mit den
Betreiberhinweisen. Maßnahme **M4**.

### 3.5 Modell- und Anbieterinformationen (Art. 50 Abs. 1, Abs. 5)

**Feststellung — und der einzige Punkt, wo die Daten schon da sind.** Serverseitig wird je Antwort
festgehalten, welches Modell sie erzeugt hat: `src/Pia.Server/Models/TokenUsageLog.cs` führt das Feld
`Model` neben `InputTokens`, `OutputTokens`, `CachedTokens` und `FinishReason`.

Im Client wird es **nicht angezeigt**. In `src/Pia.Wpf/Views/` gibt es **keinen Treffer** für
`ProviderName`; `Models/AssistantMessage.cs` trägt kein Modell- oder Anbieterfeld. Die Anzeige endet
bei der globalen Anbieterauswahl — welches Modell eine **einzelne** Antwort erzeugt hat, ist in der
Oberfläche nicht ablesbar.

**Bewertung.** Das ist die am günstigsten zu schließende Lücke: Die Information existiert, ist
persistiert und muss nur an die Nachricht gebunden und angezeigt werden. Sie ist zugleich der stärkste
Beleg für Abs. 5 („klar und deutlich unterscheidbar, spätestens zum Zeitpunkt der ersten Interaktion").
Maßnahme **M6**.

### 3.6 Menschliche Kontrolle

**Status: ERFÜLLT.** Kein Ergebnis wird ohne Nutzerhandlung wirksam. Belegt im Quellstand:

- Optimierungen werden angezeigt, bevor sie übernommen werden (`OptimizeView.xaml`,
  `OptimizeViewModel.cs`).
- Guardrails leiten riskante Anfragen auf geschützte Modelle statt sie automatisch auszuführen
  (`AiProxyService.cs`, `ChatOutcome.cs`).
- Gruppenrichtlinien und verwaltete Personas sind administrativ steuerbar
  (`AdminClientPolicyService.cs`, `AdminManagedPersonaService.cs`).
- Kein Pfad im Server führt zu einer automatisierten Entscheidung mit Rechtswirkung gegenüber
  Betroffenen.

### 3.7 Protokollierung und Löschfristen

**Status: ERFÜLLT und belegt.** Drei getrennte Protokolle mit unterschiedlichen Fristen:

| Protokoll | Inhalt | Frist | Fundstelle |
|---|---|---|---|
| Audit-Log | `Timestamp`, `ActorUserId`, `ActorEmail`, `EventType`, `IpAddress`, `UserAgent`, `Metadata`, `CorrelationId` — **keine Prompt- oder Antwortinhalte** | `Audit:RetentionDays`, Standard **365 Tage** | `Models/AuditLogEntry.cs`, `Services/AuditLogCleanupService.cs:42` |
| Token-Nutzung | `Model`, Tokenzahlen, `FinishReason`, `TemplateId` — **keine Inhalte** | keine eigene Frist konfiguriert | `Models/TokenUsageLog.cs` |
| Operator-Zuweisungen | Zuweisungsdaten, zeitweise im Klartext | `Operators:RetentionDays` **30 Tage**, Klartext `PlaintextRetentionHours` **72 Stunden** | `Configuration/OperatorOptions.cs:66,78` |

**Ein Befund am Rande:** Für `TokenUsageLog` ist **keine** Löschfrist konfiguriert, während Audit-Log
und Operator-Zuweisungen je eine haben. Die Tabelle enthält keine Inhalte, wohl aber ein
personenbeziehbares Nutzungsprofil über `UserId`, `Model` und `CreatedAt`. Das ist kein Art.-50-Punkt,
sondern einer nach Art. 5 Abs. 1 lit. e DSGVO, und gehört ins Löschkonzept aus R11. Maßnahme **M7**.

### 3.8 Beschwerdeprozess

**Feststellung.** Es existieren drei Kanäle: `kontakt@neo42.de` (Impressum),
`datenschutz@neo42.de` (Datenschutzerklärung) und der externe Datenschutzbeauftragte (WaPo Compliance
GbR, Tim Walter, Osnabrück, § 1 der Datenschutzerklärung).

**Bewertung.** Für datenschutzrechtliche Anliegen ist das ausreichend. **Nicht** vorhanden ist ein als
solcher ausgewiesener Weg für **KI-bezogene** Beschwerden — falsche Ausgaben, unangemessene Antworten,
Fehlverhalten eines Guardrails. Solche Meldungen sind heute nicht von einer Datenschutzanfrage zu
unterscheiden und werden nicht gesondert erfasst. Maßnahme **M8**.

---

## 4. Maßnahmen

| Nr. | Maßnahme | Aufwand | Abhängig von | Frist |
|---|---|---|---|---|
| **M1** | KI-Hinweis in der Anwendung sichtbar machen: Fenstertitel oder Kopfzeile des Assistenten benennt das KI-System; im Meeting-Overlay ein Hinweis für **Dritte** | gering | — | 15.09.2026 |
| **M2** | Kennzeichnung freier Textausgaben festlegen und umsetzen (Vorschlag: Metadatenfeld an der Nachricht + Kennzeichnung beim Export/Kopieren) | mittel | — | 30.09.2026 |
| **M3** | Sprachausgabe: Prüfen, welche Kennzeichnung für Piper technisch machbar ist; Ergebnis **auch bei negativem Befund** dokumentieren — das ist der Nachweis „soweit technisch machbar" | mittel | — | 30.09.2026 |
| **M4** | Zweckbestimmung als eigenes Dokument, mit Grenzen des bestimmungsgemäßen Gebrauchs | gering | E4 | 30.09.2026 |
| **M5** | Betreiberhinweise für selbst hostende Kunden, einschließlich Art. 50 Abs. 4 (Deepfake- und Öffentlichkeitstexte) | gering | E3 | mit R11 |
| **M6** | Modell und Anbieter je Antwort im Client anzeigen — das Feld `TokenUsageLog.Model` existiert bereits serverseitig | gering | — | 15.09.2026 |
| **M7** | Löschfrist für `TokenUsageLog` festlegen und im Löschkonzept aufnehmen | gering | — | mit R11 |
| **M8** | KI-Beschwerdeweg ausweisen (eigene Adresse oder gekennzeichnetes Formular) und Eingänge gesondert erfassen | gering | — | 30.09.2026 |

**Reihenfolge.** M1 und M6 zuerst — beide sind klein, beide betreffen die Transparenz zum Zeitpunkt der
ersten Interaktion, und M6 nutzt Daten, die es schon gibt. M2 und M3 sind die inhaltlich schwierigen
Punkte und sollten gemeinsam entschieden werden. M5 und M7 laufen mit dem Vertragswerk aus R11.

---

## 5. Was dieser Vermerk nicht klärt

- Die GPAI-nahe Pflichtenstellung des AI-Proxys (§ 2, letzter Absatz) — **E3**.
- Die Grenze der Ausnahme in Art. 50 Abs. 2 Satz 2 je Optimize-Template (§ 3.2) — Rechtsfrage,
  Zuarbeit Produkt.
- Die Tarifdimension der Rolleneinordnung (§ 3.3) — gegenstandslos bis zum ersten zahlungspflichtigen
  Tarif, dann **E4**.

Diese drei Punkte sind der Grund, weshalb der Vermerk als Bewertung mit benannten Lücken vorliegt und
nicht als Freigabe. Die Überfälligkeit seit 02.08.2026 ist damit adressiert: Es liegt eine
dokumentierte Bewertung vor, und die acht Prüffragen des Screenings sind einzeln beantwortet.

---

## 6. Quellen

- Verordnung (EU) 2024/1689 (KI-Verordnung), Art. 3 Nr. 3, 4, 12; Art. 50 Abs. 1–5; Kapitel IV,
  anwendbar ab 02.08.2026
- Screening neo42 vom 03.07.2026, § 9.1
- Maßnahmen- und Empfehlungsdokument vom 27.08.2026, § 6 — am 29.08.2026 in die Checklisten überführt und
  gelöscht (`git log -- docs/dsb/2026-08-27-website-screening-massnahmen.md`)
- Quellstand: `src/Pia.Server/` (AiProxy, Models, Services, Configuration), `src/Pia.Web/wwwroot/`,
  Client `C:\projects\Pia.Wpf` (`main`, 9ea2908c)
