# AI-Act Transparenz im Client — Checkliste

**Status:** Schritte 1–8 und 10 gelandet 29.08.2026 (Build ohne Warnungen); 9 wartet auf den Windows-Testlauf. 11–13 werden im Pia-Server-Repo finalisiert (Owner übernimmt die Dokumente dort von Hand).
**Owner:** Marco Altmann
**Written:** 2026-08-29
**Origin:** [2026-08-29-client-transparency-readiness.md](2026-08-29-client-transparency-readiness.md) · Vermerk [2026-08-27-ai-act-art50-readiness.md](2026-08-27-ai-act-art50-readiness.md)

**Effort:** `XS` unter einem Tag, keine neuen Typen · `S` 1–2 Tage · `M` 3–5 Tage, neue Typen oder
eine neue Oberfläche · `L` eine Woche oder mehr, ein neues Subsystem.

**Value:** `High` nutzersichtbar oder ein echtes Risiko geschlossen · `Med` lohnend, keine
Schlagzeile · `Enabler` wenig eigener Wert, entsperrt ein High.

## Entscheidungstore

| # | Frage | Antwort | Folge |
|---|---|---|---|
| **G1** | Welche Adresse und welcher Transport für KI-Beschwerden je Szenario? | **Nur Pia-Cloud-Antworten**, `POST /api/ai-feedback`; Interimsadresse `entwicklung@neo42.de` (Owner 29.08.2026) | Schritt 10 gebaut; Schritt 11 per Handoff |
| **G2** | Welche Optimize-Templates fallen unter die Ausnahme Art. 50 Abs. 2 Satz 2? | **offen** — Rechtsfrage, Zuarbeit Origin § 5 | Schritt 12 ist reine Dokumentation, ändert keinen Code |
| **G3** | Pia Cloud als „Pia Cloud" oder mit Upstream-Modell nennen? | **„Pia Cloud"** (Owner 29.08.2026) | Schritt 2 fertig wie gebaut |

## Schritte

- [x] **1. KI-Zusatz am Meeting-Anzeigenamen** — `WithAiSuffix` im Service, Vorschau im Join-Dialog, Roster-Filter kennt den Zusatz, drei Sprachen. *Deps:* — · *Effort:* S · *Value:* High
- [x] **2. Fußzeile: Anbieter · gemeldetes Modell** — `AnswerProvenance`, `Finished.Provider`, `AnswerStats.Tokens` optional, `ProviderName` in SQLite und Sync-DTO. *Deps:* G3 · *Effort:* S · *Value:* High
- [x] **3. HTML-Export markieren** — `generator`/`ai-generated`/`ai-model` Meta und sichtbare Fußzeile; Aufrufer geben die Antwort-Herkunft mit. *Deps:* 2 · *Effort:* XS · *Value:* High
- [x] **4. Markdown-Chat-Export markieren** — Frontmatter plus `*AI-generated · Anbieter · Modell*` unter jeder Antwort. *Deps:* 2 · *Effort:* XS · *Value:* Med
- [x] **5. Vault-Dateien markieren** — `AiContentMarking.YamlLines()` in `pia-meeting/v1`, `pia-direct-transcript/v1`, `VaultFrontmatter.Build/BuildPreserving`; Goldens angepasst. *Deps:* — · *Effort:* XS · *Value:* Med
- [x] **6. Fenstertitel** — `Pia AI Assistant (v…)`. *Deps:* — · *Effort:* XS · *Value:* Med
- [x] **7. Info/About-Seite** — Version, Herausgeber, KI-Hinweis, Links Impressum/Datenschutz/Doku/Website; `SettingsTab.About`, AutomationIds, Playbook-Zeile. *Deps:* — · *Effort:* S · *Value:* High
- [x] **8. Hinweis auf synthetische Stimme** — Voice-Overlay und Antwort-Badge während der Wiedergabe. *Deps:* — · *Effort:* XS · *Value:* Med
- [ ] **9. Test-Gate auf Windows** — `dotnet test` mit `failed: 0`; der Mac kompiliert nur (Rebuild Debug und Release bei `0 Warnung(en)` ist erbracht). *Deps:* 1–8 · *Effort:* XS · *Value:* High
- [x] **10. Beschwerdeweg im Client** — Rate-Buttons nur an Pia-Cloud-Antworten, „Antwort melden"-Dialog mit PII-Tokenisierung, `AiFeedbackService`, Adresse in About. *Deps:* G1 · *Effort:* S · *Value:* High
- [ ] **11. Server-Endpoint `POST /api/ai-feedback`** — nach [2026-08-29-ai-feedback-server-handoff.md](2026-08-29-ai-feedback-server-handoff.md): gesonderte Erfassung, Mail an die Interimsadresse, Weiterleitungsoption für Self-hosted, beide Goldens (Pia-Server-Repo). *Deps:* G1 · *Effort:* S · *Value:* Enabler
- [ ] **12. Template-Einordnung dokumentieren** — Ergebnis von G2 in den Vermerk und in Origin § 5. *Deps:* G2 · *Effort:* XS · *Value:* Med
- [ ] **13. Nachtrag in den kanonischen Vermerk** — Origin § 7 in `Pia.Server` übernehmen; Biometrie-Frage an den DSB. *Deps:* — · *Effort:* XS · *Value:* High
- [ ] **14. Menschlicher Smoke-Test** — echtes Teams-Meeting: Name mit Zusatz im Roster; About-Links öffnen; ein Export je Format mit Marker; Fußzeile bei Ollama ohne Usage; Daumen runter an einer Pia-Cloud-Antwort landet auf dem Server (nach 11). *Deps:* 9, 11 · *Effort:* XS · *Value:* High

## Nicht geplant

- Erzwungener Chat-Post des Bots beim Beitritt („Dieses Meeting wird von einem KI-Assistenten transkribiert") — über Playwright fragil; erst, wenn der Name allein als Hinweis nicht reicht.
- Umstellung der Kennzeichnung auf ein Text-Provenienzvokabular, sobald der Praxisleitfaden zu Art. 50 eines festlegt.

## Empfohlene Reihenfolge

13 zuerst (billig, schließt die Nachweislücke im kanonischen Dokument), dann 9, dann 11 (Server), parallel 12; 14 zuletzt.
