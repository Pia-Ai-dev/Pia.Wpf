# Handoff: Analyse des bestehenden Entra-Logins vor Graph-Integration

**Rolle:** Du analysierst eine bestehende WPF-Anwendung (.NET 10, Windows-Client) und lieferst einen Befund. Du implementierst in diesem Durchgang **noch nichts**.

**Fernziel:** Die App soll Outlook-Mails und Kalendereinträge des angemeldeten Benutzers über Microsoft Graph lesen (`Mail.Read`, `Calendars.Read`, delegiert).

**Ausgangslage:** Die App hat bereits einen Entra-ID-Login. Beim Klick auf "EntraID Login" öffnet sich der Systembrowser, es folgt ein Redirect zu Microsoft, und am Ende landet der Browser auf einer **`http://localhost:<port>/...`**-Seite mit Erfolgs- oder Fehlermeldung. Ein Server mit eigener App-Registrierung im Tenant ist ebenfalls im Spiel.

Der Loopback-Redirect ist ein starkes Indiz dafür, dass die WPF-App selbst der OAuth-Client ist und nicht der Server. Falls das stimmt, ist der Weg zu Graph kurz: dieselbe MSAL-Instanz kann per Incremental Consent einen zweiten Token für Graph holen, ohne dass sich der Benutzer erneut anmelden muss. **Diese Annahme ist zu verifizieren, nicht vorauszusetzen.**

---

## Arbeitsauftrag

Beantworte die Fragen in den Blöcken A bis E anhand des Codes. Wo der Code keine Antwort hergibt, sag das explizit, statt zu raten. Am Ende lieferst du den Befundbericht aus Abschnitt "Ergebnisformat".

### Block A — Welcher OAuth-Stack ist im Einsatz?

Die zentrale Weiche: fertige Bibliothek oder handgeschriebener Flow.

```bash
rg -i "Microsoft.Identity.Client|Microsoft.Graph|IdentityModel|Duende" --glob "*.csproj" --glob "*.props"
rg -i "PublicClientApplication|ConfidentialClientApplication|AcquireToken" -l
rg -i "HttpListener|/oauth2/v2.0/authorize|code_verifier|code_challenge" -l
```

Zu klären:

1. Ist `Microsoft.Identity.Client` (MSAL.NET) referenziert? Welche Version?
2. Falls nein: Was übernimmt den Flow? Ein `HttpListener` plus manueller Code-Exchange? Eine andere Bibliothek?
3. Wird der Authorization Code Flow mit PKCE verwendet? Suche nach `code_challenge` / `code_verifier`. Ein Public Client ohne PKCE ist ein Sicherheitsmangel, der unabhängig von Graph behoben gehört — vermerke ihn.
4. Wird der Systembrowser gestartet (`Process.Start` mit UseShellExecute) oder ein eingebettetes WebView?
5. Welcher Loopback-Port wird verwendet — fest verdrahtet oder dynamisch?

### Block B — Konfiguration der App-Registrierung

```bash
rg -i "clientid|client_id|tenantid|tenant_id|authority|redirecturi|redirect_uri|scope" \
   --glob "*.json" --glob "*.cs" --glob "*.config" --glob "*.xaml"
```

Zu klären:

1. Welche **Client-ID** nutzt die WPF-App? Ist es dieselbe wie die des Servers oder eine eigene?
2. Welche **Tenant-ID** bzw. Authority? Single-Tenant (`.../<guid>/v2.0`), `organizations` oder `common`?
3. Welche **Scopes** werden angefordert? Notiere sie wörtlich. Interessant ist besonders, ob eine `api://...`-Scope der Server-API dabei ist.
4. Welche **Redirect-URI** ist im Code hinterlegt — exakter String inklusive Port und eventuellem Trailing Slash?
5. Liegt irgendwo ein **Client Secret** im Client? Falls ja: das ist ein Finding mit hoher Priorität. Secrets gehören nicht in eine Desktop-App, und es würde bedeuten, dass die Registrierung als Confidential Client konfiguriert ist.
6. Wird der **WAM-Broker** verwendet (`WithBroker`, `BrokerOptions`, Paket `Microsoft.Identity.Client.Broker`)?

### Block C — Was passiert mit dem Token?

```bash
rg -i "AccessToken|id_token|refresh_token|Bearer|AuthenticationHeaderValue" -l
rg -i "UserTokenCache|MsalCacheHelper|StorageCreationProperties|ProtectedData|DPAPI" -l
```

Zu klären:

