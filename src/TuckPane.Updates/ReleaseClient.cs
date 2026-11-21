using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TuckPane.Updates;

public sealed record UpdateAsset(string Name, string Url, long Size, string? Digest);
public sealed record UpdateRelease(string Version, string Notes, UpdateAsset Package, UpdateAsset Checksums);

public static class ReleaseRules
{
    // This fork ships its own builds; in-app updates must never resolve to upstream releases.
    public const string Repository = "daha1216/TuckPane";
    public const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string LatestWebUrl = "https://github.com/" + Repository + "/releases/latest";
    public static Version StableVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$"))
            throw new InvalidDataException("Invalid stable version.");
        return Version.Parse(value.TrimStart('v'));
    }

    public static string ExpectedDownloadUrl(string tag, string fileName) =>
        $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{fileName}";

    public static string ExpectedRawNotesUrl(string tag) =>
        $"https://raw.githubusercontent.com/{Repository}/{Uri.EscapeDataString(tag)}/RELEASE_NOTES.md";

    public static bool TryExtractLatestTag(Uri? uri, out string tag)
    {
        tag = "";
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        string prefix = $"/{Repository}/releases/tag/";
        string path = uri.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string remainder = path[prefix.Length..].TrimEnd('/');
        if (remainder.Length == 0 || remainder.Contains('/')) return false;
        string candidate = Uri.UnescapeDataString(remainder);
        try
        {
            _ = StableVersion(candidate);
            tag = candidate;
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static bool AutoCheckDue(DateTimeOffset? lastAttempt, DateTimeOffset now) =>
        lastAttempt is null || now < lastAttempt || now - lastAttempt >= TimeSpan.FromHours(24);

    public static UpdateRelease? Parse(string json, string currentVersion, bool installed)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        Version latest = StableVersion(tag);
        if (latest <= StableVersion(currentVersion)) return null;
        string name = $"TuckPane-{latest}-win-x64-{(installed ? "setup.exe" : "portable.zip")}";
        UpdateAsset ReadAsset(string expected)
        {
            JsonElement[] matches = root.GetProperty("assets").EnumerateArray()
                .Where(a => a.GetProperty("name").GetString() == expected).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Required release asset is missing or duplicated.");
            JsonElement item = matches[0];
            string url = item.GetProperty("browser_download_url").GetString() ?? "";
            string expectedUrl = ExpectedDownloadUrl(tag, expected);
            if (!string.Equals(url, expectedUrl, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected release source.");
            long size = item.GetProperty("size").GetInt64();
            if (size <= 0 || size > 1_500_000_000) throw new InvalidDataException("Invalid asset size.");
            return new(expected, url, size, item.TryGetProperty("digest", out var digest) ? digest.GetString() : null);
        }
        return new(latest.ToString(), root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            ReadAsset(name), ReadAsset("SHA256SUMS.txt"));
    }

    public static string ExpectedHash(string checksums, string fileName)
    {
        MatchCollection matches = Regex.Matches(checksums, @"(?m)^([a-fA-F0-9]{64}) [ *]" + Regex.Escape(fileName) + @"\r?$");
        if (matches.Count != 1) throw new InvalidDataException("Missing or duplicate package checksum.");
        return matches[0].Groups[1].Value.ToLowerInvariant();
    }
}

public sealed class ReleaseClient(HttpClient http)
{
    private static readonly string UserAgentValue = BuildUserAgentValue();

    private static string BuildUserAgentValue()
    {
        foreach (Assembly? assembly in new[] { Assembly.GetEntryAssembly(), typeof(ReleaseClient).Assembly })
        {
            if (assembly is null) continue;
            string? name = assembly.GetName().Name;
            if (name is null || !name.StartsWith("TuckPane", StringComparison.Ordinal)) continue;
            string? info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                string core = info.Split('+', 2)[0].Trim();
                if (core.Length > 0 && core != "1.0.0") return "TuckPane-Updater/" + core;
            }
            if (assembly.GetName().Version is { } v && (v.Major != 1 || v.Minor != 0 || v.Build > 0))
                return $"TuckPane-Updater/{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
        return "TuckPane-Updater/4.3.3";
    }

    private static HttpRequestMessage Request(string url) =>
        Request(HttpMethod.Get, url, "application/vnd.github+json");

    private static HttpRequestMessage Request(HttpMethod method, string url, string? accept)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd(UserAgentValue);
        if (!string.IsNullOrWhiteSpace(accept))
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return request;
    }

    public async Task<string> GetLatestJsonAsync(CancellationToken token)
    {
        using var request = Request(ReleaseRules.LatestUrl);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return await GetLatestWebFallbackJsonAsync(token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1_000_000, token);
        return await response.Content.ReadAsStringAsync(token);
    }

    // Shared proxy/VPN exits frequently exhaust GitHub's 60 req/hr unauthenticated REST API quota (403/429).
    // Fall back to GitHub's rate-limit-free web release redirect, SHA256SUMS.txt, and asset size probes
    // while enforcing the exact same repository-origin, stable-SemVer, size, and SHA-256 contracts.
    private async Task<string> GetLatestWebFallbackJsonAsync(CancellationToken token)
    {
        Uri? resolvedUri;
        using (var webRequest = Request(HttpMethod.Get, ReleaseRules.LatestWebUrl, "text/html"))
        using (var webResponse = await http.SendAsync(webRequest, HttpCompletionOption.ResponseHeadersRead, token))
        {
            if ((int)webResponse.StatusCode is >= 300 and < 400 && webResponse.Headers.Location is { } location)
            {
                resolvedUri = location.IsAbsoluteUri
                    ? location
                    : new Uri(new Uri(ReleaseRules.LatestWebUrl), location);
            }
            else
            {
                webResponse.EnsureSuccessStatusCode();
                resolvedUri = webResponse.RequestMessage?.RequestUri;
            }
        }

        if (!ReleaseRules.TryExtractLatestTag(resolvedUri, out string tag))
            throw new InvalidDataException("Latest release redirect did not resolve to a valid stable tag.");

        Version version = ReleaseRules.StableVersion(tag);
        string sumsUrl = ReleaseRules.ExpectedDownloadUrl(tag, "SHA256SUMS.txt");
        byte[] sumsBytes;
        using (var sumsRequest = Request(HttpMethod.Get, sumsUrl, null))
        using (var sumsResponse = await http.SendAsync(sumsRequest, HttpCompletionOption.ResponseHeadersRead, token))
        {
            sumsResponse.EnsureSuccessStatusCode();
            await sumsResponse.Content.LoadIntoBufferAsync(65_536, token);
            sumsBytes = await sumsResponse.Content.ReadAsByteArrayAsync(token);
        }
        if (sumsBytes.Length == 0)
            throw new InvalidDataException("Release checksums file is empty.");
        string checksums = Encoding.UTF8.GetString(sumsBytes);

        string setupName = $"TuckPane-{version}-win-x64-setup.exe";
        string portableName = $"TuckPane-{version}-win-x64-portable.zip";
        string setupHash = ReleaseRules.ExpectedHash(checksums, setupName);
        string portableHash = ReleaseRules.ExpectedHash(checksums, portableName);
        string setupUrl = ReleaseRules.ExpectedDownloadUrl(tag, setupName);
        string portableUrl = ReleaseRules.ExpectedDownloadUrl(tag, portableName);

        Task<long> setupSizeTask = ProbeAssetSizeAsync(setupUrl, token);
        Task<long> portableSizeTask = ProbeAssetSizeAsync(portableUrl, token);
        Task<string> notesTask = TryFetchRawReleaseNotesAsync(tag, token);
        await Task.WhenAll(setupSizeTask, portableSizeTask, notesTask);

        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            prerelease = false,
            body = await notesTask,
            assets = new object[]
            {
                new { name = setupName, browser_download_url = setupUrl, size = await setupSizeTask, digest = "sha256:" + setupHash },
                new { name = portableName, browser_download_url = portableUrl, size = await portableSizeTask, digest = "sha256:" + portableHash },
                new { name = "SHA256SUMS.txt", browser_download_url = sumsUrl, size = (long)sumsBytes.Length }
            }
        });
    }

    private async Task<long> ProbeAssetSizeAsync(string url, CancellationToken token)
    {
        using (var headRequest = Request(HttpMethod.Head, url, null))
        using (var headResponse = await http.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, token))
        {
            if (headResponse.IsSuccessStatusCode &&
                headResponse.Content.Headers.ContentLength is long headSize and > 0 and <= 1_500_000_000)
            {
                return headSize;
            }
        }

        using var rangeRequest = Request(HttpMethod.Get, url, null);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);
        using var rangeResponse = await http.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, token);
        rangeResponse.EnsureSuccessStatusCode();
        long? totalLength = rangeResponse.Content.Headers.ContentRange?.Length
            ?? rangeResponse.Content.Headers.ContentLength;
        if (totalLength is long validSize and > 0 and <= 1_500_000_000)
            return validSize;
        throw new InvalidDataException("Invalid asset size.");
    }

    private async Task<string> TryFetchRawReleaseNotesAsync(string tag, CancellationToken token)
    {
        try
        {
            using var notesRequest = Request(HttpMethod.Get, ReleaseRules.ExpectedRawNotesUrl(tag), "text/plain");
            using var notesResponse = await http.SendAsync(notesRequest, HttpCompletionOption.ResponseHeadersRead, token);
            if (!notesResponse.IsSuccessStatusCode) return "";
            await notesResponse.Content.LoadIntoBufferAsync(262_144, token);
            return await notesResponse.Content.ReadAsStringAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return "";
        }
    }

    public async Task<string> DownloadAsync(UpdateRelease release, string directory, IProgress<double>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        using var checksumRequest = Request(release.Checksums.Url);
        using var checksumResponse = await http.SendAsync(checksumRequest, HttpCompletionOption.ResponseHeadersRead, token);
        checksumResponse.EnsureSuccessStatusCode();
        await checksumResponse.Content.LoadIntoBufferAsync(65_536, token);
        string hash = ReleaseRules.ExpectedHash(await checksumResponse.Content.ReadAsStringAsync(token), release.Package.Name);
        if (release.Package.Digest is { Length: > 0 } digest && !string.Equals(digest, "sha256:" + hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release digest and checksums disagree.");
        string destination = Path.Combine(directory, release.Package.Name);
        string partial = destination + ".download";
        try
        {
            using var request = Request(release.Package.Url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (Stream source = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[81920];
                long length = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token)) != 0)
                {
                    length += count;
                    if (length > release.Package.Size) throw new InvalidDataException("Download exceeds asset size.");
                    checksum.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    progress?.Report(length * 100d / release.Package.Size);
                }
                if (length != release.Package.Size || !Convert.ToHexString(checksum.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Package checksum or length mismatch.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, destination);
            return destination;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
