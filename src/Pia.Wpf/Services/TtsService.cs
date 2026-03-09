using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Pia.Models;
using Pia.Services.Interfaces;
using PiperSharp;
using PiperSharp.Models;

namespace Pia.Services;

public class TtsService : ITtsService, IDisposable
{
    private static readonly List<TtsVoice> CuratedVoices =
    [
        new() { Key = "en_US-lessac-medium", DisplayName = "Lessac", Language = "English (US)", Quality = "Medium", Gender = "Male", SizeBytes = 63_000_000 },
        new() { Key = "en_US-amy-medium", DisplayName = "Amy", Language = "English (US)", Quality = "Medium", Gender = "Female", SizeBytes = 63_000_000 },
        new() { Key = "en_US-ryan-medium", DisplayName = "Ryan", Language = "English (US)", Quality = "Medium", Gender = "Male", SizeBytes = 63_000_000 },
        new() { Key = "en_GB-alba-medium", DisplayName = "Alba", Language = "English (GB)", Quality = "Medium", Gender = "Female", SizeBytes = 63_000_000 },
        new() { Key = "de_DE-thorsten-medium", DisplayName = "Thorsten", Language = "German", Quality = "Medium", Gender = "Male", SizeBytes = 63_000_000 },
        new() { Key = "de_DE-eva_k-x_low", DisplayName = "Eva", Language = "German", Quality = "Low", Gender = "Female", SizeBytes = 16_000_000 },
        new() { Key = "de_DE-ramona-low", DisplayName = "Ramona", Language = "German", Quality = "Low", Gender = "Female", SizeBytes = 16_000_000 },
        new() { Key = "fr_FR-siwis-medium", DisplayName = "Siwis", Language = "French", Quality = "Medium", Gender = "Female", SizeBytes = 63_000_000 },
        new() { Key = "fr_FR-upmc-medium", DisplayName = "UPMC", Language = "French", Quality = "Medium", Gender = "Male", SizeBytes = 63_000_000 },
    ];

    private const int FillerVersion = 2;

    private static readonly Dictionary<string, FillerPhraseSet> FillerPhrasesByLanguage = new()
    {
        ["en"] = new FillerPhraseSet
        {
            Short =
            [
                "Hmm, let me think.",
                "One moment.",
                "Okay, let me see.",
                "Right, give me a second.",
            ],
            Medium =
            [
                "That's a good question, let me think about that for a moment.",
                "Okay, I'm working through that now.",
                "Hmm, let me consider the best way to put this.",
                "Alright, I want to make sure I get this right.",
                "Let me think about how to approach this.",
                "Oh, that's interesting. Let me work through it.",
            ],
            Long =
            [
                "That's a really good question. Let me take a moment to think through all the details so I can give you a proper answer.",
                "Okay, there are a few things to consider here. Let me sort through them and figure out the best way to explain this.",
                "Hmm, I want to be thorough with this one. Give me just a moment while I think it through carefully.",
                "Alright, let me take a step back and think about this from the beginning so I can give you a complete answer.",
            ]
        },
        ["de"] = new FillerPhraseSet
        {
            Short =
            [
                "Hmm, Moment mal.",
                "Okay, lass mich kurz schauen.",
                "Einen Moment bitte.",
                "Ja, Sekunde.",
            ],
            Medium =
            [
                "Das ist eine gute Frage. Lass mich kurz darüber nachdenken.",
                "Okay, da muss ich kurz überlegen, wie ich das am besten erkläre.",
                "Hmm, lass mich mal schauen, wie ich das formuliere.",
                "Alles klar, ich denke gerade darüber nach.",
                "Moment, ich möchte sichergehen, dass ich das richtig beantworte.",
                "Ja, das ist interessant. Lass mich kurz überlegen.",
            ],
            Long =
            [
                "Das ist wirklich eine gute Frage. Gib mir einen kleinen Moment, damit ich das ordentlich durchdenken kann.",
                "Okay, da gibt es einiges zu bedenken. Lass mich das kurz sortieren, damit ich dir eine vollständige Antwort geben kann.",
                "Hmm, ich möchte da gründlich sein. Gib mir einen Augenblick, damit ich alles sorgfältig durchgehe.",
                "Also, lass mich mal von vorne anfangen und das Schritt für Schritt durchdenken.",
            ]
        },
        ["fr"] = new FillerPhraseSet
        {
            Short =
            [
                "Hmm, voyons voir.",
                "Un instant.",
                "D'accord, laissez-moi réfléchir.",
                "Bon, une seconde.",
            ],
            Medium =
            [
                "C'est une bonne question. Laissez-moi y réfléchir un instant.",
                "D'accord, je suis en train d'y penser.",
                "Hmm, laissez-moi trouver la meilleure façon de formuler cela.",
                "Alors, je veux m'assurer de bien répondre à votre question.",
                "Voyons, laissez-moi considérer les différentes possibilités.",
                "Ah, c'est intéressant. Donnez-moi un moment pour y réfléchir.",
            ],
            Long =
            [
                "C'est vraiment une excellente question. Laissez-moi prendre un moment pour bien réfléchir à tous les détails avant de vous répondre.",
                "D'accord, il y a plusieurs choses à prendre en compte ici. Laissez-moi organiser mes pensées pour vous donner une réponse complète.",
                "Hmm, je veux être bien précis sur ce point. Donnez-moi juste un instant pour bien réfléchir à tout cela.",
                "Alors, laissez-moi reprendre depuis le début et y réfléchir étape par étape pour vous donner une réponse correcte.",
            ]
        }
    };

