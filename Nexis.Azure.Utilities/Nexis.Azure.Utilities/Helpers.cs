using System;
using System.Buffers;
using System.Collections;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.Shares.Models;
using CliWrap;
using Command = System.CommandLine.Command;

namespace Nexis.Azure.Utilities;

public static class Helpers
{
    public static ImmutableDictionary<string, string> EmptyStringMap = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    public static IDictionary<string, string> Tags(this BlobItem b) => b.Tags ?? EmptyStringMap;
    public static IDictionary<string, string> Metadata(this BlobItem b) => b.Metadata ?? EmptyStringMap;

    public static readonly Regex VariableSeparatorPattern = new Regex(@"[\._\-]");
    public static readonly Regex VariablePattern = new Regex(@"\$\(([\w\._\-]+)\)");

    internal static class Strings
    {
        public const string tagPrefix = "ghostd_";
        public const string last_refresh_time = $"{tagPrefix}refresh_time";
        public const string last_access = $"{tagPrefix}last_access";
        public const string snapshot = $"{tagPrefix}snapshot";
        public const string state = $"{tagPrefix}state";
        public const string block_prefix = $"{tagPrefix}block_prefix";
        public const string size = $"{tagPrefix}size";
        public const string dirty_time = $"{tagPrefix}dirty_time";
        public const string dir_metadata = "hdi_isfolder";
        public const string mtime_metadata = "mtime";
    }

    public static async Task<int> ExecAsync(string processName, string[] args, PipeTarget? target = null)
    {
        var cmd = Cli.Wrap(processName)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.ZeroExitCode);

        if (target != null)
        {
            cmd = cmd.WithStandardOutputPipe(target);
        }

        var result = await cmd.ExecuteAsync();

