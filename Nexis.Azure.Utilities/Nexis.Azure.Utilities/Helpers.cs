using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Net.Http.Json;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Web;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.Shares.Models;
using CliWrap;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using YamlDotNet.Core.Tokens;
using Command = System.CommandLine.Command;

namespace Nexis.Azure.Utilities;

public static class Helpers
{
    private static readonly YamlDotNet.Serialization.Serializer YamlSerializer = new();
    private static readonly YamlDotNet.Serialization.Deserializer YamlDeserializer = new();

    public static async IAsyncEnumerable<T[]> ChunkAsync<T>(this IAsyncEnumerable<T> items, int chunkSize)
    {
        var list = new List<T>();

        await foreach (var item in items)
        {
            list.Add(item);
            if (list.Count >= chunkSize)
            {
                yield return list.ToArray();
                list.Clear();
            }
        }

        if (list.Count != 0)
        {
            yield return list.ToArray();
        }
    }

    public static async Task<string> PostTextAsync(this HttpClient client, string uri, string text)
    {
        var response = await client.PostAsync(uri, new StringContent(text));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public static async IAsyncEnumerable<T> SplitMergeAsync<T>(this IAsyncEnumerable<T> items,
        Func<T, bool> predicate,
        Func<IAsyncEnumerable<T>, IAsyncEnumerable<T>>? handleTrueItems = null,
        Func<IAsyncEnumerable<T>, IAsyncEnumerable<T>>? handleFalseItems = null,
        ChannelBounds bounds = default)
    {
        Task handleAsync(Func<IAsyncEnumerable<T>, IAsyncEnumerable<T>>? handler, out Channel<T> inChannel, out Channel<T> outChannel)
        {
            inChannel = bounds.CreateChannel<T>();
            outChannel = bounds.CreateChannel<T>();

            handler ??= i => i;

            async Task runAsync(Channel<T> inChannel, Channel<T> outChannel)
            {
                await foreach (var item in handler(inChannel.Reader.ReadAllAsync()))
                {
                    await outChannel.Writer.WriteAsync(item);
                }

                outChannel.Writer.Complete();
            }

            return runAsync(inChannel, outChannel);
        }

        var handleTrueTask = handleAsync(handleTrueItems, out var trueInChannel, out var trueOutChannel);
        var handleFalseTask = handleAsync(handleFalseItems, out var falseInChannel, out var falseOutChannel);

        async IAsyncEnumerable<bool> enumerateResults()
        {
            await foreach (var item in items)
            {
                var result = predicate(item);
                if (result)
                {
                    await trueInChannel.Writer.WriteAsync(item);
                }
                else
                {
                    await falseInChannel.Writer.WriteAsync(item);
                }

                yield return result;
            }

            trueInChannel.Writer.Complete();
            falseInChannel.Writer.Complete();
        }

        var results = enumerateResults();
        Queue<bool> queuedResults = new();

        await foreach (var result in results)
        {
            queuedResults.Enqueue(result);

            while (queuedResults.TryPeek(out var queuedResult))
            {
                var channel = queuedResult ? trueOutChannel : falseOutChannel;
                if (channel.Reader.TryRead(out var item))
                {
                    queuedResults.Dequeue();
                    yield return item;
                }
                else
                {
                    break;
                }
            }
        }

        while (queuedResults.TryDequeue(out var queuedResult))
        {
            var channel = queuedResult ? trueOutChannel : falseOutChannel;
            var item = await channel.Reader.ReadAsync();
            yield return item;
        }
    }

    public static ConcurrentDictionary<TKey, TValue> ToConcurrent<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> entries, IEqualityComparer<TKey>? comparer = null)
    {
        return new ConcurrentDictionary<TKey, TValue>(entries, comparer);
    }

    public static string YamlSerialize<T>(T value)
    {
        return YamlSerializer.Serialize(value);
    }

    public static T YamlDeserialize<T>(string  yaml)
    {
        return YamlDeserializer.Deserialize<T>(yaml);
    }

    public static Dictionary<int, T> ToIndexMap<T>(this IEnumerable<T> items)
    {
        return items.Select((item, index) => (item, index)).ToDictionary(t => t.index, t => t.item);
    }

    public static ImmutableDictionary<string, string> EmptyStringMap = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    public static IDictionary<string, string> Tags(this BlobItem b) => b.Tags ?? EmptyStringMap;
    public static IDictionary<string, string> Metadata(this BlobItem b) => b.Metadata ?? EmptyStringMap;

    public static readonly Regex VariableSeparatorPattern = new Regex(@"[\._\-]");
    public static readonly Regex VariablePattern = new Regex(@"\$\(([\w\._\-]+)\)");

    public static bool IsAdoBuild = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BUILD_BUILDID"));

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