1. Wird ein **Access Token**, ein **ID Token** oder beides verwertet? Häufiger Fehler in selbstgebauten Flows: das ID Token wird als Bearer verwendet, was gegen Graph nicht funktioniert.
2. Wohin geht der Token? Nur an die eigene Server-API oder auch woanders hin?
3. Gibt es einen **persistenten Token-Cache**? Wenn ja, wo liegt er und wie ist er geschützt? Wenn nein: Der Benutzer muss sich nach jedem App-Start neu anmelden — relevant für die spätere UX-Bewertung.
4. Gibt es **Refresh-Logik**? Was passiert nach Ablauf des Tokens (typisch 60–90 Minuten)? Suche nach Retry-Handling auf `401`.
5. Wird der Token irgendwo **geloggt**, in Dateien geschrieben oder in Exception-Meldungen ausgegeben? Falls ja: Finding.

### Block D — Rolle des Servers

1. Wie authentifiziert sich die App gegenüber der Server-API — mit dem Entra-Token als Bearer, oder tauscht der Server ihn gegen eine eigene Session bzw. ein eigenes Cookie/JWT?
2. Falls es eine eigene Server-Session gibt: Existiert der Entra-Token im Client überhaupt noch, oder wird er nach dem Login verworfen? Das entscheidet, ob wir im Client auf einen bestehenden MSAL-Cache aufsetzen können.
3. Validiert der Server das Token gegen eine eigene Audience (`api://...`)? Suche im Serverprojekt, falls es im selben Repository liegt, nach `AddMicrosoftIdentityWebApi`, `TokenValidationParameters`, `ValidAudience`.
4. Führt der Server bereits irgendwo einen **On-Behalf-Of-Flow** aus? Suche nach `jwt-bearer`, `AcquireTokenOnBehalfOf`, `GetAccessTokenForUserAsync`.

### Block E — Integrationsfähigkeit der Codebasis

1. Wird **Dependency Injection** verwendet (`Microsoft.Extensions.Hosting`, `IServiceProvider`, ein Container wie Autofac)? Wo würde ein `GraphServiceClient` als Singleton sinnvoll hängen?
2. Wo liegt die Auth-Logik architektonisch — in Code-Behind, in einem ViewModel, in einem eigenen Service? Gibt es eine Schnittstelle, hinter der sich die Token-Beschaffung kapseln lässt?
3. Wie ist das **Threading** rund um den Login gelöst? MSAL-Interaktion braucht das Fensterhandle und muss sauber auf den UI-Thread zurückkommen.
4. Prüfe die `.csproj` auf `PublishTrimmed`, `PublishAot` oder `InvariantGlobalization`. Trimming und AOT vertragen sich schlecht mit MSAL und dem Graph SDK (Reflection, Kiota-Serialisierung) — falls aktiv, ist das ein Finding.
5. Welches `TargetFramework` genau? Gibt es eine `Directory.Packages.props` mit zentraler Paketverwaltung, in die neue Referenzen eingetragen werden müssten?

---

## Empirischer Token-Check

Der Code allein beantwortet nicht, was Entra tatsächlich ausstellt. Deshalb zusätzlich ein echter Token, decodiert.

**Wichtig:** Access Tokens sind Zugangsdaten. Nicht in ein Repository committen, nicht in Logs schreiben, nicht in Chatverläufe einfügen und **nicht auf jwt.ms oder ähnliche Webseiten hochladen**. Lokal decodieren:

```powershell
# $token = der rohe Access Token aus dem Debugger, nur im Speicher
$payload = $token.Split('.')[1].Replace('-','+').Replace('_','/')
$payload = $payload.PadRight($payload.Length + (4 - $payload.Length % 4) % 4, '=')
[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) |
    ConvertFrom-Json | ConvertTo-Json -Depth 5
```

Relevant sind diese Claims — nur die **Werte** dieser Felder in den Bericht übernehmen, nie den Token selbst:

| Claim | Bedeutung für uns |
|---|---|
| `aud` | Zielressource. `api://...` oder eine GUID der Server-App heißt: gegen Graph unbrauchbar. `00000003-0000-0000-c000-000000000000` wäre bereits Graph. |
| `appid` / `azp` | Welche App-Registrierung den Token angefordert hat. Deckt auf, ob Client und Server dieselbe ID nutzen. |
| `scp` | Die tatsächlich enthaltenen delegierten Scopes. |
| `roles` | Falls gesetzt: App-only-Berechtigungen. Wäre bei einem User-Login unerwartet und ein Finding. |
| `tid` | Tenant. |
| `idtyp` | Falls vorhanden und `app`: kein Benutzerkontext im Token. |
| `ver` | `2.0` erwartet. Ein v1.0-Token deutet auf eine veraltete Endpoint-Konfiguration hin. |

Notiere außerdem, ob ein **Refresh Token** ausgestellt wurde. Das setzt den Scope `offline_access` voraus — prüfe, ob der angefordert wird.

