using System.Diagnostics;
using System.Numerics.Tensors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using CodeGraph.Data;

namespace CodeGraph.Services.Embeddings;

/// <summary>
/// Generates text embeddings using an ONNX model (e.g. all-MiniLM-L6-v2).
/// The model is loaded lazily on first use. If no model path is configured,
/// IsAvailable returns false and all operations return zero vectors.
/// </summary>
public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly CodeGraphStorageOptions _options;
    private readonly ILogger<OnnxEmbeddingService> _logger;
    private InferenceSession? _session;
    private BertTokenizer? _bertTokenizer;
    private bool _initialized;
    private readonly object _lock = new();

    public OnnxEmbeddingService(IOptions<CodeGraphStorageOptions> optionsAccessor, ILogger<OnnxEmbeddingService> logger)
    {
        _options = optionsAccessor.Value;
        _logger = logger;
    }

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _session is not null;
        }
    }

    public int Dimensions => _options.EmbeddingDimensions;

    public float[] GenerateEmbedding(string text)
    {
        EnsureInitialized();
        if (_session is null)
            return new float[Dimensions];

        var sw = Stopwatch.StartNew();
        var result = RunInference(text);
        sw.Stop();
        _logger.LogInformation("Generated 1 embedding in {ElapsedMs}ms", sw.ElapsedMilliseconds);
        return result;
    }

    public IReadOnlyList<float[]> GenerateEmbeddings(IReadOnlyList<string> texts)
    {
        EnsureInitialized();
        if (_session is null)
            return texts.Select(_ => new float[Dimensions]).ToList();

        var sw = Stopwatch.StartNew();

        // Process in batches to limit memory usage while still batching inference
        const int maxBatchSize = 64;
        var allResults = new List<float[]>(texts.Count);

        for (int offset = 0; offset < texts.Count; offset += maxBatchSize)
        {
            var batchTexts = texts.Skip(offset).Take(maxBatchSize).ToList();
            var batchResults = RunBatchInference(batchTexts);
            allResults.AddRange(batchResults);
        }

        sw.Stop();
        _logger.LogInformation("Generated {Count} embeddings in {ElapsedMs}ms ({AvgMs:F1}ms avg)",
            texts.Count, sw.ElapsedMilliseconds,
            texts.Count > 0 ? (double)sw.ElapsedMilliseconds / texts.Count : 0);

        return allResults;
    }

    private float[] RunInference(string text)
    {
        if (_session is null)
            return new float[Dimensions];

        // Simple tokenization: split on whitespace/punctuation and map to token IDs
        // For production, use a proper BPE tokenizer matching the model
        var tokens = Tokenize(text);
        var inputIds = new long[tokens.Length];
        var attentionMask = new long[tokens.Length];
        var tokenTypeIds = new long[tokens.Length];

        for (int i = 0; i < tokens.Length; i++)
        {
            inputIds[i] = tokens[i];
            attentionMask[i] = 1;
            tokenTypeIds[i] = 0;
        }

        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, tokens.Length]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, tokens.Length]);
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, [1, tokens.Length]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
        };

        try
        {
            using var results = _session.Run(inputs);
            var output = results.First().AsTensor<float>();

            // Mean pooling over token dimension
            var embedding = new float[Dimensions];
            var tokenCount = tokens.Length;

            for (int t = 0; t < tokenCount; t++)
            {
                for (int d = 0; d < Dimensions && d < output.Dimensions[2]; d++)
                {
                    embedding[d] += output[0, t, d];
                }
            }

            for (int d = 0; d < Dimensions; d++)
                embedding[d] /= tokenCount;

            // L2 normalize
            var norm = MathF.Sqrt(embedding.Sum(x => x * x));
            if (norm > 0)
            {
                for (int d = 0; d < Dimensions; d++)
                    embedding[d] /= norm;
            }

            return embedding;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ONNX inference failed for text of length {Length}", text.Length);
            return new float[Dimensions];
        }
    }

    private IReadOnlyList<float[]> RunBatchInference(IReadOnlyList<string> texts)
    {
        if (_session is null || texts.Count == 0)
            return texts.Select(_ => new float[Dimensions]).ToList();

        try
        {
            // Tokenize all texts and find max length for padding
            var allTokens = texts.Select(t => Tokenize(t)).ToList();
            var maxLen = allTokens.Max(t => t.Length);
            var batchSize = texts.Count;

            // Build padded tensors: [batchSize, maxLen]
            var inputIds = new long[batchSize * maxLen];
            var attentionMask = new long[batchSize * maxLen];
            var tokenTypeIds = new long[batchSize * maxLen];

            for (int b = 0; b < batchSize; b++)
            {
                var tokens = allTokens[b];
                for (int t = 0; t < maxLen; t++)
                {
                    var idx = b * maxLen + t;
                    if (t < tokens.Length)
                    {
                        inputIds[idx] = tokens[t];
                        attentionMask[idx] = 1;
                    }
                    // else: already 0 (padding)
                }
            }

            var inputIdsTensor = new DenseTensor<long>(inputIds, [batchSize, maxLen]);
            var attentionMaskTensor = new DenseTensor<long>(attentionMask, [batchSize, maxLen]);
            var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, [batchSize, maxLen]);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
                NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
            };

            using var results = _session.Run(inputs);
            var output = results.First().AsTensor<float>();

            var embeddings = new List<float[]>(batchSize);
            for (int b = 0; b < batchSize; b++)
            {
                var tokenCount = allTokens[b].Length;
                var embedding = new float[Dimensions];

                // Mean pooling over non-padded tokens only
                for (int t = 0; t < tokenCount; t++)
                {
                    for (int d = 0; d < Dimensions && d < output.Dimensions[2]; d++)
                    {
                        embedding[d] += output[b, t, d];
                    }
                }

                for (int d = 0; d < Dimensions; d++)
                    embedding[d] /= tokenCount;

                // L2 normalize
                var norm = MathF.Sqrt(embedding.Sum(x => x * x));
                if (norm > 0)
                {
                    for (int d = 0; d < Dimensions; d++)
                        embedding[d] /= norm;
                }

                embeddings.Add(embedding);
            }

            return embeddings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch ONNX inference failed for {Count} texts, falling back to individual", texts.Count);
            return texts.Select(RunInference).ToList();
        }
    }

    private int[] Tokenize(string text, int maxLength = 128)
    {
        if (_bertTokenizer is not null)
        {
            var encoded = _bertTokenizer.EncodeToIds(text, maxLength,
                out _, out _);
            return encoded.ToArray();
        }

        // Fallback if no tokenizer loaded (shouldn't happen when IsAvailable is true)
        return FallbackTokenize(text, maxLength);
    }

    private static int[] FallbackTokenize(string text, int maxLength = 128)
    {
        // CLS=101, SEP=102; split on whitespace/punctuation and hash to vocab range.
        // Only used when tokenizer files are missing — embeddings will be low quality.
        var words = text.ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', '.', ',', ';', ':', '(', ')', '{', '}', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries);

        var tokens = new List<int> { 101 };
        foreach (var word in words)
        {
            if (tokens.Count >= maxLength - 1) break;
            tokens.Add(1000 + (Math.Abs(word.GetHashCode()) % 29000));
        }
        tokens.Add(102);

        return tokens.ToArray();
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_lock)
        {
            if (_initialized) return;
            _initialized = true;

            var modelPath = _options.EmbeddingModelPath;
            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            {
                _logger.LogInformation("No ONNX embedding model configured or file not found at {Path}. " +
                    "Embedding-based search will be disabled.", modelPath);
                return;
            }

            try
            {
                var sessionOptions = new SessionOptions();
                sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                _session = new InferenceSession(modelPath, sessionOptions);

                // Load BERT WordPiece tokenizer from the model directory
                var modelDir = Path.GetDirectoryName(modelPath);
                if (modelDir is not null)
                {
                    var vocabPath = Path.Combine(modelDir, "vocab.txt");
                    if (File.Exists(vocabPath))
                    {
                        _bertTokenizer = BertTokenizer.Create(vocabPath, new BertOptions
                        {
                            LowerCaseBeforeTokenization = true
                        });
                        _logger.LogInformation("BERT tokenizer loaded from {Path}", vocabPath);
                    }
                    else
                    {
                        _logger.LogWarning("No vocab.txt found at {Path}. Using fallback tokenizer — embedding quality will be degraded.", vocabPath);
                    }
                }

                _logger.LogInformation("ONNX embedding model loaded from {Path} ({Model}, {Dims} dimensions)",
                    modelPath, _options.EmbeddingModelName, _options.EmbeddingDimensions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load ONNX model from {Path}", modelPath);
            }
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
