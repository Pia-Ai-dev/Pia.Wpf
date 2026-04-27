# Technische Spezifikation: Consent-Management-System für Audio-Aufzeichnung

> **Kontext:** Diese Spezifikation beschreibt die Architektur eines DSGVO- und § 201 StGB-konformen Consent-Management-Systems für eine Desktop-Anwendung (Windows/Mac), die Audio aufnimmt, lokal per STT transkribiert und optional per Online-KI zusammenfasst. Die Anwendung speichert Audio **nicht** persistent – der Audio-Buffer wird direkt an das STT-Modell übergeben. Speaker Detection und lokales TTS sind bereits vorhanden.

> **Hinweis:** Diese Spezifikation ist programmiersprachen- und betriebssystem-agnostisch. Implementierungsdetails (z. B. Audio-Routing-APIs) sind je nach Plattform zu konkretisieren.

---

## Inhaltsverzeichnis

1. [Architektur-Überblick](#1-architektur-überblick)
2. [Datenmodell](#2-datenmodell)
3. [Komponenten im Detail](#3-komponenten-im-detail)
4. [Persistenz- und Lebenszyklus-Strategie](#4-persistenz--und-lebenszyklus-strategie)
5. [Pre-Processing Pipeline (vor Cloud-Versand)](#5-pre-processing-pipeline-vor-cloud-versand)
6. [Edge Cases und Fehlerbehandlung](#6-edge-cases-und-fehlerbehandlung)
7. [Konfigurierbare Sicherheits-Modi](#7-konfigurierbare-sicherheits-modi)
8. [Nicht-funktionale Anforderungen](#8-nicht-funktionale-anforderungen)
9. [Implementierungsreihenfolge](#9-implementierungsreihenfolge)
10. [Rechtliche Hinweise](#10-rechtliche-hinweise)

---

## 1. Architektur-Überblick

### 1.1 Datenfluss-Diagramm

```
[Audio Input]
      ↓
[Speaker Diarization]                        ← bereits vorhanden
      ↓
[Per-Speaker Voice-Embedding-Filter]         ← blockt DENIED dauerhaft
      ↓
[Per-Speaker Ring Buffer (RAM only)]         ← hält Audio bis Consent-Entscheidung
      ↓
[Consent State Manager]
      ├─→ State == UNKNOWN → trigger Prompt → State = PROMPTED
      ├─→ State == PROMPTED → Audio an Consent-Classifier-Pfad (STT-isoliert)
      ├─→ State == GRANTED → Audio an reguläres STT
      └─→ State == DENIED/REVOKED → Audio verwerfen
      ↓
[STT Engine] (zwei logische Pfade: Consent-Klassifikation vs. Transcript)
      ↓
[Post-STT Defense Filter]                    ← Doppelprüfung
      ↓
[In-Memory Transcript Store]
      ↓
   ├─→ [Optional: Cloud-LLM für Zusammenfassung] (PII-Pseudonymisierung davor)
   └─→ [Optional: User-initiierte Persistierung des Transkripts]
      ↓
[Audit Log] (durchgehend, hash-chained, nur Metadaten – keine Inhalte)
```

### 1.2 Zentrale Designprinzipien

- **Speaker-scoped Verarbeitung:** Audio wird nicht als monolithischer Stream behandelt, sondern als Menge speaker-segmentierter Streams, die unabhängig durch verschiedene Consent-Zustände laufen.
- **Pre-STT-Consent-Gate:** Audio wird nur dann an das STT übergeben, wenn der zugehörige Speaker eingewilligt hat. Das Gate ist die kritische Echtzeit-Komponente der gesamten Architektur.
- **Audio bleibt flüchtig (RAM-only):** Es wird kein Audio auf dauerhafte Speicher geschrieben. Das ist ein starkes Privacy-by-Design-Argument nach Art. 25 DSGVO.
- **Defense-in-Depth:** Mehrere Filter-Ebenen vor und nach dem STT verhindern, dass nicht-eingewilligte Audio-Inhalte verarbeitet werden, selbst bei Race Conditions.
- **Konservative Defaults:** Bei Zweifeln (Timeout, mehrdeutige Antwort, unbekannter Speaker) wird **nicht** aufgenommen.

### 1.3 Komponentenübersicht

| Komponente | Status | Funktion |
|---|---|---|
| Speaker Diarization | vorhanden | Speaker-Identifikation in Echtzeit |
| Lokales STT | vorhanden | Transkription |
| Lokales TTS | vorhanden | Ausgabe der Consent-Prompts |
| Per-Speaker Ring Buffer | neu | Hält Audio bis Consent-Entscheidung |
| Consent State Manager | neu | Orchestriert Lebenszyklus pro Speaker |
| Consent Classifier | neu | Klassifiziert Antworten als Grant/Deny |
| Voice-Embedding-Filter | neu | Blockt DENIED-Speaker dauerhaft |
| Pre-STT-Gate | neu | Routing-Entscheidung pro Audio-Chunk |
| Post-STT Defense Filter | neu | Doppelprüfung nach Transkription |
| Audit Log (Hash-Chain) | neu | Manipulationssichere Dokumentation |

---

## 2. Datenmodell

### 2.1 Speaker Identity

Jeder erkannte Speaker bekommt eine eindeutige Session-ID:

```
Speaker {
    speaker_id: UUID                    // session-lokal eindeutig
    voice_embedding: float[]            // vom Diarization-Modell
    first_detected: timestamp
    consent_state: ConsentState
    consent_evidence: ConsentEvidence?
    transcript_segments: SegmentRef[]
}
```

### 2.2 Consent State Machine

Pro Speaker existiert ein Zustandsautomat mit folgenden Zuständen:

```
UNKNOWN          → Speaker neu erkannt, Consent noch nicht erfragt
PROMPTED         → Ansage wurde abgespielt, Antwort wird erwartet
GRANTED          → Einwilligung erteilt und dokumentiert
DENIED           → Einwilligung verweigert
REVOKED          → Nachträglich widerrufen
TIMEOUT          → Keine Antwort innerhalb X Sekunden
AMBIGUOUS        → Antwort konnte nicht eindeutig klassifiziert werden
```

### 2.3 Consent Evidence

Da kein Audio persistiert wird, basiert die Evidenz primär auf dem **Transkript** der Antwort:

```
ConsentEvidence {
    transcript_text: string             // Wortlaut der Antwort (vom STT)
    classification_confidence: float    // 0.0 - 1.0
    timestamp: timestamp
    consent_scope: ConsentScope         // lokal-only, EU-Cloud, US-Cloud
    prompt_version_hash: string         // welche Ansage wurde gespielt
    prompt_text_played: string          // exakter Wortlaut der Ansage
    stt_model_id: string                // welches STT hat klassifiziert
    cryptographic_signature: bytes      // Manipulationssicherheit
}
```

> **Optionale Härtung:** Behalte die Möglichkeit, *nur den Consent-Audio-Snippet* (z. B. die ersten 10–15 Sekunden mit der Ja-Antwort) zu persistieren – das ist datenschutzrechtlich unter Datenminimierung vertretbar und juristisch ein deutlich stärkerer Beweis als reiner STT-Output. Dies wäre eine kontrollierte Ausnahme von der „kein Audio speichern"-Regel und sollte konfigurierbar sein.

### 2.4 Consent Scope

```
ConsentScope {
    local_processing: bool              // STT lokal
    eu_cloud_processing: bool           // EU-LLM für Zusammenfassung
    non_eu_cloud_processing: bool       // US- oder andere Drittland-LLM
    biometric_persistence: bool         // Voice-Embedding über Sessions
}
```

---

## 3. Komponenten im Detail

### 3.1 Speaker Diarization Layer (vorhanden – Schnittstelle definieren)

Erweitere die bestehende Speaker-Detection um folgende Events:

```
Event: SpeakerDetected(speaker_id, voice_embedding, confidence)
Event: SpeakerSpeechStarted(speaker_id, timestamp)
Event: SpeakerSpeechEnded(speaker_id, timestamp)
Event: NewSpeakerJoined(speaker_id)            // erstmals erkannt
Event: SpeakerReturned(speaker_id)             // bekannter Speaker spricht wieder
```

Wichtig: Die Diarization muss unter realistischer Latenz liefern (max. 500 ms), damit der Ringpuffer- und Gate-Mechanismus funktioniert.

### 3.2 Per-Speaker Ring Buffer

Statt eines globalen Ringpuffers wird **pro Speaker ein eigener Buffer** geführt:

```
RingBuffer {
    speaker_id: UUID
    capacity_seconds: int               // Default: 60s
    audio_samples: CircularQueue<Sample>
    metadata: SegmentMetadata[]
}
```

**Funktionsweise:**

- Sobald ein Sample eintrifft, wird es einem oder mehreren Speakern zugeordnet (Diarization-Output).
- Bei überlappenden Speakern (Cross-Talk): Sample geht in den Buffer aller aktiven Speaker.
- Buffer-Inhalt wird nur dann an das STT übergeben, wenn der zugehörige Speaker den Zustand `GRANTED` erreicht.

**Speicherverwaltung:**

- Buffers werden ausschließlich im RAM gehalten (kein Disk-Spill, kein Swap-File).
- Bei Speicher-Druck: ältester Inhalt wird verworfen, **niemals** auf Disk ausgelagert.
- Memory-Cap pro Session konfigurierbar (z. B. 100 MB total über alle Speaker).

### 3.3 Consent State Manager

Zentrale Komponente, die für jeden Speaker den Lebenszyklus orchestriert.

**Workflow bei `NewSpeakerJoined`:**

```
1. Erstelle Speaker-Eintrag mit consent_state = UNKNOWN
2. Erstelle leeren Ring Buffer für diesen Speaker
3. Setze pending_action = REQUEST_CONSENT
4. Bestimme passenden Prompt aus Template-Library
5. Übergebe Prompt an TTS Output Manager
```

**Workflow bei TTS-Ansage abgespielt:**

```
1. Setze consent_state = PROMPTED
2. Starte Timeout-Timer (z. B. 15 Sekunden)
3. Aktiviere Consent-Listener für diesen Speaker
```

**Workflow bei Speaker-Antwort:**

```
1. Identifiziere Audio-Segment der Antwort im Ring Buffer
2. Sende Segment an lokales STT über den isolierten Consent-Pfad
3. Sende Transkript an Consent Classifier
4. Update consent_state basierend auf Classifier-Ergebnis
5. Speichere ConsentEvidence
6. Bei GRANTED: öffne Pre-STT-Gate für diesen Speaker für reguläre Transkription
   Bei DENIED: Buffer verwerfen, Voice-Embedding zur Blocklist hinzufügen
   Bei AMBIGUOUS: Clarification-Prompt abspielen
   Bei TIMEOUT: Behandlung wie DENIED (sicherer Default)
```

### 3.4 Pre-STT-Consent-Gate (kritische Echtzeit-Komponente)

Das Gate entscheidet pro Audio-Chunk und Speaker, ob der Chunk an das STT übergeben wird:

```
function should_pass_to_stt(speaker, audio_chunk):
    if speaker.consent_state == GRANTED:
        return PASS_TO_TRANSCRIPT
    
    if speaker.consent_state == PROMPTED 
       and is_within_response_window(speaker):
        # Spezial-Pfad: nur für Consent-Klassifikation,
        # Output landet NICHT im regulären Transcript
        return PASS_TO_CONSENT_CLASSIFIER
    
    # UNKNOWN, DENIED, REVOKED, TIMEOUT, AMBIGUOUS
    return DROP
```

**Anforderungen an das Gate:**

```
- Diarization-Output muss spätestens 200ms nach Sample-Eingang vorliegen
- Gate-Entscheidung muss synchron erfolgen
- Bei UNKNOWN/PROMPTED-State: Audio im Ring Buffer halten,
  NICHT ans reguläre STT durchreichen
- Bei GRANTED: Buffer-Inhalt + Live-Stream ans STT
- Bei DENIED: Buffer verwerfen, Live-Stream droppen
```

### 3.5 STT-Output-Routing pro Speaker

Da das STT speaker-aware angesprochen wird, muss das Output-Handling speaker-spezifisch sein:

```
function on_stt_output(text_segment, speaker_id, pipeline_path):
    speaker = get_speaker(speaker_id)
    
    if pipeline_path == CONSENT_CLASSIFICATION:
        process_as_consent_evidence(text_segment, speaker)
        return  # NICHT ins reguläre Transcript
    
    # Defense-in-Depth: doppelte Prüfung
    if speaker.consent_state != GRANTED:
        log_anomaly("STT-Output ohne Consent erhalten")
        return
    
    append_to_transcript(text_segment, speaker)
```

### 3.6 Post-STT Defense Filter

Selbst wenn das Pre-STT-Gate fehlschlägt, filtert diese Ebene nach dem STT nochmals:

```
- Wenn STT-Output einem Speaker mit consent_state != GRANTED 
  zugeordnet ist:
  → verwerfen
  → Audit-Event "DROPPED_TRANSCRIPT_NO_CONSENT" schreiben
  → Health-Check-Alert auslösen (Hinweis auf Bug im Gate)
```

Dies ist die zweite Schutzlinie gegen Race Conditions zwischen Diarization und Gate-Entscheidung.

### 3.7 Consent Classifier

Klassifiziert die STT-Ausgabe der Antwort.

**Stufe 1 – Rule-based Matching:**

Phrase-Listen pro Sprache mit gewichteten Mustern:

```
GRANT_PATTERNS = {
    de: ["ja", "einverstanden", "okay", "kein problem", "in ordnung",
         "von mir aus", "passt", "geht klar"],
    en: ["yes", "sure", "okay", "fine", "go ahead", "no problem",
         "of course", "agreed"],
    ...
}

DENY_PATTERNS = {
    de: ["nein", "nicht einverstanden", "lieber nicht", "stopp",
         "kein einverständnis", "auf keinen fall"],
    ...
}

AMBIGUOUS_INDICATORS = ["vielleicht", "ich weiß nicht", "warum",
                         "was genau", "moment"]
```

**Stufe 3 – Konfidenz-Threshold:**

- `confidence >= 0.9` → entsprechender Zustand wird gesetzt
- `confidence < 0.9` → AMBIGUOUS → erneutes Nachfragen oder Abbruch

### 3.8 TTS Output Manager (vorhanden – Erweiterungen)

Erweitere das bestehende lokale TTS um:

**Prompt Template Library:**

```
PromptTemplate {
    template_id: string
    language: string
    text: string                        // mit Platzhaltern
    variants: PromptVariant[]           // für verschiedene Consent Scopes
    audio_cache: AudioFile?             // pre-rendered für niedrigere Latenz
    version_hash: string
}
```

Beispiel-Templates:

```
INITIAL_CONSENT_LOCAL_ONLY:
"Hallo, ich nutze ein Tool, das unser Gespräch lokal auf meinem
Computer aufzeichnet und für meine Notizen verarbeitet. Es werden
keine Daten an externe Dienste gesendet. Sind Sie damit einverstanden?
Ein kurzes Ja oder Nein genügt."

INITIAL_CONSENT_EU_CLOUD:
"... das Gespräch wird mit einem KI-Dienst in der Europäischen Union
verarbeitet ..."

NEW_SPEAKER_JOIN:
"Eine neue Person ist dem Gespräch beigetreten. Auch hier benötige
ich kurz Ihre Einwilligung..."

CLARIFICATION_AMBIGUOUS:
"Entschuldigung, ich habe Ihre Antwort nicht eindeutig verstanden.
Sind Sie mit der Aufzeichnung einverstanden – ja oder nein?"

REVOCATION_CONFIRM:
"Verstanden, die Aufzeichnung wurde gestoppt und alle Notizen 
gelöscht."
```

**Audio-Routing:**

Das TTS-Output muss in den **ausgehenden Audio-Stream** der Kommunikation eingespeist werden, nicht nur lokal abgespielt. Das erfordert ein virtuelles Audio-Device oder Loopback-Routing (Implementierungsdetail je nach OS).

**Half-Duplex-Schutz:**

Während TTS spricht, sollte die Aufnahme/Klassifikation des eigenen TTS-Outputs vermieden werden:

- Während TTS aktiv: Diarization-Events für TTS-Stimme markieren (eigenes Voice-Embedding kennen) und ignorieren.
- Echo Cancellation auf Eingangsseite, falls TTS auch über Lautsprecher kommt.

### 3.9 New Speaker Handling

Wenn ein neuer Speaker während des laufenden Gesprächs erkannt wird, gibt es zwei Strategien, die parallel implementiert und vom Nutzer konfigurierbar sein sollten:

**Strategie A: Pause & Re-Consent**

```
1. STT-Pipeline aller bisher GRANTED-Speaker pausieren
2. Neuen Ring Buffer für neuen Speaker anlegen
3. NEW_SPEAKER_JOIN-Prompt abspielen
4. Auf Antwort warten und klassifizieren
5. Bei GRANT: STT-Pipeline aller Speaker fortsetzen
6. Bei DENY: nur diesen Speaker dauerhaft ausschließen,
   andere Pipelines fortsetzen
```

**Strategie B: Selective Recording (Default)**

```
1. Bisherige Pipelines laufen ungestört weiter
2. Neuer Speaker bekommt eigenen Ring Buffer
3. Audio-Segmente des neuen Speakers werden NICHT ans STT übergeben
4. Asynchron: NEW_SPEAKER_JOIN-Prompt abspielen
5. Bei späterem GRANT: ab diesem Moment ans STT durchreichen
   (NICHT rückwirkend aus Ring Buffer übernehmen,
   da Vor-Consent-Zeitraum ohne Einwilligung)
6. Bei DENY/TIMEOUT: Ring Buffer verwerfen,
   permanenter Filter für dieses Voice-Embedding
```

**Voice-Embedding-Filter für DENIED Speaker:**

```
DeniedSpeakerFilter {
    blocked_embeddings: VoiceEmbedding[]
    similarity_threshold: float = 0.85
    
    function should_drop(sample, embedding):
        return any(cosine_similarity(embedding, b) > threshold
                   for b in blocked_embeddings)
}
```

Dieser Filter läuft **vor** dem Ring Buffer und verhindert, dass DENIED-Speaker überhaupt im Buffer landen.

### 3.10 Cross-Talk Handling

Reale Gespräche haben überlappende Sprecher. Problemfälle:

**Fall 1: GRANTED-Speaker und DENIED-Speaker sprechen gleichzeitig**

- Audio-Sample wird beiden Speakern zugeordnet.
- STT-Übergabe erfolgt nur, wenn **alle** beteiligten Speaker GRANTED sind.
- Alternative: Source Separation einsetzen (rechenintensiv) und nur den GRANTED-Anteil an das STT geben.

**Fall 2: Unbekannter neuer Speaker während Cross-Talk**

- Sample wird in Buffer aller bekannten Speaker eingeordnet.
- Für den unbekannten Speaker startet der NEW_SPEAKER-Workflow.
- Während Klassifikation läuft: Sample wird **nicht** ans reguläre STT übergeben (konservatives Default).

**Implementierungsempfehlung:**

```
function should_pass_to_stt(sample, active_speakers):
    if len(active_speakers) == 0:
        return False                    // unklar, lieber droppen
    return all(s.consent_state == GRANTED for s in active_speakers)
```

---

## 4. Persistenz- und Lebenszyklus-Strategie

### 4.1 Was wird gespeichert – und was nicht

| Datenkategorie | Persistenz | Begründung |
|---|---|---|
| **Roh-Audio** | nie | Privacy-by-Design; nur RAM-Buffer |
| **Voice-Embeddings (session-flüchtig)** | nie | Nur im Speaker-Objekt während Session |
| **Voice-Embeddings (über Sessions)** | nur mit Extra-Consent | Biometrisches Datum nach Art. 9 DSGVO |
| **Transkript** | optional, nutzergesteuert | Default: nicht persistieren |
| **Consent Evidence (Transkript-Text)** | ja | Nachweispflicht nach Art. 7 DSGVO |
| **Consent-Audio-Snippet** | optional, konfigurierbar | Stärkere Beweiskraft, Datenminimierung beachten |
| **Audit Log** | ja | Manipulationssicher, nur Metadaten |

### 4.2 Storage-Struktur (für die persistierten Anteile)

```
session_<uuid>/
├── manifest.json                       // Session-Metadaten
├── consent/
│   ├── speaker_<id>_evidence.json      // ConsentEvidence pro Speaker
│   ├── speaker_<id>_grant.opus         // OPTIONAL, falls aktiviert
│   └── prompt_<hash>.opus              // OPTIONAL: gespielte Ansage
├── transcript/
│   └── transcript.json                 // OPTIONAL, mit Speaker-Tags
└── audit_log.jsonl                     // Append-only Event-Log
```

### 4.3 Encryption-at-Rest (für persistierte Anteile)

- Symmetrische Verschlüsselung pro Session (z. B. AES-256-GCM).
- Session-Key abgeleitet aus Master-Key im OS-Keystore.
- Manifest enthält Key-ID, nicht den Schlüssel selbst.

### 4.4 Audit Log (manipulationssicher)

```
AuditEvent {
    event_id: UUID
    timestamp: timestamp
    event_type: enum                    // SPEAKER_JOINED, CONSENT_GRANTED, ...
    speaker_id: UUID?
    details: JSON                       // KEINE Inhalte, nur Metadaten
    previous_event_hash: bytes          // Hash-Chain
    signature: bytes
}
```

Hash-Chain: jeder Event referenziert den Hash des vorherigen → nachträgliche Manipulation einzelner Events erzeugt einen Bruch in der Chain.

**Wichtig:** Im Audit Log werden **niemals** Transkript-Inhalte gespeichert, nur Metadaten (Speaker-ID, Zeitstempel, Event-Typ, Consent-Status).

### 4.5 Session-Ende

```
1. Stoppe alle Ring Buffer
2. Verwerfe alle Buffer-Inhalte (RAM löschen, ggf. überschreiben)
3. Falls Transkript-Persistenz aktiviert: finalisiere Transkript-File
4. Generiere Session-Manifest mit:
   - Liste aller Speaker
   - Consent-Status pro Speaker
   - Tatsächlich verarbeitete Audio-Dauer pro Speaker
5. Setze Retention-Timer für persistierte Anteile
```

### 4.6 Retention Policy

```
RetentionPolicy {
    transcript_retention_days: int           // Default: 90
    consent_evidence_retention_days: int     // Default: 1095 (3 Jahre, §195 BGB)
    consent_audio_snippet_retention_days: int // Default: 1095, falls aktiviert
    audit_log_retention_days: int            // Default: 1095
}
```

> **Wichtig:** Consent Evidence muss **länger** aufbewahrt werden als das eigentliche Transkript – sie ist das Nachweismaterial im Streitfall. Drei Jahre orientieren sich an der regelmäßigen Verjährungsfrist nach § 195 BGB.

### 4.7 Widerrufsworkflow

```
function revoke_consent(speaker_id, scope):
    1. Markiere Speaker als REVOKED
    2. Voice-Embedding zur dauerhaften Blocklist hinzufügen
    3. Falls Transkript persistiert: 
       - Redigiere Passagen dieses Speakers (oder lösche komplett)
    4. Trigger KI-Anbieter-Löschanfrage 
       (falls Zusammenfassung bereits an Cloud-LLM gesendet)
    5. Falls Zusammenfassung lokal vorliegt: 
       - neu generieren ohne diesen Speaker oder löschen
    6. Schreibe REVOCATION-Event ins Audit Log
    7. Behalte ConsentEvidence (für Nachweis "es gab mal Consent")
       und ergänze RevocationEvidence
```

---

## 5. Pre-Processing Pipeline (vor Cloud-Versand)

Falls Cloud-KI für Zusammenfassungen eingesetzt wird, muss vor dem Upload zusätzlich passieren:

```
1. Filter: nur Segmente mit consent_scope == CLOUD_ALLOWED
2. PII-Erkennung lokal
3. Pseudonymisierung: Namen, Telefonnummern, IBANs, Adressen ersetzen
4. Reverse-Mapping lokal speichern
5. Upload pseudonymisierter Inhalt
6. Empfangenes Ergebnis lokal de-pseudonymisieren
```

**Cloud-Anbieter-Kategorien:**

- **EU-Anbieter** (z. B. Mistral FR, Aleph Alpha DE): AVV erforderlich, kein Drittlandtransfer.
- **US-Anbieter mit Privacy Framework**: AVV + Standardvertragsklauseln, explizite Nutzer-Einwilligung mit Drittland-Hinweis.
- **Sonstige Drittländer**: nur mit ausdrücklicher Einwilligung und SCCs.

---

## 6. Edge Cases und Fehlerbehandlung

### 6.1 TTS-Ansage wird nicht durchgelassen

Beispielsweise weil Audio-Routing fehlschlägt oder Gegenseite stummgeschaltet hat.

**Lösung:**
- Echo-Detection: prüfe, ob TTS-Output im eigenen Mikrofon-Loopback ankommt.
- Falls nicht: warne Nutzer visuell („Ansage konnte nicht abgespielt werden – bitte mündlich um Einwilligung bitten und dann manuell bestätigen").
- Manueller Bestätigungs-Modus als Fallback.

### 6.2 Speaker-Verwechslung

Diarization ist nicht fehlerfrei. Wenn Speaker A versehentlich als Speaker B klassifiziert wird:

**Lösung:**
- Konservatives Threshold beim Voice-Matching.
- Bei Unsicherheit: neuen Speaker anlegen statt bestehendem zuordnen → führt zu erneutem Consent-Prompt (lieber einmal zu viel fragen).
- Post-Session-Tool für Nutzer zur manuellen Speaker-Zuordnungs-Korrektur.

### 6.3 Schweigen statt Antwort

```
Timeout-Verhalten:
- Nach 15s: TIMEOUT-Zustand
- TIMEOUT wird wie DENIED behandelt (sicherer Default)
```

### 6.4 Sprache des Gesprächspartners unklar

```
1. Default-Sprache aus Nutzer-Einstellungen verwenden
2. Nach erster Antwort: Spracherkennung auf Antwort
3. Falls Mismatch: Folge-Prompts in erkannter Sprache
4. Mehrsprachiger Initial-Prompt als Option ("Hello / Hallo / Bonjour ...")
```

### 6.5 App-Crash während laufender Aufnahme

```
1. Ring Buffer im RAM ist verloren – das ist gewollt 
   (kein Pre-Consent-Audio überlebt den Crash)
2. Falls Transkript-Persistenz aktiviert: 
   per Segment final geschrieben → unbeschädigt
3. Beim Neustart: Recovery erkennt offene Session, 
   schließt sauber ab
4. Kein Continuation der Aufnahme ohne erneuten Consent
```

### 6.6 Race Condition zwischen Diarization und Gate

Das Pre-STT-Gate könnte in seltenen Fällen Audio durchlassen, bevor der Consent-State final gesetzt ist.

**Lösung:**
- Post-STT Defense Filter (Komponente 3.6) verwirft Output von Non-GRANTED-Speakern.
- Health-Check-Alert dokumentiert Race Conditions für spätere Analyse.

---

## 7. Konfigurierbare Sicherheits-Modi

Biete dem Nutzer drei vordefinierte Profile:

### Strict Mode (Default)
- Strategie A für neue Speaker (Pause & Re-Consent)
- Cross-Talk mit Unbekannten → keine STT-Übergabe
- Cloud-Verarbeitung deaktiviert
- Transkript-Retention: 7 Tage
- Consent-Audio-Snippet wird zusätzlich gespeichert

### Standard Mode
- Strategie B für neue Speaker (Selective Recording)
- EU-Cloud erlaubt
- Transkript-Retention: 30 Tage
- Consent-Audio-Snippet optional

### Permissive Mode (mit Warnhinweis)
- Strategie B
- Cloud-Verarbeitung erlaubt (auch außerhalb EU)
- Transkript-Retention: 90 Tage
- Beim Aktivieren: Bestätigungsdialog mit Hinweis auf erhöhte rechtliche Verantwortung des Nutzers
- Empfehlung: zusätzliche In-App-Bestätigung pro Session

---

## 8. Implementierungsreihenfolge

### Phase 1: MVP
- Single-Speaker, einfacher Ring Buffer
- Rule-based Classifier
- Strict Mode only
- Pre-STT-Gate mit binärer Entscheidung
- Audit Log (basic)

### Phase 2: V1
- Multi-Speaker mit Strategie B
- LLM-Classifier-Fallback
- Audit Log mit Hash-Chain
- Post-STT Defense Filter

### Phase 3: V2
- Strategie A
- Cross-Talk-Handling
- Alle drei Sicherheits-Modi
- Voice-Embedding-Blocklist persistent über Sessions

### Phase 4: V3
- PII-Pseudonymisierung
- Cloud-Pipeline mit Anbieter-Abstraktion
- Revocation-Tooling
- Optionale Consent-Audio-Snippet-Persistierung

### Phase 5: V4
- Voice-Embedding-Persistence über Sessions hinweg (für „dieser Anrufer hat schon mal eingewilligt"-Flows)
- Erfordert eigene biometrische Einwilligung als zusätzlicher Consent-Scope
- Wiederverwendung gespeicherter Consents inkl. Verfallsfristen