    public class PipeTargetValue(PipeTarget? pipeTarget = null, TextWriter? textWriter = null, FileInfo? fileInfo = null, StringBuilder? sb = null)
    {
        public PipeTarget? Target { get; } = pipeTarget
            ?? textWriter?.FluidSelect(t => PipeTarget.ToDelegate(line => t.WriteLine(line ?? string.Empty)))
            ?? sb?.FluidSelect(t => PipeTarget.ToStringBuilder(t))
            ?? fileInfo?.FluidSelect(t => PipeTarget.ToFile(fileInfo.FullName));

        public static PipeTargetValue operator &(PipeTargetValue left, PipeTargetValue right)
        {
            if (left.Target == null)
            {
                return right;
            }
            else if (right.Target == null)
            {
                return left;
            }
            else
            {
                return PipeTarget.Merge(left!, right!);
            }
        }

        public static PipeTargetValue operator |(PipeTargetValue left, PipeTargetValue right)
        {
            return left & right;
        }

        public static implicit operator PipeTargetValue(PipeTarget pipeTarget) => new(pipeTarget: pipeTarget);
        public static implicit operator PipeTargetValue(TextWriter textWriter) => new(textWriter: textWriter);
        public static implicit operator PipeTargetValue(FileInfo fileInfo) => new(fileInfo: fileInfo);
        public static implicit operator PipeTargetValue(StringBuilder sb) => new(sb: sb);

        public static implicit operator PipeTarget?(PipeTargetValue value) => value.Target;

    }

    public static async IAsyncEnumerable<T> DistinctBy<T, TKey>(this IAsyncEnumerable<T> items, Func<T, TKey> getKey, IEqualityComparer<TKey> comparer = null!)
    {
        comparer ??= EqualityComparer<TKey>.Default;

        HashSet<TKey> set = new(comparer);

        await foreach (var item in items)
        {
            var key = getKey(item);
            if (!set.Add(key)) continue;
            yield return item;
        }
    }

    public static IEnumerable<string> SplitLines(this string s)
    {
        var r = new StringReader(s);
        while (Out.Var(out var line, r.ReadLine()) != null)
        {
            yield return line!;
        }
    }

    public static async Task<int> RunProcessAsync(string processName, string[] args, PipeTargetValue? target = null)
    {
        var psi = new ProcessStartInfo(processName, args);
        var process = Process.Start(psi);
        int exitCode = -1;
        if (process != null)
        {
            await process.WaitForExitAsync();

            exitCode = process.ExitCode;
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Non-zero exit code {exitCode}");
        }
        return exitCode;

    }

    public static async Task<int> ExecAsync(string processName, string[] args, PipeTargetValue? target = null, PipeTargetValue? error = null, bool isCliWrap = false, bool useCmd = false)
    {
        if (useCmd)
        {
            args = ["/C", processName, ..args];
            processName = "cmd";
        }

        isCliWrap |= processName == "ffmpeg";
        //error ??= target;
        var cmd = Cli.Wrap(processName)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.ZeroExitCode);

        if (target?.Target is { } outTarget)
        {
            cmd = cmd.WithStandardOutputPipe(outTarget);
        }

        if (error?.Target is { } errorTarget)
        {
            cmd = cmd.WithStandardErrorPipe(errorTarget);
        }

        Console.WriteLine(cmd);