    private record FillerPhraseSet
    {
        public required string[] Short { get; init; }
        public required string[] Medium { get; init; }
        public required string[] Long { get; init; }
        public string[] All => [.. Short, .. Medium, .. Long];
    }

    private readonly ILogger<TtsService> _logger;
    private readonly ISettingsService _settingsService;
    private readonly string _baseDirectory;
    private readonly string _piperDirectory;
    private readonly string _modelsDirectory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Random _random = new();

    private PiperProvider? _piperProvider;
    private CancellationTokenSource? _playbackCts;
    private string? _currentVoiceKey;
    private bool _isReady;
    private bool _isPlaying;
    private bool _disposed;

    // Filler support
    private readonly Dictionary<string, List<byte[]>> _fillerCache = new();
    private CancellationTokenSource? _fillerCts;
    private int _lastFillerIndex = -1;

    public bool IsReady => _isReady;
    public bool HasVoiceLoaded => _piperProvider is not null;
    public bool HasFillers => _currentVoiceKey is not null && _fillerCache.ContainsKey(_currentVoiceKey)
                              && _fillerCache[_currentVoiceKey].Count > 0;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (_isPlaying != value)
            {
                _isPlaying = value;
                IsPlayingChanged?.Invoke(this, value);
            }
        }
    }

    public event EventHandler<bool>? IsPlayingChanged;

    public TtsService(ILogger<TtsService> logger, ISettingsService settingsService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pia", "Piper");
        _piperDirectory = Path.Combine(_baseDirectory, "piper");
        _modelsDirectory = Path.Combine(_baseDirectory, "models");

        Directory.CreateDirectory(_baseDirectory);
        Directory.CreateDirectory(_modelsDirectory);
    }

    public async Task InitializeAsync(IProgress<TtsDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_isReady)
            return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_isReady)
                return;

            var piperExePath = GetPiperExePath();
            if (!File.Exists(piperExePath))
            {
                _logger.LogInformation("Downloading Piper executable...");
                progress?.Report(new TtsDownloadProgress("Downloading Piper engine...", 0));

                (await PiperDownloader.DownloadPiper()).ExtractPiper(_baseDirectory);

                progress?.Report(new TtsDownloadProgress("Piper engine ready", 100));
            }

            var settings = await _settingsService.GetSettingsAsync();
            var voiceKey = settings.TtsVoiceModelKey;

            if (IsVoiceDownloaded(voiceKey))
            {
                await LoadVoiceAsync(voiceKey, cancellationToken);
                _ = Task.Run(() => PreGenerateFillersAsync(CancellationToken.None));
            }

            _isReady = true;
            _logger.LogInformation("TTS service initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!_isReady)
            await InitializeAsync(cancellationToken: cancellationToken);

        if (_piperProvider is null)
        {
            _logger.LogWarning("No voice model loaded, cannot speak");
            return;
        }

        Stop();

        _playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _playbackCts.Token;

        try
        {
            IsPlaying = true;

            var audioBytes = await _piperProvider.InferAsync(text, AudioOutputType.Wav, token);

            if (token.IsCancellationRequested)
                return;

            await PlayWavBytesAsync(audioBytes, token);
        }
        catch (OperationCanceledException)
        {
            // Expected when Stop() is called
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TTS playback failed");
        }
        finally
        {
            _playbackCts?.Dispose();
            _playbackCts = null;
            IsPlaying = false;
        }
    }

    public async Task SpeakChunkedAsync(IAsyncEnumerable<string> sentences, CancellationToken cancellationToken = default)
    {
        if (_piperProvider is null)
        {
            _logger.LogWarning("No voice model loaded, cannot speak");
            return;
        }

        // Only stop previous speech playback, keep filler running
        _playbackCts?.Cancel();

        _playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _playbackCts.Token;

        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        try
        {
            IsPlaying = true;

            // Producer: synthesize sentences to WAV bytes and write to channel
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var sentence in sentences.WithCancellation(token))
                    {
                        if (string.IsNullOrWhiteSpace(sentence))
                            continue;

                        var audioBytes = await _piperProvider.InferAsync(sentence, AudioOutputType.Wav, token);
                        await channel.Writer.WriteAsync(audioBytes, token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TTS chunked synthesis failed");
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, token);

            // Consumer: play WAV bytes sequentially
            var isFirstChunk = true;
            await foreach (var audioBytes in channel.Reader.ReadAllAsync(token))
            {
                if (isFirstChunk)
                {
                    // Stop filler right when the first response audio is ready
                    _fillerCts?.Cancel();
                    isFirstChunk = false;
                }

                await PlayWavBytesAsync(audioBytes, token);
            }

            await producerTask;
        }
        catch (OperationCanceledException)
        {
            // Expected when Stop() is called
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TTS chunked playback failed");
        }
        finally
        {
            _playbackCts?.Dispose();
            _playbackCts = null;
            IsPlaying = false;
        }
    }

    public async Task PreGenerateFillersAsync(CancellationToken cancellationToken = default)
    {
        if (_piperProvider is null || _currentVoiceKey is null)
            return;

        if (_fillerCache.ContainsKey(_currentVoiceKey))
            return;

        var fillerDir = GetFillerDirectory(_currentVoiceKey);

        // Check version -- invalidate stale cache from older filler sets
        var versionFile = Path.Combine(fillerDir, "fillers_version.txt");
        if (Directory.Exists(fillerDir))
        {
            var isStale = !File.Exists(versionFile) ||
                          await File.ReadAllTextAsync(versionFile, cancellationToken) != FillerVersion.ToString();
            if (isStale)
            {
                _logger.LogInformation("Filler cache is stale for voice: {VoiceKey}, regenerating", _currentVoiceKey);
                Directory.Delete(fillerDir, true);
            }
        }

        // Try loading from disk first
        var cached = await LoadFillersFromDiskAsync(fillerDir, cancellationToken);
        if (cached.Count > 0)
        {
            _fillerCache[_currentVoiceKey] = cached;
            _logger.LogInformation("Loaded {Count} cached filler phrases from disk for voice: {VoiceKey}", cached.Count, _currentVoiceKey);
            return;
        }

        // Generate and persist
        var phraseSet = GetFillerPhraseSetForVoice(_currentVoiceKey);
        var phrases = phraseSet.All;
        _logger.LogInformation("Generating {Count} filler phrases for voice: {VoiceKey}", phrases.Length, _currentVoiceKey);
        Directory.CreateDirectory(fillerDir);

        var cache = new List<byte[]>();

        for (var i = 0; i < phrases.Length; i++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var audioBytes = await _piperProvider.InferAsync(phrases[i], AudioOutputType.Wav, cancellationToken);
                cache.Add(audioBytes);
                await File.WriteAllBytesAsync(Path.Combine(fillerDir, $"filler_{i}.wav"), audioBytes, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate filler: {Phrase}", phrases[i]);
            }
        }

        if (cache.Count > 0)
        {
            _fillerCache[_currentVoiceKey] = cache;
            await File.WriteAllTextAsync(versionFile, FillerVersion.ToString(), cancellationToken);
            _logger.LogInformation("Generated and cached {Count} filler phrases for voice: {VoiceKey}", cache.Count, _currentVoiceKey);
        }
    }

    private static FillerPhraseSet GetFillerPhraseSetForVoice(string voiceKey)
    {
        // Voice keys are formatted as "lang_REGION-name-quality", e.g. "en_US-lessac-medium"
        var langPrefix = voiceKey.Split('_')[0];
        return FillerPhrasesByLanguage.TryGetValue(langPrefix, out var set)
            ? set
            : FillerPhrasesByLanguage["en"];
    }

    private static async Task<List<byte[]>> LoadFillersFromDiskAsync(string fillerDir, CancellationToken cancellationToken)
    {
        var result = new List<byte[]>();
        if (!Directory.Exists(fillerDir))
            return result;

        var files = Directory.GetFiles(fillerDir, "filler_*.wav");
        Array.Sort(files);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(await File.ReadAllBytesAsync(file, cancellationToken));
        }

        return result;
    }

    private string GetFillerDirectory(string voiceKey)
        => Path.Combine(GetModelDirectory(voiceKey), "fillers");

    public async Task PlayFillerAsync(CancellationToken cancellationToken = default)
    {
        if (_currentVoiceKey is null || !_fillerCache.TryGetValue(_currentVoiceKey, out var fillerData) || fillerData.Count == 0)
            return;

        _fillerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _fillerCts.Token;

        try
        {
            var phraseSet = GetFillerPhraseSetForVoice(_currentVoiceKey);
            var shortCount = phraseSet.Short.Length;

            // First filler: pick a short one for quick acknowledgment
            var firstIndex = PickFillerIndex(shortCount);
            await PlaySingleFillerAsync(fillerData[firstIndex], token);

            // Subsequent fillers: pick from medium/long for more substance
            while (!token.IsCancellationRequested)
            {
                // Wait before playing another filler
                await Task.Delay(3000, token);

                var poolStart = shortCount;
                var poolSize = fillerData.Count - shortCount;
                if (poolSize <= 0)
                {
                    poolStart = 0;
                    poolSize = fillerData.Count;
                }

                var idx = poolStart + PickFillerIndex(poolSize);
                if (idx >= fillerData.Count) idx = _random.Next(fillerData.Count);

                await PlaySingleFillerAsync(fillerData[idx], token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when Stop() is called
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Filler playback failed");
        }
        finally
        {
            _fillerCts?.Dispose();
            _fillerCts = null;
        }
    }

    private int PickFillerIndex(int count)
    {
        if (count <= 1) return 0;
        int idx;
        do { idx = _random.Next(count); } while (idx == _lastFillerIndex);
        _lastFillerIndex = idx;
        return idx;
    }

    private static async Task PlaySingleFillerAsync(byte[] audioBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var memoryStream = new MemoryStream(audioBytes);
        var waveReader = new WaveFileReader(memoryStream);
        var waveOut = new WaveOutEvent();

        var tcs = new TaskCompletionSource();
        waveOut.PlaybackStopped += (_, _) =>
        {
            tcs.TrySetResult();
        };

        try
        {
            waveOut.Init(waveReader);
            waveOut.Play();

            using var registration = token.Register(() => waveOut.Stop());
            await tcs.Task;
        }
        finally
        {
            waveOut.Dispose();
            waveReader.Dispose();
            memoryStream.Dispose();
        }
    }

    public void Stop()
    {
        _playbackCts?.Cancel();
        _fillerCts?.Cancel();
        IsPlaying = false;
    }

    public Task<IReadOnlyList<TtsVoice>> GetAvailableVoicesAsync(CancellationToken cancellationToken = default)
    {
        var voices = CuratedVoices.Select(v => new TtsVoice
        {
            Key = v.Key,
            DisplayName = v.DisplayName,
            Language = v.Language,
            Quality = v.Quality,
            Gender = v.Gender,
            SizeBytes = v.SizeBytes,
            IsDownloaded = IsVoiceDownloaded(v.Key)
        }).ToList();

        return Task.FromResult<IReadOnlyList<TtsVoice>>(voices);
    }

    public async Task DownloadVoiceAsync(string voiceKey, IProgress<TtsDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsVoiceDownloaded(voiceKey))
            return;

        _logger.LogInformation("Downloading voice model: {VoiceKey}", voiceKey);
        progress?.Report(new TtsDownloadProgress("Downloading voice model...", 0));

        var previousDir = Directory.GetCurrentDirectory();
        try
        {
            // PiperSharp downloads models relative to CWD
            Directory.SetCurrentDirectory(_baseDirectory);

            progress?.Report(new TtsDownloadProgress("Downloading voice model...", 10));
            await PiperDownloader.DownloadModelByKey(voiceKey);
            progress?.Report(new TtsDownloadProgress("Voice model ready", 100));

            _logger.LogInformation("Voice model downloaded: {VoiceKey}", voiceKey);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDir);
        }
    }

    public async Task SetVoiceAsync(string voiceKey, CancellationToken cancellationToken = default)
    {
        if (!IsVoiceDownloaded(voiceKey))
            throw new InvalidOperationException($"Voice model '{voiceKey}' is not downloaded.");

        Stop();
        await LoadVoiceAsync(voiceKey, cancellationToken);

        var settings = await _settingsService.GetSettingsAsync();
        settings.TtsVoiceModelKey = voiceKey;
        await _settingsService.SaveSettingsAsync(settings);

        // Fire-and-forget filler generation for the new voice
        _ = Task.Run(() => PreGenerateFillersAsync(CancellationToken.None));
    }

    private static async Task PlayWavBytesAsync(byte[] audioBytes, CancellationToken cancellationToken)
    {
        var memoryStream = new MemoryStream(audioBytes);
        var waveReader = new WaveFileReader(memoryStream);
        var waveOut = new WaveOutEvent();

        var tcs = new TaskCompletionSource();
        waveOut.PlaybackStopped += (_, _) => tcs.TrySetResult();

        try
        {
            waveOut.Init(waveReader);
            waveOut.Play();

            using var registration = cancellationToken.Register(() => waveOut.Stop());
            await tcs.Task;
        }
        finally
        {
            waveOut.Dispose();
            waveReader.Dispose();
            memoryStream.Dispose();
        }
    }

    private async Task LoadVoiceAsync(string voiceKey, CancellationToken cancellationToken)
    {
        var modelDir = GetModelDirectory(voiceKey);
        var model = await VoiceModel.LoadModel(modelDir);

        _piperProvider = new PiperProvider(new PiperConfiguration
        {
            ExecutableLocation = GetPiperExePath(),
            WorkingDirectory = _piperDirectory,
            Model = model
        });

        _currentVoiceKey = voiceKey;
        _logger.LogInformation("Voice model loaded: {VoiceKey}", voiceKey);
    }

    private bool IsVoiceDownloaded(string voiceKey)
    {
        var modelDir = GetModelDirectory(voiceKey);
        if (!Directory.Exists(modelDir))
            return false;

        // Check for .onnx file in the model directory
        return Directory.GetFiles(modelDir, "*.onnx").Length > 0;
    }

    private string GetModelDirectory(string voiceKey)
        => Path.Combine(_modelsDirectory, voiceKey);

    private string GetPiperExePath()
        => Path.Combine(_piperDirectory, "piper.exe");

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Stop();
        _fillerCache.Clear();
        _initLock.Dispose();

        GC.SuppressFinalize(this);
    }
}
