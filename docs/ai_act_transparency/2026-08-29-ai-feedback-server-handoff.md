# Handoff: `POST /api/ai-feedback` im Pia-Server

**Status:** Client-Seite gelandet 29.08.2026 (`Services/AiFeedbackService.cs`); Server-Seite offen — dieses Dokument ist die Übergabe · **Owner:** Marco Altmann · **Written:** 2026-08-29 · **Origin:** [2026-08-29-client-transparency-readiness.md](2026-08-29-client-transparency-readiness.md) § 6.1, Maßnahme M8 aus [2026-08-27-ai-act-art50-readiness.md](2026-08-27-ai-act-art50-readiness.md)

## 1. Zweck

Der KI-Beschwerdeweg nach Vermerk M8, beschränkt auf Antworten aus **Pia Cloud**: Nur dort hat neo42 das
Modell gewählt und betrieben. BYOK-Antworten haben im Client keine Melde-Buttons. Der Client sendet
Daumen hoch/runter und — bei Daumen runter — einen Freitext mit optionalem Antworttext an den
verbundenen Server. Der Server erfasst die Eingänge **gesondert** (nicht im Audit-Log, nicht in der
Token-Nutzung) und benachrichtigt die M8-Adresse.

## 2. Client-Verhalten (ist)

- Buttons erscheinen nur, wenn die Nachricht `Stats.IsPiaCloud` ist (`Models/AssistantMessage.cs`,
  `IsRateable`). Daumen hoch sendet sofort `rating: "up"` ohne Text. Daumen runter öffnet
  `AiFeedbackContentDialog` (Freitext, Checkbox „Antworttext mitsenden", Standard an) und sendet
  `rating: "down"`.
- Ist die PII-Tokenisierung des Nutzers an, laufen Kommentar und Antworttext durch dieselbe
  Tokenisierung wie ausgehende Prompts (`ITokenMapService.TokenizeStructuredResult`); der Server sieht
  dann `[Person_1]` statt Namen und `piiTokenized: true`.
- Transport: `POST {ServerUrl}/api/ai-feedback`, `Authorization: Bearer <access token>` wie bei allen
  anderen Endpunkten, `Content-Type: application/json`, camelCase. Jede Antwort ≠ 2xx gilt im Client als
  „nicht gesendet" (Snackbar, kein Retry).
- Nicht angemeldet oder kein `ServerUrl` → der Client sendet nichts und meldet das dem Nutzer.

## 3. Vertrag

Request-Body — `Pia.Shared/Models/AiFeedbackRequest.cs` ist die Referenz:

```json
{
  "schemaVersion": 1,
  "messageId": "3f9c…",
  "chatId": "8a12…",
  "rating": "down",
  "comment": "[Person_1] ist nicht der Geschäftsführer",
  "answerText": "…",
  "piiTokenized": true,
  "model": "Pia Cloud",
  "answeredAt": "2026-08-29T09:14:03Z",
  "reportedAt": "2026-08-29T09:16:41Z",
  "appVersion": "1.3.1000",
  "locale": "de-DE"
}
```

| Feld | Typ | Pflicht | Bemerkung |
|---|---|---|---|
| `schemaVersion` | int | ja | derzeit 1; additive Änderungen, unbekannte Felder speichern und zurückgeben (wie Chat-History) |
| `messageId` | guid | ja | Id der bewerteten Assistant-Nachricht (Client-seitig vergeben) |
| `chatId` | guid | nein | null bei Headless-Kontexten |
| `rating` | `"up"` / `"down"` | ja | |
| `comment` | string | nein | Freitext, ggf. tokenisiert |
| `answerText` | string | nein | nur mit Zustimmung des Nutzers, ggf. tokenisiert |
| `piiTokenized` | bool | ja | true ⇒ Platzhalter statt Klartext-PII |
| `model` | string | nein | Client-Label; das tatsächliche Upstream-Modell kennt nur der Server (`TokenUsageLog.Model` zur `messageId`? — siehe § 5) |
| `answeredAt`, `reportedAt` | UTC | ja | |
| `appVersion`, `locale` | string | nein | |

Antworten: `202 Accepted` (leerer Body oder `{ "id": … }`), `400` bei Schema-Fehlern, `401` ohne gültiges
Token, `429` bei Rate-Limit. Der Client unterscheidet nur 2xx/nicht-2xx.

## 4. Server-Umsetzung (soll)

1. **Endpoint** `POST /api/ai-feedback`, authentifiziert wie `/api/capabilities`; `UserId` aus dem Token,
   nie aus dem Body.
2. **Speicherung** in einer eigenen Tabelle `AiFeedback` (`Id`, `UserId`, `MessageId`, `ChatId`, `Rating`,
   `Comment`, `AnswerText`, `PiiTokenized`, `Model`, `AnsweredAt`, `ReportedAt`, `AppVersion`, `Locale`,
   `CreatedAt`, `Status` [new/triaged/closed], `ExtensionJson`). Getrennt vom Audit-Log — das ist die
   „gesonderte Erfassung" aus M8. Inhalte sind Nutzerinhalte: Zugriff nur für die Admin-Rolle.
3. **Benachrichtigung** je Eingang mit `rating: "down"` an `Feedback:NotifyAddress`, Standard
   **`entwicklung@neo42.de`** (Interimsadresse, Owner 29.08.2026). Inhalt der Mail: Id, Zeitpunkt, Modell,
   Kommentar, Hinweis auf Tokenisierung — den Antworttext nur per Link/Admin-Ansicht, nicht in der Mail.
   Daumen hoch nur zählen, nicht mailen.
4. **Self-hosted:** `Feedback:ForwardToProvider` (bool, Standard `true`) leitet eine Kopie an
   `Feedback:ProviderAddress` (Standard dieselbe Interimsadresse) weiter; der Betreiber kann es abschalten.
   Gehört in die Betreiberhinweise (Vermerk M5).
5. **Rate-Limit** z. B. 30 Eingänge je Nutzer und Stunde → `429`.
6. **Löschfrist** ins Löschkonzept aus R11 aufnehmen (Vorschlag: wie `Audit:RetentionDays`, 365 Tage;
   `AnswerText` früher, z. B. 90 Tage).
7. **Tests:** neue Route und neue DI-Registrierungen brauchen die beiden handgepflegten Snapshot-Goldens
   `routes.golden.txt` **und** `services.golden.txt`.

## 5. Offen

- Ob der Server die Meldung mit dem tatsächlichen Upstream-Modell anreichert (Join über `messageId` auf
  `TokenUsageLog`, falls dort die Nachrichten-Id geführt wird — heute vermutlich nicht).
- Eigener Alias statt `entwicklung@neo42.de`, sobald der Beschwerdeweg nach außen ausgewiesen wird
  (er steht seit 29.08.2026 in der About-Seite des Clients).
- Digest statt Einzelmail, wenn das Volumen es verlangt.