        return result.ExitCode;
    }

    public static IAsyncEnumerable<T> AsyncEnum<T>(Func<IAsyncEnumerable<T>> enumerate)
    {
        return enumerate();
    }

    public static string PrepPath(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        EnsureParentDirectory(path);
        return path;
    }

    public static void EnsureParentDirectory(string path) => Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    public static string[] SplitArgs(this string args) => CommandLineStringSplitter.Instance.Split(args).ToArray();

    public static string GetDownloadTranslationTargetPath(string targetFolder, LanguageCode language, FileType type)
        => Path.Combine(targetFolder, $"{language}.audio.{type}");

    public static Guid GenerateGuidFromString(string input)
    {
        // Use SHA-1 to hash the input string
        using (MD5 hasher = MD5.Create())
        {
            byte[] hashBytes = hasher.ComputeHash(Encoding.UTF8.GetBytes(input));

            // Create a new GUID from the first 16 bytes of the hash
            byte[] guidBytes = new byte[16];
            Array.Copy(hashBytes, guidBytes, 16);

            return new Guid(guidBytes);
        }
    }

    public static string UriCombine(string? baseUri, string relativeUri)
    {
        baseUri ??= "";
        string path;
        if (string.IsNullOrEmpty(relativeUri))
        {
            path = baseUri;
        }
        else if (string.IsNullOrEmpty(baseUri))
        {
            path = relativeUri.TrimStart('/');
        }
        else if (relativeUri.Contains(':'))
        {
            // This is actually a full uri. Just return it.
            path = relativeUri;
        }
        else
        {
            path = $"{baseUri.TrimEnd('/')}/{relativeUri.TrimStart('/')}";
        }

        return path;
    }

    public static Timestamp? GetFileLastModifiedTime(this BlobItem blobItem)
    {
        return blobItem.Metadata()!.ValueOrDefault(Strings.mtime_metadata)
            ?? (Timestamp?)blobItem.Properties.LastModified;
    }

    public static ShareFileHttpHeaders ToHttpHeaders(this ShareFileProperties properties)
    {
        return new ShareFileHttpHeaders
        {
            ContentType = properties.ContentType,
            ContentEncoding = properties.ContentEncoding?.ToArray(),
            ContentLanguage = properties.ContentLanguage?.ToArray(),
            ContentDisposition = properties.ContentDisposition,
            CacheControl = properties.CacheControl,
            ContentHash = properties.ContentHash
        };
    }

    public static string ExpandVariables(string input, Out<IEnumerable<string>> missingVariables = default, bool requireAll = false)
    {
        missingVariables.Value = Enumerable.Empty<string>();

        if (string.IsNullOrEmpty(input))
            return input;

        List<string> missingVars = null;

        // Replace matches with environment variable values
        string result = VariablePattern.Replace(input, match =>
        {
            string variableName = match.Groups[1].Value;
            string envName = AsEnvironmentVariableName(variableName);
            string? envValue = GetEnvironmentVariable(envName);

            if (string.IsNullOrEmpty(envValue))
            {
                missingVars ??= new();
                missingVars.Add(envName);
                return variableName;
            }

            return envValue;
        });

        if (missingVars != null)
        {
            missingVariables.Value = missingVars;
            if (requireAll)
            {
                return null;
            }
        }

        return result;
    }

    public static async Task<long> CopyToAsync(this Stream sourceStream, Stream targetStream, IConsole console, CancellationToken token = default, int bufferSize = 1 << 20)
    {
        var totalLength = sourceStream.Length;
        long copied = 0;
        var remaining = totalLength;

        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(totalLength, bufferSize));
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            int bytesRead;
            while ((bytesRead = await sourceStream.ReadAsync(new Memory<byte>(buffer), token)) != 0)
            {
                await targetStream.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, bytesRead), token);

                copied += bytesRead;
                var percentage = (copied * 100.0) / totalLength;
                Console.WriteLine($"Copied ({percentage.Truncate(1)}%) {copied} of {totalLength} in {watch.Elapsed}");

                watch.Restart();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return copied;
    }

    public static UriBuilder Scrub(this Uri uri)
    {
        var scrubbedUri = new UriBuilder(uri)
        {
            Query = null
        };

        return scrubbedUri;
    }

    public static double Truncate(this double value, int digits)
    {
        return Math.Round(value, digits, MidpointRounding.ToZero);
    }

    public static IEnumerable<KeyValuePair<string, string?>> GetEnvironmentVariables()
    {
        return Environment.GetEnvironmentVariables().OfType<DictionaryEntry>().Select(e => KeyValuePair.Create((string)e.Key, (string?)e.Value));
    }

    public static string? GetEnvironmentVariable(string name)
    {
        string overrideEnvName = OverridePrefix + name;
        return Environment.GetEnvironmentVariable(overrideEnvName).AsNonEmptyOrOptional().Value
                ?? Environment.GetEnvironmentVariable(name);
    }

    public static string? AsNonEmptyOrNull(this string? s)
    {
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public static Optional<string> AsNonEmptyOrOptional(this string? s)
    {
        return string.IsNullOrEmpty(s) ? Optional.Default : s;
    }

    public static TValue? ValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> map, TKey key, TValue defaultValue = default!)
    {
        return map.TryGetValue(key, out var value) ? value : defaultValue;
    }

    public static TResult FluidSelect<T, TResult>(this T c, Func<T, TResult> selector)
    {
        return selector(c);
    }

    public static ValueTask<T> AsValueTask<T>(this Task<T> task) => new(task);

    public static async IAsyncEnumerable<T> AsAsyncEnumerable<T>(this Task<IReadOnlyList<T>> listTask)
    {
        var list = await listTask;
        foreach (var item in list)
        {
            yield return item;
        }
    }

    public static Timestamp GetLastWriteTimestamp(this ShareFileItem file)
    {
        return file.Properties.LastWrittenOn!.Value;
    }

    public static Timestamp GetLastWriteTimestamp(this ShareFileProperties file)
    {
        return file.SmbProperties.FileLastWrittenOn!.Value;
    }

    public static Timestamp ToTimeStamp(this DateTimeOffset time)
    {
        return time;
    }

    public static Timestamp ToTimeStamp(this DateTime time)
    {
        return time;
    }

    public static Optional<T> Or<T>(this Optional<T> first, Optional<T> second)
    {
        if (first.HasValue) return first;
        else return second;
    }

    public static Optional<T?> ToNullable<T>(this Optional<T> value)
        where T : struct
    {
        if (!value.HasValue) return Optional.Default;

        return value.Value;
    }

    public static T Result<T>(Func<T> getResult)
    {
        return getResult();
    }

    public static async ValueTask<TResult> ThenAsync<T, TResult>(this ValueTask<T> task, Func<T, TResult> select)
    {
        var input = await task;
        return select(input);
    }

    public static async ValueTask<TResult> ThenAsync<T, TResult>(this Task<T> task, Func<T, TResult> select)
    {
        var input = await task;
        return select(input);
    }

    public static bool IsNonEmpty([NotNullWhen(true)] this string? s)
    {
        return !string.IsNullOrEmpty(s);
    }

    public static void Add<K, V>(this IDictionary<K, V> map, IEnumerable<KeyValuePair<K, V>> entries)
    {
        foreach (var entry in entries)
        {
            map[entry.Key] = entry.Value;
        }
    }

    public static string AsEnvironmentVariableName(string variableName)
    {
        return VariableSeparatorPattern.Replace(variableName, m => "_").ToUpperInvariant();
    }

    public static void Add(this Command parent, IEnumerable<Command> commands)
    {
        foreach (var command in commands)
        {
            parent.Add(command);
        }
    }

    public static string GetSetPipelineVariableText(string name, string value, bool isSecret = false, bool isOutput = false, bool emit = false, bool log = false)
    {
        string additionalArgs = "";
        if (isSecret)
        {
            additionalArgs += "issecret=true;";
        }

        if (isOutput)
        {
            additionalArgs += "isOutput=true;";
        }

        if (log && emit)
        {
            string valueText = isSecret ? "***" : value;
            Console.WriteLine($"Setting '{name}' to '{valueText}'");
        }

        return PrintAndReturn($"##vso[task.setvariable variable={name};{additionalArgs}]{value}", emit);
    }

    public static string AddBuildTag(string tag, bool print = true)
    {
        return PrintAndReturn($"##vso[build.addbuildtag]{tag}", print: print);
    }

    public record struct Parallelism(ParallelOptions? Options)
    {
        public static implicit operator Parallelism(bool value)
        {
            return new(value ? new ParallelOptions() : null);
        }

        public static implicit operator Parallelism(int parallelism)
        {
            return new Parallelism(parallelism > 1 ? new ParallelOptions()
            {
                MaxDegreeOfParallelism = parallelism
            } : null);
        }
    }

    public static async Task ForEachAsync<TSource>(Parallelism parallel, IEnumerable<TSource> source, CancellationToken token, Func<TSource, CancellationToken, ValueTask> body)
    {
        if (parallel.Options is { } options)
        {
            options.CancellationToken = token;
            await Parallel.ForEachAsync(source, options, body);
        }
        else
        {
            foreach (var item in source)
            {
                await body(item, token);
            }
        }
    }

    public static async Task ForEachAsync<TSource>(Parallelism parallel, IAsyncEnumerable<TSource> source, CancellationToken token, Func<TSource, CancellationToken, ValueTask> body)
    {
        if (parallel.Options is { } options)
        {
            options.CancellationToken = token;
            await Parallel.ForEachAsync(source, options, body);
        }
        else
        {
            await foreach (var item in source)
            {
                await body(item, token);
            }
        }
    }

    private static string PrintAndReturn(string value, bool print)
    {
        if (print) Console.WriteLine(value);
        return value;
    }

    private static string? GetReplaceableTokenValue(this string arg) =>
        arg.Length > 1 && arg[0] == '@'
            ? arg.Substring(1)
            : null;

    internal static string[] TryReadResponseFile(string filePath)
    {
        return ExpandResponseFile(filePath).ToArray();

        static IEnumerable<string> ExpandResponseFile(string filePath)
        {
            var lines = File.ReadAllLines(filePath);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                foreach (var p in SplitLine(line))
                {
                    if (p.GetReplaceableTokenValue() is { } path)
                    {
                        foreach (var q in ExpandResponseFile(path))
                        {
                            yield return q;
                        }
                    }
                    else
                    {
                        yield return p;
                    }
                }
            }
        }

        static IEnumerable<string> SplitLine(string line)
        {
            var arg = line.Trim();

            if (arg.Length == 0 || arg[0] == '#')
            {
                yield break;
            }

            foreach (var word in CommandLineStringSplitter.Instance.Split(arg))
            {
                yield return word;
            }
        }
    }

    public static string? ExtractFilenameFromContentDispositionUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var result = extractCore(url);
        if (result?.StartsWith("http") == true)
        {
            result = extractCore(result);
        }

        return result;

        string? extractCore(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                return null;

            var query = HttpUtility.ParseQueryString(uri.Query);
            string? contentDisposition = query["response-content-disposition"];

            if (!string.IsNullOrEmpty(contentDisposition))
            {
                // Decode URL-encoded header value
                string decoded = Uri.UnescapeDataString(contentDisposition);

                const string marker = "filename*=UTF-8''";

                if (decoded.Contains(marker))
                {
                    int start = decoded.IndexOf(marker) + marker.Length;
                    string filename = decoded[start..].TrimEnd(';').Trim();
                    return filename;
                }

                // Fallback: Try to find simple "filename=" case (not RFC 5987)
                const string simpleMarker = "filename=";
                if (decoded.Contains(simpleMarker))
                {
                    int start = decoded.IndexOf(simpleMarker) + simpleMarker.Length;
                    string filename = decoded[start..].Trim('\"', ';', ' ');
                    return filename;
                }
            }
            else if (query["filename"] is { } fileName && !string.IsNullOrEmpty(fileName))
            {
                return fileName;
            }

            // Final fallback: Look for [[filename]]
            var match = Regex.Match(url, @"\[\[(.+)\]\]");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return null;
        }
    }

    public static (Guid Id, int Index) ExtractFileDescriptor(string name)
    {
        var match = Regex.Match(name, @"\[\[(?<id>\w+)-(?<index>\d+)\]\]");
        if (match.Success)
        {
            var id = Guid.Parse(match.Groups["id"].Value);
            var index = int.Parse(match.Groups["index"].Value);
            return (id, index);
        }

        throw new FormatException(name);
    }

    /// <summary>
    /// Creates a new <see cref="SemaphoreSlim"/> representing a mutex which can only be entered once.
    /// </summary>
    /// <returns>the semaphore</returns>
    public static SemaphoreSlim CreateMutex()
    {
        return new SemaphoreSlim(initialCount: 1, maxCount: 1);
    }

    /// <summary>
    /// Asynchronously acquire a semaphore
    /// </summary>
    /// <param name="semaphore">The semaphore to acquire</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A disposable which will release the semaphore when it is disposed.</returns>
    public static async ValueTask<SemaphoreReleaser> AcquireAsync(this SemaphoreSlim semaphore, CancellationToken cancellationToken = default(CancellationToken))
    {
        Contract.Requires(semaphore != null);
        await semaphore.WaitAsync(cancellationToken);
        return new SemaphoreReleaser(semaphore);
    }

    /// <summary>
    /// Allows an IDisposable-conforming release of an acquired semaphore
    /// </summary>
    [SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
    public struct SemaphoreReleaser : IDisposable
    {
        public bool IsAcquired => m_semaphore != null;

        private readonly SemaphoreSlim m_semaphore;

        /// <summary>
        /// Creates a new releaser.
        /// </summary>
        /// <param name="semaphore">The semaphore to release when Dispose is invoked.</param>
        /// <remarks>
        /// Assumes the semaphore is already acquired.
        /// </remarks>
        internal SemaphoreReleaser(SemaphoreSlim semaphore)
        {
            this.m_semaphore = semaphore;
        }

        /// <summary>
        /// IDispoaable.Dispose()
        /// </summary>
        public void Dispose()
        {
            m_semaphore?.Release();
        }

        /// <summary>
        /// Whether this semaphore releaser is valid (and not the default value)
        /// </summary>
        public bool IsValid
        {
            get { return m_semaphore != null; }
        }

        /// <summary>
        /// Gets the number of threads that will be allowed to enter the semaphore.
        /// </summary>
        public int CurrentCount
        {
            get { return m_semaphore.CurrentCount; }
        }
    }
}