---

## Entscheidungsbaum

Ordne den Befund einem dieser Fälle zu:

**Fall 1 — MSAL Public Client im WPF-Client, eigene oder mitgenutzte Client-ID.**
Bester Fall. Nächster Schritt ist dann nur: Graph-Delegated-Permissions in der Registrierung ergänzen, Admin Consent einholen, zweiter `AcquireTokenSilent`-Aufruf mit den Graph-Scopes, `GraphServiceClient` verdrahten. Kein zweiter Login für den Benutzer.

**Fall 2 — Handgeschriebener Flow mit HttpListener.**
Funktioniert prinzipiell auch, aber Refresh, Cache-Verschlüsselung, PKCE, Claims Challenges und Broker-Support müssten alle selbst gebaut werden. Empfehlung wäre die Migration auf MSAL. Schätze in diesem Fall den Migrationsaufwand ab: Wie viele Stellen sind betroffen, wie stark ist die Auth-Logik in die UI verwoben?

**Fall 3 — Der Loopback dient nur dazu, eine Server-Session abzuholen; der Entra-Token existiert im Client nicht oder nur flüchtig.**
Dann ist der Client kein vollwertiger OAuth-Client. Entweder wird er dazu gemacht, oder der Server holt per On-Behalf-Of einen Graph-Token und stellt die Daten über eigene Endpunkte bereit. Trage in diesem Fall die Fakten zusammen, die für die Abwägung nötig sind: Gibt es bereits ein Client Secret oder Zertifikat auf dem Server? Ist die Server-Registrierung Multi-Tenant? Wie groß wäre der Umbau auf Clientseite?

**Fall 4 — etwas anderes.** Beschreib es.

---

## Ergebnisformat

Liefere eine Datei `docs/auth-analyse.md` mit:

1. **Kurzbefund** — welcher Fall aus dem Entscheidungsbaum, in drei bis fünf Sätzen.
2. **Faktentabelle** — Client-ID (die letzten vier Zeichen genügen zur Unterscheidung), Authority, Scopes, Redirect-URI, MSAL-Version, Broker ja/nein, Cache ja/nein.
3. **Token-Claims** — die Werte aus der Tabelle oben.
4. **Codestellen** — Datei und Zeilennummer für: Login-Einstieg, Token-Beschaffung, Token-Verwendung, Konfigurationsquelle.
5. **Findings** — nach Schweregrad sortiert. Secrets im Client, fehlendes PKCE, geloggte Tokens, ungeschützter Cache zuerst.
6. **Offene Punkte** — was sich aus dem Code nicht ermitteln ließ und beim Tenant-Admin oder am laufenden System geklärt werden muss.
7. **Vorschlag für den nächsten Schritt** — konkret, mit geschätztem Umfang, aber ohne Implementierung.

---

## Nicht tun

- Keine Änderungen an der Authentifizierung in diesem Durchgang. Erst Befund, dann Entscheidung, dann Code.
- Keine Pakete installieren oder `.csproj`-Dateien anfassen.
- Keine Client-IDs, Tenant-IDs, Secrets oder Tokens in den Bericht schreiben, sofern sie nicht ohnehin im Repository stehen. Client- und Tenant-IDs sind für sich genommen nicht geheim, aber unnötige Streuung vermeiden.
- Nicht bei der ersten plausiblen Fundstelle aufhören. Gerade bei gewachsenen Anwendungen existieren manchmal zwei Auth-Pfade parallel, etwa ein alter und ein neuer. Prüfe, welcher tatsächlich aufgerufen wird.

---

## Kontext für die spätere Umsetzung

Damit die Empfehlung in die richtige Richtung zeigt, hier der bereits abgesteckte Zielzustand:

- **Graph statt EWS.** EWS wird in Exchange Online ab 1. Oktober 2026 tenant-weise abgeschaltet, vollständig ab 1. April 2027. Kein gangbarer Weg mehr.
- **Delegierte Berechtigungen**, kein App-only. `Mail.Read` und `Calendars.Read` genügen; nicht großzügiger anfordern.
- **Kalender über `/me/calendarView`**, nicht `/me/events` — nur die CalendarView löst Serientermine in einzelne Vorkommen auf.
- **WAM-Broker** ist erwünscht, sofern machbar: stilles SSO auf Entra-joined Geräten, Tokens im Broker statt im Prozess, zuverlässigere Conditional-Access-Signale.
- **Voraussetzung Exchange Online.** Liegt das Postfach on-premises, funktioniert Graph nicht. Falls sich im Repository oder in der Konfiguration Hinweise auf eine Hybrid-Umgebung finden, unbedingt vermerken.
