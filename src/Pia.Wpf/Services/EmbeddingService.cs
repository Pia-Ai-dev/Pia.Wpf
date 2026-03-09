using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Pia.Services.Interfaces;

namespace Pia.Services;

public partial class EmbeddingService : IEmbeddingService, IDisposable
{
    private const string ModelFileName = "all-MiniLM-L6-v2.onnx";
    private const string TokenizerFileName = "tokenizer.json";
    private const string ModelUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
    private const string TokenizerUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/tokenizer.json";
    private const int MaxSequenceLength = 256;
    private const int EmbeddingDimension = 384;

    private readonly ILogger<EmbeddingService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _modelDirectory;
    private InferenceSession? _session;
    private Dictionary<string, int>? _vocabulary;
    private bool _disposed;

    public bool IsModelAvailable => File.Exists(ModelPath) && File.Exists(TokenizerPath);

    private string ModelPath => Path.Combine(_modelDirectory, ModelFileName);
    private string TokenizerPath => Path.Combine(_modelDirectory, TokenizerFileName);

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

            // Download tokenizer
            if (!File.Exists(TokenizerPath))
            {
                _logger.LogInformation("Downloading tokenizer...");
                await DownloadFileAsync(httpClient, TokenizerUrl, TokenizerPath, null, cancellationToken);
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

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
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

        _logger.LogInformation("Embedding model loaded successfully");
    }

    private void LoadVocabulary()
    {
        if (_vocabulary is not null) return;

        var tokenizerJson = File.ReadAllText(TokenizerPath);
        var doc = System.Text.Json.JsonDocument.Parse(tokenizerJson);

        _vocabulary = new Dictionary<string, int>();

        if (doc.RootElement.TryGetProperty("model", out var model) &&
            model.TryGetProperty("vocab", out var vocab))
        {
            foreach (var entry in vocab.EnumerateObject())
            {
                _vocabulary[entry.Name] = entry.Value.GetInt32();
            }
        }

        _logger.LogInformation("Loaded vocabulary with {Count} tokens", _vocabulary.Count);
    }

    private long[] Tokenize(string text)
    {
        if (_vocabulary is null)
            throw new InvalidOperationException("Vocabulary not loaded");

        var tokens = new List<long> { GetTokenId("[CLS]") };

        // Simple WordPiece tokenization
        var words = TokenizeRegex().Split(text.ToLowerInvariant())
            .Where(w => !string.IsNullOrWhiteSpace(w));

        foreach (var word in words)
        {
            var remaining = word;
            var isFirst = true;

            while (remaining.Length > 0)
            {
                var prefix = isFirst ? "" : "##";
                var found = false;

                // Try longest matching subword
                for (var end = remaining.Length; end > 0; end--)
                {
                    var subword = prefix + remaining[..end];
                    if (_vocabulary.ContainsKey(subword))
                    {
                        tokens.Add(GetTokenId(subword));
                        remaining = remaining[end..];
                        found = true;
                        isFirst = false;
                        break;
                    }
                }

                if (!found)
                {
                    // Unknown token
                    tokens.Add(GetTokenId("[UNK]"));
                    break;
                }
            }

            if (tokens.Count >= MaxSequenceLength - 1)
                break;
        }

        tokens.Add(GetTokenId("[SEP]"));

        // Pad or truncate to max sequence length
        if (tokens.Count > MaxSequenceLength)
        {
            tokens.RemoveRange(MaxSequenceLength - 1, tokens.Count - MaxSequenceLength);
            tokens.Add(GetTokenId("[SEP]"));
        }

        return tokens.ToArray();
    }

    private long GetTokenId(string token)
    {
        if (_vocabulary is not null && _vocabulary.TryGetValue(token, out var id))
            return id;
        return _vocabulary?.GetValueOrDefault("[UNK]", 100) ?? 100;
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

    [GeneratedRegex(@"[\s\p{P}]+")]
    private static partial Regex TokenizeRegex();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _session?.Dispose();
        _session = null;

        GC.SuppressFinalize(this);
    }
}