        if (!isCliWrap)
        {
            var psi = new ProcessStartInfo(processName, args);

            if (cmd.StandardOutputPipe != null)
            {
                psi.RedirectStandardOutput = true;
            }

            if (cmd.StandardErrorPipe != null)
            {
                psi.RedirectStandardError = true;
            }

            var process = Process.Start(psi);

            int exitCode = -1;
            List<Task> t = new();
            if (process != null)
            {

                if (cmd.StandardOutputPipe != null)
                {
                    //process.BeginOutputReadLine();
                    t.Add(cmd.StandardOutputPipe.CopyFromAsync(process.StandardOutput.BaseStream));
                }

                if (cmd.StandardErrorPipe != null)
                {
                    //process.BeginErrorReadLine();
                    t.Add(cmd.StandardErrorPipe.CopyFromAsync(process.StandardError.BaseStream));
                }

                await process.WaitForExitAsync();
                await Task.WhenAll(t);

                exitCode = process.ExitCode;
            }

            if (exitCode != 0)
            {
                throw new CliWrap.Exceptions.CommandExecutionException(cmd, exitCode, "Non-zero exit code");
            }
            return exitCode;
        }
        else
        {

            var result = await cmd.ExecuteAsync();

            return result.ExitCode;
        }
    }

    private static int _lastProgress = -1;

    public static void LogPipelineProgress(int progress, string message = "")
    {
        if (IsAdoBuild)
        {
            int lastProgress = _lastProgress;
            if (Interlocked.CompareExchange(ref _lastProgress, progress, lastProgress) == lastProgress)
            {
                Console.WriteLine($"##vso[task.setprogress value={progress};]{message}");
                Console.WriteLine($"Progress: {progress}% ({message})");
            }
        }
    }

    public static int GetPercentage(long numerator, long denominator)
    {
        var result = (numerator * 100) / denominator;
        return (int)Math.Max(0, Math.Min(result, 100));
    }

    public static IEnumerable<BlobItem> FilterDirectories(IEnumerable<BlobItem> blobs)
    {
        foreach (var blob in blobs)
        {
            if (!blob.Metadata().ContainsKey(Strings.dir_metadata))
            {
                yield return blob;
            }
        }
    }

    public static IAsyncEnumerable<T> AsyncEnum<T>(Func<IAsyncEnumerable<T>> enumerate)
    {
        return enumerate();
    }

    public static IComparer<string> VideoFileNameComparer { get; } = Comparer<string>.Create((string a, string b) =>
    {
        var aparts = SplitVideoFileNameParts(a).GetEnumerator();
        var bparts = SplitVideoFileNameParts(b).GetEnumerator();

        bool aMove = true;
        bool bMove = true;

        while (Out.Var(out aMove, aparts.MoveNext()) && Out.Var(out bMove, bparts.MoveNext()))
        {
            int result = 0;
            if (int.TryParse(aparts.Current.Span, out var aNum) && int.TryParse(bparts.Current.Span, out var bNum))
            {
                result = aNum.CompareTo(bNum);
            }
            else
            {
                result = aparts.Current.Span.CompareTo(bparts.Current.Span, StringComparison.OrdinalIgnoreCase);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return aMove.CompareTo(bMove);
    });

    public static IEnumerable<ReadOnlyMemory<char>> SplitVideoFileNameParts(string videoFileName)
    {
        int start = 0;
        bool? lastIsDigit = null;
        for (int i = 0; i < videoFileName.Length; i++)
        {
            var ch = videoFileName[i];
            bool isDigit = char.IsDigit(ch);
            lastIsDigit ??= isDigit;
            if (isDigit != lastIsDigit)
            {
                yield return videoFileName.AsMemory(start, i - start);
                start = i;
            }

            lastIsDigit = isDigit;
        }

        yield return videoFileName.AsMemory(start, videoFileName.Length - start);
    }

    public static async Task<HttpRequestMessage> AsHttpRequestAsync(this IRequest request, string? url = null, HttpMethod? method =  null)
    {
        var result = new HttpRequestMessage(method ?? HttpMethod.Parse(request.Method), url ?? request.Url);

        var headers = await request.AllHeadersAsync();
        foreach (var header in headers)
        {
            if (header.Key.StartsWith(":")) continue;
            result.Headers.Add(header.Key, header.Value);            
        }

        return result;
    }

    public static async Task<HttpClient> AsHttpClientAsync(this IRequest request)
    {
        var client = new HttpClient(new HttpClientHandler()
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });

        var headers = await request.AllHeadersAsync();
        foreach (var header in headers)
        {
            if (header.Key.StartsWith(":")) continue;
            try
            {
                client.DefaultRequestHeaders.Add(header.Key, header.Value);
            }
            catch
            {
            }
        }

        return client;
    }

    public static Task<IRequest> GotoAndGetUrlRequest(this IPage page, string url, string requestUrl)
    {
        return page.RunAndWaitForRequestFinishedAsync(() => page.GotoAsync(url),
        new()
        {
            Predicate = (IRequest r) =>
            {
                var url = r.Url;
                if (url == requestUrl)
                {
                    return true;
                }

                return false;
            }
        });
    }

    public static Task PostDataFromBrowserContextAsync<T>(this IPage page, string url, T data)
    {
        return PostJsonFromBrowserContextAsync(page, url, JsonSerializer.Serialize<T>(data));
    }

    public static async Task PostJsonFromBrowserContextAsync(this IPage page, string url, string jsonPayload)
    {
        // Evaluate JavaScript in browser to send POST with fetch
        var responseText = await page.EvaluateAsync<string>(
            @"async (url, jsonBody) => {
            const res = await fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: jsonBody
            });
            return await res.text(); // or res.json() if expecting JSON
        }",
            new[] { url, jsonPayload }
        );

        Console.WriteLine("Response:");
        Console.WriteLine(responseText);
    }


    public static string CreateDirectoryForFile(string root, string relative = "")
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

    public static string UriCombine(this string? baseUri, string relativeUri)
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

    public static ValueTaskAwaiter<T> GetAwaiter<T>(this ValueTask<T>? task)
    {
        return task?.GetAwaiter() ?? ValueTask.FromResult(default(T)).GetAwaiter()!;
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

    public record struct ChannelBounds(int? Capacity)
    {
        public static implicit operator ChannelBounds(int? capacity)
        {
            return new(capacity);
        }

        public static implicit operator ChannelBounds(Parallelism parallelism)
        {
            var maxParallel = parallelism.Options?.MaxDegreeOfParallelism ?? 1;
            if (maxParallel == -1) maxParallel = Environment.ProcessorCount;
            return maxParallel * 2;
        }

        public Channel<T> CreateChannel<T>()
        {
            return Capacity == null ? Channel.CreateUnbounded<T>() : Channel.CreateBounded<T>(new BoundedChannelOptions(Capacity.Value));
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

    public static ValueTaskAwaiter GetAwaiter(this ValueTask? task)
    {
        return (task ?? ValueTask.CompletedTask).GetAwaiter();
    }

    public static async IAsyncEnumerable<T> WrapAsync<T>(this IAsyncEnumerable<T> source, Func<ValueTask>? beforeAsync = null, Func<ValueTask>? afterAsync = null)
    {
        await beforeAsync?.Invoke();

        await foreach (var item in source)
        {
            yield return item;
        }

        await afterAsync?.Invoke();
    }

    public static IAsyncEnumerable<T> CreateSecondaryEnumerable<T>(this IAsyncEnumerable<T> items, out IAsyncEnumerable<T> secondary, ChannelBounds bounds = default)
    {
        var channel = bounds.CreateChannel<T>();

        secondary = channel.Reader.ReadAllAsync();
        return items.WrapAsync(afterAsync: async () =>
        {
            channel.Writer.Complete();
        }).SelectAwait(async item =>
        {
            await channel.Writer.WriteAsync(item);
            return item;
        });
    }

    public static async IAsyncEnumerable<TResult> ParallelSelectAsync<T, TResult>(this IAsyncEnumerable<T> source, Parallelism parallel, CancellationToken token, Func<T, CancellationToken, Task<TResult>> body)
    {
        if (parallel.Options is { } options)
        {
            options.CancellationToken = token;
            var tasks = source.Select(i => body(i, token)).CreateSecondaryEnumerable(out var resultTasks, parallel);
            await Parallel.ForEachAsync(tasks, options, async (resultTask, token) => await resultTask);

            await foreach (var resultTask in resultTasks)
            {
                var result = await resultTask;
                yield return result;
            }
        }
        else
        {
            await foreach (var item in source)
            {
                var result = await body(item, token);
                yield return result;
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

    public static string GetOperationId(string path, bool includeGuid = true)
    {
        var guid = Guid.NewGuid().ToString().Substring(0, 8);
        var fileName = Path.GetFileNameWithoutExtension(path);

        var name = fileName;

        if (fileName.Contains(" - "))
        {
            var parts = fileName.Split(" - ");
            if (parts.Length >= 3)
            {
                name = $"{parts[0].Trim().Truncate(20)}_{parts[1].Truncate(8)}";
            }
        }

        StringBuilder sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
            }
            else if (c == '.' || c == '-')
            {
                sb.Append('_');
            }
        }

        if (includeGuid)
        {
            sb.Append("_");
            sb.Append(guid);
        }
        return sb.ToString();
    }

    public static long RoundUpToMultiple(this long value, long multiple)
    {
        if (multiple == 0) return value; // or throw, depending on semantics
        return ((value + multiple - 1) / multiple) * multiple;
    }

    public static string Truncate(this string s, int length) => s.Length <= length ? s : s.Substring(0, length);

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

    public static Task<HttpResponseMessage> PostApiRequestAsync<TRequest>(this HttpClient client, TRequest request, CancellationToken token)
        where TRequest : IApiRequest<TRequest>
    {
        return client.PostAsJsonAsync(request.GetApiUrl(), request, token);
    }

    public static bool EqualsIgnoreCase(this string s, string? other)
    {
        return string.Equals(s, other, StringComparison.OrdinalIgnoreCase);
    }

    public static (Vuid Id, int Index) ExtractFileDescriptor(string name)
    {
        name = Uri.UnescapeDataString(name);
        var match = Regex.Match(name, @"\[\[(?<id>\w+)-(?<index>\d+)\]\]");
        if (match.Success)
        {
            var id = new Vuid(match.Groups["id"].Value);
            var index = int.Parse(match.Groups["index"].Value);
            return (id, index);
        }

        throw new FormatException(name);
    }

    public static IDisposable ReportProgressAsync(TimeSpan period, Action action)
    {
        CancellationTokenSource cts = new CancellationTokenSource();

        async void runAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(period, cts.Token);
                }
                catch
                {
                    break;
                }
                finally
                {
                    action();
                }
            }
        }

        runAsync();

        return new DisposeAction(() =>
        {
            cts.Cancel();
            cts.Dispose();
        });
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