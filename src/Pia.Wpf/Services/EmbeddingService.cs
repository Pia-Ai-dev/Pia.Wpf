using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Pia.Paths;
using Pia.Services.Assets;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class EmbeddingService : IEmbeddingService, IDisposable
{
    internal const string ModelFileName = "paraphrase-multilingual-MiniLM-L12-v2.onnx";
    internal const string TokenizerFileName = "tokenizer.json";
    internal const string SentencePieceFileName = "sentencepiece.bpe.model";
    internal const string ModelUrl = "https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/main/onnx/model.onnx";
    internal const string TokenizerUrl = "https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/main/tokenizer.json";
    internal const string SentencePieceUrl = "https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/main/sentencepiece.bpe.model";
    // Matches the tokenizer's own truncation (tokenizer.json truncation.max_length = 128), including the two
    // framing tokens (<s> … </s>). The model is paraphrase-multilingual-MiniLM-L12-v2, an XLM-RoBERTa
    // SentencePiece Unigram model — NOT BERT WordPiece.
    private const int MaxSequenceLength = 128;
    private const int EmbeddingDimension = 384;

    // XLM-RoBERTa special-token ids as they appear in tokenizer.json (the ids the ONNX model expects).
    // Read from the vocabulary at load time; these are the defaults if a key is somehow missing.
    private const int DefaultBosId = 0; // <s>
    private const int DefaultEosId = 2; // </s>
    private const int DefaultUnkId = 3; // <unk>

    private readonly ILogger<EmbeddingService> _logger;
    private readonly IAssetDownloader _downloader;
    private readonly string _modelDirectory;
    private readonly Func<string, InferenceSession> _sessionFactory;
    private InferenceSession? _session;
    // A failed type initializer stays failed for the process, so retrying ONNX cannot succeed.
    private Exception? _runtimeFailure;
    // The SentencePiece model does the (normalization + Unigram Viterbi) segmentation; _vocabulary maps the
    // resulting piece strings to the ids the ONNX embedding table expects. tokenizer.json is the id oracle:
    // its model.vocab is an ARRAY whose index IS the model id, which sidesteps XLM-R's fairseq +1 offset
    // (the raw sentencepiece.bpe.model numbers pieces differently).
    private readonly object _loadGate = new();
    private Tokenizer? _tokenizer;
    private Dictionary<string, int>? _vocabulary;
    private int _bosId = DefaultBosId;
    private int _eosId = DefaultEosId;
    private int _unkId = DefaultUnkId;
    private bool _disposed;

    public bool IsModelAvailable =>
        File.Exists(ModelPath) && File.Exists(TokenizerPath) && File.Exists(SentencePiecePath);

    private string ModelPath => Path.Combine(_modelDirectory, ModelFileName);
    private string TokenizerPath => Path.Combine(_modelDirectory, TokenizerFileName);
    private string SentencePiecePath => Path.Combine(_modelDirectory, SentencePieceFileName);

    public EmbeddingService(ILogger<EmbeddingService> logger, IAssetDownloader downloader)
        : this(logger, downloader, Path.Combine(PiaPaths.ModelsDirectory, "Embeddings"))
    {
    }

    internal EmbeddingService(
        ILogger<EmbeddingService> logger,
        IAssetDownloader downloader,
        string modelDirectory,
        Func<string, InferenceSession>? sessionFactory = null)
    {
        _logger = logger;
        _downloader = downloader;
        _modelDirectory = modelDirectory;
        _sessionFactory = sessionFactory ?? CreateSession;
        Directory.CreateDirectory(_modelDirectory);
    }

    public async Task<bool> DownloadModelAsync(
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Download model file
            if (!File.Exists(ModelPath))
            {
                _logger.LogInformation("Downloading embedding model...");
                await DownloadFileAsync(RuntimeAssetCatalog.EmbeddingModel, ModelPath, progress, cancellationToken);
            }

            // Download tokenizer (the id oracle: piece string -> model id)
            if (!File.Exists(TokenizerPath))
            {
                _logger.LogInformation("Downloading tokenizer...");
                await DownloadFileAsync(RuntimeAssetCatalog.EmbeddingTokenizer, TokenizerPath, null, cancellationToken);
            }

            // Download the SentencePiece model (the segmenter)
            if (!File.Exists(SentencePiecePath))
            {
                _logger.LogInformation("Downloading SentencePiece model...");
                await DownloadFileAsync(RuntimeAssetCatalog.EmbeddingSentencePiece, SentencePiecePath, null, cancellationToken);
            }

            _logger.LogInformation("Embedding model downloaded successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download embedding model");
            return false;
        }
    }

    public async Task<bool> EnsureAvailableAsync(
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsModelAvailable) return true;

        _logger.LogInformation("Embedding model missing - auto-downloading");
        return await DownloadModelAsync(progress, cancellationToken);
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAvailableAsync(progress: null, cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new EmbeddingUnavailableException("Embedding model is not available and could not be downloaded.");
        }
        return await Task.Run(() =>
        {
            // Inside Task.Run so a first-touch load (ONNX session + vocabulary, seconds) blocks a
            // pool thread, not the caller — vault-watcher timers and UI-driven recall both land here.
            EnsureModelLoaded();

            cancellationToken.ThrowIfCancellationRequested();

            var tokenIds = Tokenize(text);

            var inputIds = new DenseTensor<long>(new[] { 1, tokenIds.Length });
            var attentionMask = new DenseTensor<long>(new[] { 1, tokenIds.Length });
            var tokenTypeIds = new DenseTensor<long>(new[] { 1, tokenIds.Length });

            for (var i = 0; i < tokenIds.Length; i++)
            {
                inputIds[0, i] = tokenIds[i];
                attentionMask[0, i] = 1;
                tokenTypeIds[0, i] = 0;
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
                NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds)
            };

            using var results = _session!.Run(inputs);

            // Get the last hidden state (token_embeddings output)
            var output = results.First().AsTensor<float>();

            // Mean pooling over the sequence dimension
            var embedding = MeanPooling(output, tokenIds.Length);

            // L2 normalize
            Normalize(embedding);

            return embedding;
        }, cancellationToken);
    }

    public byte[] FloatsToBytes(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private void EnsureModelLoaded()
    {
        // Exclusive: vault-watcher callbacks race on first use, and a double load corrupts _vocabulary.
        lock (_loadGate)
        {
            if (_session is not null) return;
            if (_runtimeFailure is not null) throw RuntimeUnavailable(_runtimeFailure);

            if (!IsModelAvailable)
                throw new EmbeddingUnavailableException("Embedding model is not available. Call DownloadModelAsync first.");

            var session = CreateSessionOrLatchFailure();
            try
            {
                LoadVocabulary();

                // We frame <s>/</s> ourselves, so the segmenter's own, differently numbered ones stay off.
                using var spStream = File.OpenRead(SentencePiecePath);
                _tokenizer = SentencePieceTokenizer.Create(
                    spStream,
                    addBeginningOfSentence: false,
                    addEndOfSentence: false);
            }
            catch
            {
                session.Dispose();
                throw;
            }

            // Last: _session is the loaded-guard.
            _session = session;

            _logger.LogInformation("Embedding model loaded successfully");
        }
    }

    private InferenceSession CreateSessionOrLatchFailure()
    {
        try
        {
            return _sessionFactory(ModelPath);
        }
        catch (Exception ex) when (ex is TypeInitializationException or DllNotFoundException)
        {
            _runtimeFailure = ex;
            var runtimePath = Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll");
            _logger.LogError(
                "ONNX Runtime failed to load ({Failure}; onnxruntime.dll present: {Present}, Win32 error {Win32Error}). " +
                "Semantic recall and speech stay off until Pia restarts",
                Describe(ex), File.Exists(runtimePath), NativeLoadProbe.GetLoadError(runtimePath));
            throw RuntimeUnavailable(ex);
        }
    }

    // The options must be built inside the factory: without the runtime, new SessionOptions() is what throws.
    private static InferenceSession CreateSession(string modelPath)
    {
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        return new InferenceSession(modelPath, options);
    }

    private static EmbeddingUnavailableException RuntimeUnavailable(Exception cause) =>
        new("ONNX Runtime could not be loaded; embeddings stay off until Pia restarts.", cause);

    // Type names only: exception messages can quote install paths.
    private static string Describe(Exception ex) =>
        ex.InnerException is null ? ex.GetType().Name : $"{ex.GetType().Name} -> {ex.InnerException.GetType().Name}";

    private void LoadVocabulary()
    {
        if (_vocabulary is not null) return;

        var tokenizerJson = File.ReadAllText(TokenizerPath);
        var doc = System.Text.Json.JsonDocument.Parse(tokenizerJson);

        _vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);

        // model.vocab is a Unigram vocabulary: an ARRAY of [token, score] pairs. The array index is the
        // token's model id, so we ignore the score and use the position.
        if (doc.RootElement.TryGetProperty("model", out var model) &&
            model.TryGetProperty("vocab", out var vocab) &&
            vocab.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var id = 0;
            foreach (var entry in vocab.EnumerateArray())
            {
                var token = entry[0].GetString();
                if (token is not null)
                    _vocabulary[token] = id;
                id++;
            }
        }

        _bosId = _vocabulary.GetValueOrDefault("<s>", DefaultBosId);
        _eosId = _vocabulary.GetValueOrDefault("</s>", DefaultEosId);
        _unkId = _vocabulary.GetValueOrDefault("<unk>", DefaultUnkId);

        _logger.LogInformation("Loaded vocabulary with {Count} tokens", _vocabulary.Count);
    }

    private long[] Tokenize(string text)
    {
        if (_vocabulary is null || _tokenizer is null)
            throw new InvalidOperationException("Tokenizer not loaded");

        // The XLM-RoBERTa post-processor frames the sequence as: <s> … </s>. Reserve those two slots so a
        // full sequence is exactly MaxSequenceLength tokens; break once the content fills the remainder.
        var tokens = new List<long>(MaxSequenceLength) { _bosId };

        foreach (var piece in _tokenizer.EncodeToTokens(text, out _))
        {
            if (tokens.Count >= MaxSequenceLength - 1)
                break;

            // Map the SentencePiece piece string to the id the ONNX model expects (tokenizer.json index).
            tokens.Add(_vocabulary.TryGetValue(piece.Value, out var id) ? id : _unkId);
        }

        tokens.Add(_eosId);
        return tokens.ToArray();
    }

    private static float[] MeanPooling(Tensor<float> output, int sequenceLength)
    {
        var embedding = new float[EmbeddingDimension];
        var dimensions = output.Dimensions.ToArray();

        // output shape: [1, seq_len, hidden_size]
        var hiddenSize = dimensions.Length >= 3 ? dimensions[2] : EmbeddingDimension;
        var actualDim = Math.Min(hiddenSize, EmbeddingDimension);

        for (var i = 0; i < actualDim; i++)
        {
            float sum = 0;
            for (var j = 0; j < sequenceLength; j++)
            {
                sum += output[0, j, i];
            }
            embedding[i] = sum / sequenceLength;
        }

        return embedding;
    }

    private static void Normalize(float[] vector)
    {
        float norm = 0;
        foreach (var v in vector)
            norm += v * v;

        norm = MathF.Sqrt(norm);
        if (norm <= 0) return;

        for (var i = 0; i < vector.Length; i++)
            vector[i] /= norm;
    }

    // The shared downloader reports ModelDownloadProgress; this service's callers are on IProgress<float>,
    // and unifying the two would ripple into the vault UI for nothing.
    private async Task DownloadFileAsync(
        RuntimeAsset asset,
        string destinationPath,
        IProgress<float>? progress,
        CancellationToken cancellationToken)
    {
        var adapter = progress is null
            ? null
            : new Progress<ModelDownloadProgress>(p => progress.Report(p.PercentComplete / 100f));

        var tempPath = destinationPath + ".tmp";
        await _downloader.DownloadAsync(asset, tempPath, adapter, cancellationToken);
        File.Move(tempPath, destinationPath, overwrite: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _session?.Dispose();
        _session = null;

        GC.SuppressFinalize(this);
    }
}
