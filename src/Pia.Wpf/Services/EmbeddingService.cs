using System.IO;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class EmbeddingService : IEmbeddingService, IDisposable
{
    private const string ModelFileName = "paraphrase-multilingual-MiniLM-L12-v2.onnx";
    private const string TokenizerFileName = "tokenizer.json";
    private const string SentencePieceFileName = "sentencepiece.bpe.model";
    private const string ModelUrl = "https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/main/onnx/model.onnx";
    private const string TokenizerUrl = "https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/main/tokenizer.json";
    private const string SentencePieceUrl = "https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/main/sentencepiece.bpe.model";
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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _modelDirectory;
    private InferenceSession? _session;
    // The SentencePiece model does the (normalization + Unigram Viterbi) segmentation; _vocabulary maps the
    // resulting piece strings to the ids the ONNX embedding table expects. tokenizer.json is the id oracle:
    // its model.vocab is an ARRAY whose index IS the model id, which sidesteps XLM-R's fairseq +1 offset
    // (the raw sentencepiece.bpe.model numbers pieces differently).
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

    public EmbeddingService(ILogger<EmbeddingService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _modelDirectory = Path.Combine(localAppData, "Pia", "Models", "Embeddings");
        Directory.CreateDirectory(_modelDirectory);
    }

    public async Task<bool> DownloadModelAsync(
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10);

            // Download model file
            if (!File.Exists(ModelPath))
            {
                _logger.LogInformation("Downloading embedding model...");
                await DownloadFileAsync(httpClient, ModelUrl, ModelPath, progress, cancellationToken);
            }

            // Download tokenizer (the id oracle: piece string -> model id)
            if (!File.Exists(TokenizerPath))
            {
                _logger.LogInformation("Downloading tokenizer...");
                await DownloadFileAsync(httpClient, TokenizerUrl, TokenizerPath, null, cancellationToken);
            }

            // Download the SentencePiece model (the segmenter)
            if (!File.Exists(SentencePiecePath))
            {
                _logger.LogInformation("Downloading SentencePiece model...");
                await DownloadFileAsync(httpClient, SentencePieceUrl, SentencePiecePath, null, cancellationToken);
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
            throw new InvalidOperationException("Embedding model is not available and could not be downloaded.");
        }
        EnsureModelLoaded();

        return await Task.Run(() =>
        {
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
        if (_session is not null) return;

        if (!IsModelAvailable)
            throw new InvalidOperationException("Embedding model is not available. Call DownloadModelAsync first.");

        var sessionOptions = new SessionOptions();
        sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _session = new InferenceSession(ModelPath, sessionOptions);

        LoadVocabulary();

        // Load the SentencePiece model purely as a segmenter. We frame <s>/</s> ourselves (per the
        // tokenizer's TemplateProcessing) and map pieces to ids via _vocabulary, so the beginning/end
        // tokens the library would otherwise emit (with the SentencePiece model's own numbering) are
        // suppressed here.
        using var spStream = File.OpenRead(SentencePiecePath);
        _tokenizer = SentencePieceTokenizer.Create(
            spStream,
            addBeginningOfSentence: false,
            addEndOfSentence: false);

        _logger.LogInformation("Embedding model loaded successfully");
    }

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

    private async Task DownloadFileAsync(
        HttpClient httpClient,
        string url,
        string destinationPath,
        IProgress<float>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var tempPath = destinationPath + ".tmp";

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            if (totalBytes > 0)
            {
                progress?.Report((float)totalRead / totalBytes);
            }
        }

        fileStream.Close();
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
