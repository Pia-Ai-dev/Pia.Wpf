# Handoff: Offene Sicherheitspunkte im OAuth-Login

**Status:** Teilweise umgesetzt — Punkte 2 und 4 implementiert am 2026-08-29 (siehe [Umsetzungsstand](#umsetzungsstand-2026-08-29)); Punkte 1 und 3 offen
**Written:** 2026-08-29
**Origin:** [2026-08-28-auth-analyse.md](2026-08-28-auth-analyse.md) — der Befund, auf dem dieser Plan steht. Liegt jetzt im selben Ordner.
**Scope:** Ausschließlich die Sicherheitsprobleme des bestehenden Logins. **Graph ist nicht in Scope.**

Dieser Text ist als **Prompt** gedacht: in einer Session öffnen, die Zugriff auf **beide** Repositories hat.

---

## Ausgangslage (bitte nicht neu herleiten)

Zwei Repositories:

- **Client:** `~/Documents/GitHub/Pia.Wpf` — WPF, `net10.0-windows`, Branch `feature/agent-run-spine`. (`feature/scheduled_teams` wird **nicht mehr verwendet**.)
- **Server:** `~/Documents/GitHub/Pia` — ASP.NET Core, `net10.0`, Branch `feature/connector-abstraction-phase1`, Trunk ist `master`.

**Falle:** `Pia/lib/Pia.Wpf` ist ein **initialisiertes Submodul** des Clients, kein eigenständiger Klon — `src/Pia.Server/Pia.Server.csproj` verweist per Projektreferenz auf dessen `Pia.Shared`, der Server kompiliert also gegen den dort gepinnten Client-Stand. (Eine frühere Fassung dieses Dokuments behauptete das Gegenteil; auf dem Mac des Autors war es tatsächlich nur ein Klon.) **Dort trotzdem nichts bearbeiten:** Der Client wird ausschließlich im Client-Repo geändert, das Submodul folgt nur per Pointer-Bump auf einen Client-Commit. Solange der Client-Branch unveröffentlicht ist, braucht der Bump vorher ein `git -C lib/Pia.Wpf fetch <lokaler Client-Pfad> feature/agent-run-spine` — sonst kennt das Submodul den Commit nicht.

Befund aus der Analyse, gilt als gesichert:

- Der WPF-Client ist **kein OAuth-Client**. Er öffnet den Browser auf `{ServerUrl}/auth/login?provider=entraid&redirect_uri=http://localhost:<port>/`; den Entra-Flow führt der **Server**. Über den Loopback kommt ein **Pia-eigenes** JWT-Paar (Access + Refresh) per **Query-String** zurück.
- Im Client existiert **kein Entra-Token**, kein MSAL, keine Client-ID. Relevante Datei: `src/Pia.Wpf/Services/AuthService.cs`.
- Server-seitig: **single-tenant**, Authority fest auf die Tenant-GUID. Der Entra-Token wird per `SaveTokens` abgelegt, aber im selben Request durch `SignOutAsync("OAuthCookie")` wieder verworfen und im Login-Pfad nie ausgelesen. Das Pia-Token ist ein HS256-JWT (Issuer `pia-server`).

**Bereits erledigt — nicht erneut anfassen:** Commit `70c6fe0` auf `feature/connector-abstraction-phase1` schließt die ungeprüfte `redirect_uri` (F0, kritisch). Neue Allowlist `src/Pia.Server/Auth/ClientRedirectUri.cs`, Gate in `OAuthCallbackService.ProcessAsync` vor `IssueAsync`, Fail-Fast in `AuthController.Login`, 37 Tests.

---

## Aufgaben

Reihenfolge ist bewusst. Punkt 1 ist unabhängig von allem Code und am dringlichsten. Vier Punkte, alle rein sicherheitsbezogen.

### 1. Forensik: wurde F0 ausgenutzt? *(Ops, kein Code)*

`OAuthCallbackService` loggt das Redirect-Ziel seit jeher mit (`clientRedirectUri={RedirectUri}`). In den **Produktionslogs** nach `clientRedirectUri=` filtern und alles herausziehen, was nicht `localhost`, `127.0.0.1` oder `/admin` ist.

- Treffer ⇒ die abgeflossenen Refresh-Tokens sind **weiterhin gültig**. Der Codefix holt sie nicht zurück. Betroffene Konten über `IRefreshTokenService.RevokeAsync` rotieren.
- Keine Treffer ⇒ belastbare Negativevidenz, im Analysedokument festhalten.

Liegen die Logs nicht vor: als Aufgabe an den Betreiber formulieren, nicht raten.

### 2. F1 + F2 gemeinsam lösen — Code-Exchange statt Tokens im Query-String

**Nicht getrennt angehen.** Eine Protokolländerung schließt beide Findings, und der Zwischenschritt „nur `state` nachrüsten" ist deutlich weniger wert.

Heute:
- **F1:** Kein `state`/Nonce. Der Loopback-Listener nimmt jeden GET an und übernimmt Tokens aus der Query. Er bedient genau **ein** `GetContextAsync()` — die erste Anfrage gewinnt. Ein lokaler Prozess oder eine offene Webseite kann während des 5-Minuten-Fensters hohe Ports mit `img`-Tags abdecken und den Client **in ein fremdes Konto einloggen**; die Tokens landen persistent in den Settings. Datenabfluss über den Sync.
- **F2:** Access- und Refresh-Token stehen im URL-Query-String ⇒ Browser-Historie, potenziell Server-Logs und `Referer`.

Zielbild (PKCE-artig, schließt F1 und F2 in einem Zug):

1. Client erzeugt `code_verifier` (kryptografisch zufällig) und schickt `code_challenge` (SHA256, base64url) an `/auth/login`.
2. Server stempelt die Challenge in `AuthenticationProperties.Items`, erzeugt am Callback einen **kurzlebigen Einmal-Code** statt Tokens und leitet auf `http://localhost:<port>/?code=<opaque>` um.
3. Client löst per **POST** `/auth/token` mit `code` + `code_verifier` ein und erhält die Tokens **im Response-Body**.

Damit ist der Callback an genau die Client-Instanz gebunden, die den Flow gestartet hat — das, wofür `state` gedacht war — und es stehen keine Tokens mehr in einer URL.

**Zu entscheiden und explizit zu benennen:** Der Sicherheitsgewinn tritt erst ein, wenn der Server den alten Pfad **nicht mehr** bedient. Toleriert er weiterhin Logins ohne `code_challenge`, lässt ein Angreifer sie einfach weg und F1 bleibt offen. Also: Übergangsfenster festlegen und alten Pfad danach abschalten, oder Flag Day. Diese Frage vor der Implementierung beantworten, nicht danach.

Betroffen: `Pia.Wpf/src/Pia.Wpf/Services/AuthService.cs` (Client-Hälfte) sowie `Pia/src/Pia.Server/Controllers/Auth/AuthController.cs`, `Auth/OAuthCallbackService.cs` und ein neuer, kurzlebiger Code-Store (Server-Hälfte).

### 3. F0-Backport auf `master` — erst nach Freigabe

**Nicht ohne ausdrückliche Zustimmung des Owners beginnen.** Bewusst zurückgestellt.

`master` enthält dieselbe Lücke im Vorgängerstand `src/Pia.Server/Auth/AuthEndpoints.cs`: gleiche ungeprüfte `redirect_uri`, gleiches `StartsWith("/admin")` als einzige Prüfung, Tokens werden dort angehängt. Die Controller-Migration (`AuthController`/`OAuthCallbackService`) existiert auf `master` **nicht** — deshalb ließ sich `70c6fe0` nicht einfach dorthin branchen.

`src/Pia.Server/Auth/ClientRedirectUri.cs` ist ein abhängigkeitsfreier statischer Helper und lässt sich **unverändert** übernehmen; nur die Einhängepunkte unterscheiden sich (Minimal-API-Handler statt Controller + Service). Tests aus `tests/Pia.Server.Tests/Auth/ClientRedirectUriTests.cs` übertragen sich ebenfalls unverändert.

### 4. F3 — `ExpiresIn` wird ignoriert *(Client, kein Sicherheitsproblem)*

`LocalLoginResponse.ExpiresIn` existiert, wird nie gelesen; stattdessen dreimal hartcodiert `DateTime.UtcNow.AddMinutes(14)` in `AuthService.cs`. Der Loopback-Callback überträgt gar keine Ablaufzeit. Rein Robustheit — der 401-Pfad fängt es ab. Erledigen, wenn ohnehin an `AuthService` gearbeitet wird (also zusammen mit Punkt 2).

## Arbeitsregeln

**Test-Gates:**

- Server: `cd ~/Documents/GitHub/Pia && dotnet test --filter "Category!=Docker&Category!=Temporal&Category!=Network&Category!=E2E"` — läuft auf diesem Mac, Stand `70c6fe0`: 3440 grün, 0 Fehler. Das ist die Messlatte.
- Client: **`dotnet test` ist auf diesem Mac nicht ausführbar** (`net10.0-windows`). Kompilieren geht mit `-p:EnableWindowsTargeting=true`. Client-Tests müssen auf Windows oder in CI laufen — das offen sagen, statt ungetestete Änderungen als verifiziert auszugeben.

**Warnungen:** `Pia.Wpf` hat eine Zero-Warning-Policy (`TreatWarningsAsErrors`), Baseline 0. `Pia.Server` hat 12 vorbestehende Warnungen — die Zahl darf nicht steigen. MSBuild ist hier deutschsprachig (`Warnung(en)`/`Fehler`).

**Weitere Regeln:** Beide Repos haben eine `CLAUDE.md` mit verbindlichen Vorgaben (Kommentardisziplin, Doku-Layout, Server-Routing über `docs/ai_context/`). Vor Änderungen lesen. Commit-Stil: imperativ, keine Conventional-Commit-Präfixe.

**Nicht bearbeiten:** `Pia/lib/Pia.Wpf` — Submodul, siehe oben. Der Pointer-Bump ist die einzige erlaubte Änderung daran.

## Umsetzungsstand (2026-08-29)

**Entschieden (Owner, 2026-08-29): Übergangsfenster mit Time Bomb.** Der Server bedient Loopback-Logins
**ohne** `code_challenge` — Clients von vor dem Exchange — weiterhin auf dem alten Weg (Tokens im
Query-String), aber nur bis zum **einkompilierten Sunset `2026-10-01T00:00:00Z`**
(`LegacyLoopbackLoginPolicy.SunsetUtc`). Ab dann antwortet `/auth/login` für Loopback ohne Challenge mit
`400 code_challenge_required`, und `OAuthCallbackService` weist dasselbe als maßgebliches Gate ab. Verlängern
geht ausschließlich per neuem Server-Build — bewusst. Früher schließen kann der Betreiber jederzeit mit
`OAuth:AllowLegacyLoopbackLogin=false` (Default `true`, steht in `appsettings.json`). Jeder Legacy-Login
schreibt eine Warnung ins Log und `legacyLoopback: true` in die Audit-Metadaten von `Login.OAuth.Success` —
daran ist ablesbar, wann alle Clients umgestellt sind; beim Start warnt der Server, solange das Fenster offen
ist. Eine gesendete, aber fehlerhafte Challenge ist immer ein Fehler (`400 code_challenge_invalid`). **F1 ist
erst mit geschlossenem Fenster vollständig geschlossen.** Bestehende Sessions laufen über `/auth/refresh`
unverändert weiter.

- **Server** (`feature/connector-abstraction-phase1`): `Auth/Pkce.cs`, `Auth/LoginCodeStore.cs` (In-Memory,
  Einmal-Code, 2 Minuten, gehasht abgelegt), `Auth/LoginCodeExchangeService.cs`,
  `Auth/LegacyLoopbackLoginPolicy.cs` (Time Bomb), `POST /auth/token`
  (`AuthController`, `[AllowAnonymous]` + `[RequiresFeature(OAuth)]` + Rate-Limit `auth`). Tokens werden
  erst beim Exchange gemünzt — ein nie eingelöster Code hinterlässt keine Session. Ein falscher Verifier
  verbrennt den Code und schreibt `Login.Failed` mit `code_verifier_mismatch` ins Audit-Log.
- **Client** (`feature/agent-run-spine`): `Services/PkceCodes.cs`; `AuthService.LoginAsync` sendet die
  Challenge, ignoriert Loopback-Requests ohne `code`/`error` (404) statt sie den Login entscheiden zu lassen,
  löst den Code per `POST /auth/token` ein und schreibt die Browser-Seite erst nach dem Exchange. Antwortet
  ein alter Server noch mit Tokens in der URL, übernimmt der neue Client sie **nicht**, sondern bricht mit
  klarer Meldung ab.
- **F3:** `ExpiresIn` wird aus Login-, Token- und Refresh-Antwort gelesen (`AccessTokenExpiryFrom`, Abzug von
  bis zu 60 s Sicherheitsmarge); die hartcodierten 14 Minuten sind nur noch der Fallback ohne Angabe.
- **Doku:** `docs/ai_context/api_contracts.md`, `server_guidelines.md`, `endpoint-inventory.md`,
  Route-Golden um `POST /auth/token` ergänzt.
- **Tests:** Server grün (Auth-Namespace inkl. End-to-End-Handshake gegen den echten Container, Legacy-Fenster
  offen und geschlossen, echter 302 zum Provider). Client-Tests
  (`PkceCodesTests`, `AuthServiceTokenExpiryTests`) kompilieren; **Ausführung steht auf Windows/CI aus.**

Offen bleiben Punkt 1 (Forensik der Produktionslogs) und Punkt 3 (`master`-Backport, nur nach Freigabe).
