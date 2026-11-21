using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace TuckPane.Services;

public sealed class IconCacheService
{
    private const int JumboSize = 256;
    private const int ExtraLargeSize = 48;
    private const int FallbackSize = 32;
    private const int ShIlExtraLarge = 2;
    private const int MaximumShortcutIconDepth = 4;
    private const string CacheVersion = "v9-premul-utf8-url-retina";

    internal const int MaximumMemoryCachedIcons = 2048;
    internal const int MaximumCachedIcons = 4096;
    internal const long MaximumCacheBytes = 64L * 1024 * 1024;

    private static readonly object InflightSync = new();
    private static readonly Dictionary<string, Task> InflightRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private static volatile bool _pathsEnsured;

    private readonly record struct CacheEntry(string MemoryKey, string Identity, BitmapImage Image);
    private readonly object _cacheSync = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _lru = new();

    internal static IntPtr CreateDragBitmap(string path, int size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        IntPtr icon = GetPreferredIcon(path, size, out _);

        try
        {
            byte[] pixels = DrawIconPixels(icon, size);
            UnpremultiplyAlpha(pixels);
            return CreateBitmap(pixels, size);
        }
        finally
        {
            _ = NativeMethods.DestroyIcon(icon);
        }
    }

    // Cache files have no expiry of their own, so leftovers are pruned once per startup:
    // interrupted writes leave .tmp files behind and the cache grows without bound.
    internal static Task SweepAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        try
        {
            string root = AppPaths.IconCacheRoot;
            if (!Directory.Exists(root)) return;
            var icons = new List<FileInfo>();
            long totalBytes = 0;
            int deleted = 0;
            DateTime staleBefore = DateTime.UtcNow.AddHours(-1);
            foreach (string entry in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (cancellationToken.IsCancellationRequested) return;
                var file = new FileInfo(entry);
                if ((file.Attributes & System.IO.FileAttributes.ReparsePoint) != 0) continue;
                if (file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    if (file.LastWriteTimeUtc < staleBefore && TryDelete(file)) deleted++;
                    continue;
                }
                if (!file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) continue;
                icons.Add(file);
                totalBytes += file.Length;
            }
            if (icons.Count > MaximumCachedIcons || totalBytes > MaximumCacheBytes)
            {
                icons.Sort((left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc));
                int remaining = icons.Count;
                foreach (FileInfo file in icons)
                {
                    if (remaining <= MaximumCachedIcons && totalBytes <= MaximumCacheBytes) break;
                    // Length must be read before the delete: FileInfo refreshes its state on access.
                    long length = file.Length;
                    if (!TryDelete(file)) continue;
                    remaining--;
                    totalBytes -= length;
                    deleted++;
                }
            }
            AppLogger.Performance($"icon-cache sweep files={icons.Count} deleted={deleted} bytes={totalBytes}");
        }
        catch { /* Cache maintenance must never change application behavior. */ }
    });

    private static bool TryDelete(FileInfo file)
    {
        try { file.Delete(); return !file.Exists; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    public Task<BitmapImage?> GetIconAsync(string path, bool refresh = false) =>
        GetIconCoreAsync(path, refresh, dockPresentation: false);

    internal Task<BitmapImage?> GetDockIconAsync(string path, bool refresh = false) =>
        GetIconCoreAsync(path, refresh, dockPresentation: true);

    internal bool TryGetCachedCompactPreviewIcon(string path, int physicalPixelSize, out BitmapImage? image)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            image = null;
            return false;
        }
        int targetPx = NormalizeCompactPreviewPixelSize(physicalPixelSize);
        string key = Path.GetFullPath(path);
        string memoryKey = $"{key}|compact-px={targetPx}";
        return TryGetMemoryCached(memoryKey, requiredIdentity: null, out image);
    }

    internal async Task<BitmapImage?> GetCompactPreviewIconAsync(string path, int physicalPixelSize, bool refresh = false)
    {
        int targetPx = NormalizeCompactPreviewPixelSize(physicalPixelSize);
        string key = Path.GetFullPath(path);
        string memoryKey = $"{key}|compact-px={targetPx}";
        if (!refresh && TryGetMemoryCached(memoryKey, requiredIdentity: null, out BitmapImage? fastHit))
        {
            return fastHit;
        }

        string identity = await Task.Run(() =>
        {
            EnsurePathsCreated();
            return BuildCacheIdentity(key);
        });
        string cacheIdentity = $"{identity}|compact-px={targetPx}";
        if (TryGetMemoryCached(memoryKey, cacheIdentity, out BitmapImage? verifiedHit))
        {
            return verifiedHit;
        }

        string cachePath = Path.Combine(AppPaths.IconCacheRoot, $"{Hash(identity)}.png");
        byte[]? pngBytes = await ReadOrCreateCacheBytesAsync(key, cachePath, dockPresentation: false);
        if (pngBytes is not { Length: > 0 }) return null;

        SupersampledPng? supersampled = await Task.Run(() => TrySupersampleCompactPngAsync(pngBytes, targetPx));

        try
        {
            byte[] decodeBytes = supersampled is { Bytes.Length: > 0 } ? supersampled.Value.Bytes : pngBytes;
            using var memoryStream = new MemoryStream(decodeBytes, writable: false);
            using IRandomAccessStream randomAccess = memoryStream.AsRandomAccessStream();
            var image = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Physical
            };
            if (supersampled is { Width: > 0, Height: > 0 } sized)
            {
                image.DecodePixelWidth = sized.Width;
                image.DecodePixelHeight = sized.Height;
            }
            else
            {
                image.DecodePixelWidth = targetPx;
            }
            await image.SetSourceAsync(randomAccess);
            StoreMemoryCached(memoryKey, cacheIdentity, image);
            return image;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"紧凑预览图标缓存读取失败：{cachePath}", ex);
            return null;
        }
    }

    internal static int NormalizeCompactPreviewPixelSize(int physicalPixelSize) =>
        Math.Clamp(physicalPixelSize, 12, 128);

    private readonly record struct SupersampledPng(byte[] Bytes, int Width, int Height);

    private static async Task<SupersampledPng?> TrySupersampleCompactPngAsync(byte[] sourcePngBytes, int targetPx)
    {
        try
        {
            using var inputStream = new InMemoryRandomAccessStream();
            await inputStream.WriteAsync(sourcePngBytes.AsBuffer()).AsTask().ConfigureAwait(false);
            inputStream.Seek(0);

            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(inputStream).AsTask().ConfigureAwait(false);
            uint srcWidth = decoder.PixelWidth;
            uint srcHeight = decoder.PixelHeight;
            if (srcWidth == 0 || srcHeight == 0) return null;

            uint dstWidth;
            uint dstHeight;
            if (srcWidth >= srcHeight)
            {
                dstWidth = (uint)targetPx;
                dstHeight = (uint)Math.Max(1, Math.Round((double)srcHeight * targetPx / srcWidth));
            }
            else
            {
                dstHeight = (uint)targetPx;
                dstWidth = (uint)Math.Max(1, Math.Round((double)srcWidth * targetPx / srcHeight));
            }

            if (srcWidth <= dstWidth && srcHeight <= dstHeight)
            {
                return new SupersampledPng(sourcePngBytes, (int)srcWidth, (int)srcHeight);
            }

            var transform = new BitmapTransform
            {
                ScaledWidth = dstWidth,
                ScaledHeight = dstHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            using SoftwareBitmap downsampled = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask().ConfigureAwait(false);

            int width = downsampled.PixelWidth;
            int height = downsampled.PixelHeight;
            uint byteCount = checked((uint)(width * height * 4));
            var pixelBuffer = new Windows.Storage.Streams.Buffer(byteCount);
            downsampled.CopyToBuffer(pixelBuffer);
            byte[] pixels = pixelBuffer.ToArray();

            ApplyAlphaSafeMicroUnsharpMask(pixels, width, height, strength: 0.20f);

            using var outputStream = new InMemoryRandomAccessStream();
            using (SoftwareBitmap sharpened = SoftwareBitmap.CreateCopyFromBuffer(
                pixels.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                width,
                height,
                BitmapAlphaMode.Premultiplied))
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream).AsTask().ConfigureAwait(false);
                encoder.SetSoftwareBitmap(sharpened);
                await encoder.FlushAsync().AsTask().ConfigureAwait(false);
            }

            outputStream.Seek(0);
            int encodedLength = checked((int)outputStream.Size);
            if (encodedLength <= 0) return null;
            using Stream readStream = outputStream.AsStreamForRead();
            byte[] encoded = new byte[encodedLength];
            await readStream.ReadExactlyAsync(encoded).ConfigureAwait(false);
            return new SupersampledPng(encoded, width, height);
        }
        catch
        {
            return null;
        }
    }

    internal static void ApplyAlphaSafeMicroUnsharpMask(byte[] pixels, int width, int height, float strength = 0.20f)
    {
        if (width < 3 || height < 3 || strength <= 0f || pixels.Length < width * height * 4) return;
        byte[] source = (byte[])pixels.Clone();
        int stride = width * 4;

        for (int y = 0; y < height; y++)
        {
            int yPrev = y > 0 ? y - 1 : 0;
            int yNext = y + 1 < height ? y + 1 : height - 1;
            int row = y * stride;
            int rowPrev = yPrev * stride;
            int rowNext = yNext * stride;

            for (int x = 0; x < width; x++)
            {
                int idx = row + x * 4;
                int a = source[idx + 3];
                if (a < 16) continue;

                int xPrev = x > 0 ? x - 1 : 0;
                int xNext = x + 1 < width ? x + 1 : width - 1;

                int i00 = rowPrev + xPrev * 4;
                int i01 = rowPrev + x * 4;
                int i02 = rowPrev + xNext * 4;
                int i10 = row + xPrev * 4;
                int i12 = row + xNext * 4;
                int i20 = rowNext + xPrev * 4;
                int i21 = rowNext + x * 4;
                int i22 = rowNext + xNext * 4;

                int sumA =
                    source[i00 + 3] + (source[i01 + 3] << 1) + source[i02 + 3] +
                    (source[i10 + 3] << 1) + (a << 2) + (source[i12 + 3] << 1) +
                    source[i20 + 3] + (source[i21 + 3] << 1) + source[i22 + 3];
                if (sumA <= 0) continue;

                // Alpha-weighted straight-color neighborhood prevents brightening/darkening halos on transparent silhouettes.
                float localStrength = strength * (a / 255f);
                float invAlpha = 255f / a;
                float invSumA = 255f / sumA;
                float alphaScale = a / 255f;

                for (int c = 0; c < 3; c++)
                {
                    int sumC =
                        source[i00 + c] + (source[i01 + c] << 1) + source[i02 + c] +
                        (source[i10 + c] << 1) + (source[idx + c] << 2) + (source[i12 + c] << 1) +
                        source[i20 + c] + (source[i21 + c] << 1) + source[i22 + c];

                    float straightCenter = source[idx + c] * invAlpha;
                    float straightBlur = sumC * invSumA;
                    float delta = straightCenter - straightBlur;
                    if (MathF.Abs(delta) <= 2.0f) continue;

                    float sharpenedStraight = Math.Clamp(straightCenter + delta * localStrength, 0f, 255f);
                    int premul = (int)MathF.Round(sharpenedStraight * alphaScale);
                    pixels[idx + c] = (byte)Math.Clamp(premul, 0, a);
                }
            }
        }
    }

    private async Task<BitmapImage?> GetIconCoreAsync(string path, bool refresh, bool dockPresentation)
    {
        string key = Path.GetFullPath(path);
        string memoryKey = dockPresentation ? $"dock-visible-v1|{key}" : key;
        if (!refresh && TryGetMemoryCached(memoryKey, requiredIdentity: null, out BitmapImage? fastHit))
        {
            return fastHit;
        }

        string identity = await Task.Run(() =>
        {
            EnsurePathsCreated();
            string built = BuildCacheIdentity(key);
            return dockPresentation ? $"dock-visible-v1|{built}" : built;
        });
        if (TryGetMemoryCached(memoryKey, identity, out BitmapImage? verifiedHit))
        {
            return verifiedHit;
        }

        string cachePath = Path.Combine(AppPaths.IconCacheRoot, $"{Hash(identity)}.png");
        byte[]? pngBytes = await ReadOrCreateCacheBytesAsync(key, cachePath, dockPresentation);
        if (pngBytes is not { Length: > 0 }) return null;

        try
        {
            using var memoryStream = new MemoryStream(pngBytes, writable: false);
            using IRandomAccessStream randomAccess = memoryStream.AsRandomAccessStream();
            var image = new BitmapImage();
            await image.SetSourceAsync(randomAccess);
            StoreMemoryCached(memoryKey, identity, image);
            return image;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"图标缓存读取失败：{cachePath}", ex);
            return null;
        }
    }

    private static void EnsurePathsCreated()
    {
        if (_pathsEnsured) return;
        AppPaths.EnsureCreated();
        _pathsEnsured = true;
    }

    private static Task<byte[]?> ReadOrCreateCacheBytesAsync(string key, string cachePath, bool dockPresentation) =>
        Task.Run(async () =>
        {
            if (!File.Exists(cachePath))
            {
                try
                {
                    await EnsureDiskCacheFileAsync(key, cachePath, dockPresentation).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Shell 图标提取失败：{key}", ex);
                }
            }

            if (!File.Exists(cachePath)) return null;
            try
            {
                return await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"图标缓存读取失败：{cachePath}", ex);
                return null;
            }
        });

    private bool TryGetMemoryCached(string memoryKey, string? requiredIdentity, out BitmapImage? image)
    {
        lock (_cacheSync)
        {
            if (!_memoryCache.TryGetValue(memoryKey, out LinkedListNode<CacheEntry>? node))
            {
                image = null;
                return false;
            }
            if (requiredIdentity is not null &&
                !string.Equals(node.Value.Identity, requiredIdentity, StringComparison.Ordinal))
            {
                _lru.Remove(node);
                _memoryCache.Remove(memoryKey);
                image = null;
                return false;
            }
            if (!ReferenceEquals(_lru.First, node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }
            image = node.Value.Image;
            return true;
        }
    }

    private void StoreMemoryCached(string memoryKey, string identity, BitmapImage image)
    {
        lock (_cacheSync)
        {
            if (_memoryCache.TryGetValue(memoryKey, out LinkedListNode<CacheEntry>? existing))
            {
                _lru.Remove(existing);
                _memoryCache.Remove(memoryKey);
            }
            LinkedListNode<CacheEntry> added = _lru.AddFirst(new CacheEntry(memoryKey, identity, image));
            _memoryCache[memoryKey] = added;
            while (_memoryCache.Count > MaximumMemoryCachedIcons && _lru.Last is { } oldest)
            {
                _lru.RemoveLast();
                _memoryCache.Remove(oldest.Value.MemoryKey);
            }
        }
    }

    private static Task EnsureDiskCacheFileAsync(string path, string cachePath, bool dockPresentation)
    {
        if (File.Exists(cachePath)) return Task.CompletedTask;
        lock (InflightSync)
        {
            if (InflightRefreshes.TryGetValue(cachePath, out Task? existing)) return existing;
            Task created = RunRefreshOnceAsync(path, cachePath, dockPresentation);
            InflightRefreshes[cachePath] = created;
            return created;
        }
    }

    private static async Task RunRefreshOnceAsync(string path, string cachePath, bool dockPresentation)
    {
        try
        {
            if (!File.Exists(cachePath))
                await RefreshAsync(path, cachePath, dockPresentation).ConfigureAwait(false);
        }
        finally
        {
            lock (InflightSync) _ = InflightRefreshes.Remove(cachePath);
        }
    }

    internal static string BuildCacheIdentity(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return BuildCacheIdentity(fullPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildCacheIdentity(string fullPath, HashSet<string> visited)
    {
        try
        {
            if (File.Exists(fullPath))
            {
                var file = new FileInfo(fullPath);
                string identity = $"{fullPath}|file|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
                if (!visited.Add(fullPath)) return identity;
                string extension = Path.GetExtension(fullPath);
                if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    TryReadShellLinkIconLocation(fullPath, out string iconPath, out int iconIndex))
                {
                    iconPath = NormalizeShortcutResourcePath(fullPath, iconPath);
                    identity += $"|icon-index|{iconIndex}|target|{BuildCacheIdentity(iconPath, visited)}";
                }
                else if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                         TryReadShellLinkTarget(fullPath, out string targetPath))
                {
                    targetPath = NormalizeShortcutResourcePath(fullPath, targetPath);
                    identity += $"|target-path|{BuildCacheIdentity(targetPath, visited)}";
                }
                else if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    string iconPathValue = ReadInternetShortcutValue(fullPath, "IconFile");
                    if (!string.IsNullOrWhiteSpace(iconPathValue))
                    {
                        iconPathValue = NormalizeShortcutResourcePath(fullPath, iconPathValue.Trim().Trim('"'));
                        int urlIconIndex = int.TryParse(
                            ReadInternetShortcutValue(fullPath, "IconIndex"),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int parsedIndex) ? parsedIndex : 0;
                        identity += $"|icon-index|{urlIconIndex}|target|{BuildCacheIdentity(iconPathValue, visited)}";
                    }
                }
                return identity;
            }
            if (Directory.Exists(fullPath))
            {
                var directory = new DirectoryInfo(fullPath);
                return $"{fullPath}|directory|0|{directory.LastWriteTimeUtc.Ticks}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"{fullPath}|unavailable|0|0";
        }
        return $"{fullPath}|missing|0|0";
    }

    private static string NormalizeShortcutResourcePath(string shortcutPath, string resourcePath)
    {
        string normalized = Environment.ExpandEnvironmentVariables(resourcePath.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(normalized))
            normalized = Path.Combine(Path.GetDirectoryName(shortcutPath)!, normalized);
        return Path.GetFullPath(normalized);
    }

    private static async Task RefreshAsync(string path, string cachePath, bool dockPresentation)
    {
        IconSnapshot? thumbnail = await TryExtractImageThumbnailAsync(path).ConfigureAwait(false);
        IconSnapshot snapshot = thumbnail is not null
            ? (dockPresentation ? await Task.Run(() => NormalizeDockIcon(thumbnail)).ConfigureAwait(false) : thumbnail)
            : await Task.Run(() =>
            {
                IconSnapshot shellIcon = ExtractShellIconPixels(path);
                return dockPresentation ? NormalizeDockIcon(shellIcon) : shellIcon;
            }).ConfigureAwait(false);

        string directory = Path.GetDirectoryName(cachePath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(cachePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var memory = new InMemoryRandomAccessStream();
            using (SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                snapshot.Pixels.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                snapshot.Width,
                snapshot.Height,
                BitmapAlphaMode.Premultiplied))
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory).AsTask().ConfigureAwait(false);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync().AsTask().ConfigureAwait(false);
            }
            memory.Seek(0);
            await using (var output = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }))
            {
                await memory.AsStreamForRead().CopyToAsync(output).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task<IconSnapshot?> TryExtractImageThumbnailAsync(string path)
    {
        if (!File.Exists(path)) return null;
        string extension = Path.GetExtension(path);
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
            IsIconResourceExtension(extension)) return null;
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
            if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;

            using StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                JumboSize,
                ThumbnailOptions.ResizeThumbnail).AsTask().ConfigureAwait(false);
            if (thumbnail is null || thumbnail.Type != ThumbnailType.Image) return null;

            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(thumbnail).AsTask().ConfigureAwait(false);
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied).AsTask().ConfigureAwait(false);
            uint byteCount = checked((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
            var buffer = new Windows.Storage.Streams.Buffer(byteCount);
            bitmap.CopyToBuffer(buffer);
            return new IconSnapshot(buffer.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"图片缩略图提取失败，已回退 Shell 图标：{path}", ex);
            return null;
        }
    }

    internal static IconSnapshot ExtractShellIconPixels(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
            IsIconResourceExtension(extension))
        {
            if (TryGetShortcutIcon(path, JumboSize, out IntPtr shortcutIcon))
            {
                try { return new(DrawIconPixels(shortcutIcon, JumboSize), JumboSize, JumboSize); }
                finally { _ = NativeMethods.DestroyIcon(shortcutIcon); }
            }
            if (TryGetResourceIcon(path, JumboSize, out IntPtr resourceIcon))
            {
                try { return new(DrawIconPixels(resourceIcon, JumboSize), JumboSize, JumboSize); }
                finally { _ = NativeMethods.DestroyIcon(resourceIcon); }
            }
            if (TryExtractShellItemImage(path, JumboSize, out IconSnapshot shortcutShellItem))
            {
                return PreferJumboIfLowRes(path, shortcutShellItem);
            }
            IntPtr icon = GetShellFallbackIcon(path, out int sourceSize);
            try
            {
                return new(DrawIconPixels(icon, sourceSize), sourceSize, sourceSize);
            }
            finally
            {
                _ = NativeMethods.DestroyIcon(icon);
            }
        }
        if (TryExtractShellItemImage(path, JumboSize, out IconSnapshot shellItemImage))
        {
            return PreferJumboIfLowRes(path, shellItemImage);
        }

        IntPtr fallbackIcon = GetShellFallbackIcon(path, out int fallbackSourceSize);
        try
        {
            return new(DrawIconPixels(fallbackIcon, fallbackSourceSize), fallbackSourceSize, fallbackSourceSize);
        }
        finally
        {
            _ = NativeMethods.DestroyIcon(fallbackIcon);
        }
    }

    private static IconSnapshot PreferJumboIfLowRes(string path, IconSnapshot shellItemImage)
    {
        if (shellItemImage.Width >= 64 && shellItemImage.Height >= 64)
            return shellItemImage;

        if (TryGetJumboIcon(path, out IntPtr jumboIcon))
        {
            try
            {
                return new(DrawIconPixels(jumboIcon, JumboSize), JumboSize, JumboSize);
            }
            catch
            {
                // Fall back to the ShellItem image if drawing Jumbo fails.
            }
            finally
            {
                _ = NativeMethods.DestroyIcon(jumboIcon);
            }
        }
        return shellItemImage;
    }

    private static IntPtr GetPreferredIcon(string path, int requestedSize, out int sourceSize)
    {
        if (TryGetShortcutIcon(path, requestedSize, out IntPtr shortcutIcon))
        {
            sourceSize = requestedSize;
            return shortcutIcon;
        }
        if (TryGetResourceIcon(path, requestedSize, out IntPtr resourceIcon))
        {
            sourceSize = requestedSize;
            return resourceIcon;
        }
        return GetShellFallbackIcon(path, out sourceSize);
    }

    private static IntPtr GetShellFallbackIcon(string path, out int sourceSize)
    {
        if (TryGetJumboIcon(path, out IntPtr jumboIcon))
        {
            sourceSize = JumboSize;
            return jumboIcon;
        }
        if (TryGetExtraLargeIcon(path, out IntPtr extraLargeIcon))
        {
            sourceSize = ExtraLargeSize;
            return extraLargeIcon;
        }
        sourceSize = FallbackSize;
        return GetFallbackIcon(path);
    }

    private static bool IsIconResourceExtension(string extension) =>
        extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".scr", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cpl", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ocx", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".sys", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetResourceIcon(string path, int size, out IntPtr icon)
    {
        icon = IntPtr.Zero;
        if (!IsIconResourceExtension(Path.GetExtension(path)) || !File.Exists(path)) return false;
        try
        {
            int result = NativeMethods.SHDefExtractIcon(path, 0, 0, out icon, out IntPtr smallIcon, (uint)size);
            if (smallIcon != IntPtr.Zero) _ = NativeMethods.DestroyIcon(smallIcon);
            if (result == 0 && icon != IntPtr.Zero) return true;
            if (icon != IntPtr.Zero) _ = NativeMethods.DestroyIcon(icon);
        }
        catch
        {
            // Fall through to the Shell image list/fallback path.
        }
        icon = IntPtr.Zero;
        return false;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string ReadInternetShortcutValue(string path, string key)
    {
        if (TryReadInternetShortcutValueManaged(path, key, out string managedValue))
            return managedValue;

        var value = new StringBuilder(32768);
        _ = NativeMethods.GetPrivateProfileString("InternetShortcut", key, string.Empty, value, (uint)value.Capacity, path);
        return value.ToString();
    }

    private static bool TryReadInternetShortcutValueManaged(string path, string key, out string value)
    {
        value = string.Empty;
        try
        {
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > 128 * 1024) return false;
            byte[] raw = File.ReadAllBytes(path);
            string text;
            if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            {
                text = Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
            }
            else if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            {
                text = Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
            }
            else if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            {
                text = StrictUtf8.GetString(raw, 3, raw.Length - 3);
            }
            else
            {
                // Strict UTF-8 decoding preserves CJK paths in UTF-8 .url files; invalid UTF-8
                // throws DecoderFallbackException so legacy ANSI .url files fall back cleanly.
                text = StrictUtf8.GetString(raw);
            }

            bool inSection = false;
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
                if (trimmed.IsEmpty || trimmed[0] == ';' || trimmed[0] == '#') continue;
                if (trimmed[0] == '[' && trimmed[^1] == ']')
                {
                    ReadOnlySpan<char> section = trimmed[1..^1].Trim();
                    inSection = section.Equals("InternetShortcut", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                int eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                ReadOnlySpan<char> candidateKey = trimmed[..eq].Trim();
                if (!candidateKey.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                string parsed = trimmed[(eq + 1)..].Trim().ToString();
                if (parsed.Length > 0)
                {
                    value = parsed;
                    return true;
                }
            }
        }
        catch
        {
            // Fall back to GetPrivateProfileString for legacy ANSI or inaccessible files.
        }
        return false;
    }

    private static bool TryGetShortcutIcon(string path, int size, out IntPtr icon)
    {
        icon = IntPtr.Zero;
        string extension = Path.GetExtension(path);
        if ((!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
             !extension.Equals(".url", StringComparison.OrdinalIgnoreCase)) ||
            !File.Exists(path)) return false;
        return TryGetShortcutIcon(path, size, new HashSet<string>(StringComparer.OrdinalIgnoreCase), out icon);
    }

    private static bool TryGetShortcutIcon(
        string path,
        int size,
        HashSet<string> visited,
        out IntPtr icon)
    {
        icon = IntPtr.Zero;
        string current = Path.GetFullPath(path);
        if (!visited.Add(current) || visited.Count > MaximumShortcutIconDepth) return false;
        try
        {
            string extension = Path.GetExtension(current);
            string iconPath;
            int iconIndex;
            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadShellLinkIconLocation(current, out iconPath, out iconIndex))
                {
                    if (!TryReadShellLinkTarget(current, out iconPath)) return false;
                    iconIndex = 0;
                }
            }
            else if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                iconPath = ReadInternetShortcutValue(current, "IconFile");
                if (string.IsNullOrWhiteSpace(iconPath)) return false;
                iconIndex = int.TryParse(
                    ReadInternetShortcutValue(current, "IconIndex"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsedIndex) ? parsedIndex : 0;
            }
            else return false;

            iconPath = NormalizeShortcutResourcePath(current, iconPath);
            if (iconPath.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(iconPath)) return false;
            string targetExtension = Path.GetExtension(iconPath);
            if (targetExtension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                targetExtension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                return TryGetShortcutIcon(iconPath, size, visited, out icon);

            int result = NativeMethods.SHDefExtractIcon(
                iconPath, iconIndex, 0, out icon, out IntPtr smallIcon, (uint)size);
            if (smallIcon != IntPtr.Zero) _ = NativeMethods.DestroyIcon(smallIcon);
            if (result == 0 && icon != IntPtr.Zero) return true;
            if (icon != IntPtr.Zero) _ = NativeMethods.DestroyIcon(icon);
        }
        catch
        {
            // Fall through to the normal Shell icon fallback.
        }
        icon = IntPtr.Zero;
        return false;
    }

    private static bool TryReadShellLinkIconLocation(string shortcutPath, out string iconPath, out int iconIndex)
    {
        iconPath = string.Empty;
        iconIndex = 0;
        NativeMethods.IShellLinkW? shellLink = null;
        try
        {
            shellLink = (NativeMethods.IShellLinkW)new NativeMethods.ShellLink();
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink).Load(shortcutPath, 0);
            var value = new StringBuilder(32768);
            if (shellLink.GetIconLocation(value, value.Capacity, out iconIndex) < 0 || value.Length == 0) return false;
            iconPath = value.ToString();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (shellLink is not null && Marshal.IsComObject(shellLink)) _ = Marshal.FinalReleaseComObject(shellLink);
        }
    }

    private static bool TryReadShellLinkTarget(string shortcutPath, out string targetPath) =>
        ShellLinkTarget.TryRead(shortcutPath, out targetPath);

    private static bool TryExtractShellItemImage(string path, int size, out IconSnapshot snapshot)
    {
        snapshot = null!;
        if (!File.Exists(path) && !Directory.Exists(path)) return false;

        NativeMethods.IShellItemImageFactory? imageFactory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            Guid interfaceId = typeof(NativeMethods.IShellItemImageFactory).GUID;
            if (NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out imageFactory) < 0)
                return false;
            if (imageFactory.GetImage(
                    new NativeMethods.SIZE { Width = size, Height = size },
                    NativeMethods.SIIGBF_ICONONLY,
                    out bitmap) < 0 || bitmap == IntPtr.Zero)
            {
                return false;
            }
            if (NativeMethods.GetObject(bitmap, Marshal.SizeOf<NativeMethods.BITMAP>(), out NativeMethods.BITMAP details) == 0 ||
                details.Width <= 0 || details.Height == 0)
            {
                return false;
            }

            int width = details.Width;
            int height = Math.Abs(details.Height);
            byte[] pixels = new byte[checked(width * height * 4)];
            var bitmapInfo = new NativeMethods.BITMAPINFO
            {
                Header = new NativeMethods.BITMAPINFOHEADER
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = NativeMethods.BI_RGB,
                    SizeImage = (uint)pixels.Length
                }
            };
            IntPtr dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
            try
            {
                if (dc == IntPtr.Zero || NativeMethods.GetDIBits(
                        dc,
                        bitmap,
                        0,
                        (uint)height,
                        pixels,
                        ref bitmapInfo,
                        NativeMethods.DIB_RGB_COLORS) == 0)
                {
                    return false;
                }
            }
            finally
            {
                if (dc != IntPtr.Zero) _ = NativeMethods.DeleteDC(dc);
            }

            RepairMissingAlpha(pixels);
            PremultiplyAlpha(pixels);
            snapshot = new IconSnapshot(pixels, width, height);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) _ = NativeMethods.DeleteObject(bitmap);
            if (imageFactory is not null && Marshal.IsComObject(imageFactory)) _ = Marshal.FinalReleaseComObject(imageFactory);
        }
    }

    private static bool TryGetJumboIcon(string path, out IntPtr icon) =>
        TryGetShellImageListIcon(path, NativeMethods.SHIL_JUMBO, out icon);

    private static bool TryGetExtraLargeIcon(string path, out IntPtr icon) =>
        TryGetShellImageListIcon(path, ShIlExtraLarge, out icon);

    private static bool TryGetShellImageListIcon(string path, int imageListType, out IntPtr icon)
    {
        icon = IntPtr.Zero;
        var shellInfo = new NativeMethods.SHFILEINFO { DisplayName = string.Empty, TypeName = string.Empty };
        UIntPtr result = NativeMethods.SHGetFileInfo(
            path,
            0,
            ref shellInfo,
            (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SYSICONINDEX);
        if (result == UIntPtr.Zero) return false;

        if (shellInfo.Icon != IntPtr.Zero) _ = NativeMethods.DestroyIcon(shellInfo.Icon);

        Guid interfaceId = typeof(NativeMethods.IImageList).GUID;
        int hr = NativeMethods.SHGetImageList(imageListType, ref interfaceId, out NativeMethods.IImageList imageList);
        if (hr < 0) return false;
        try
        {
            int imageIndex = shellInfo.IconIndex & 0x00FFFFFF;
            return imageList.GetIcon(imageIndex, NativeMethods.ILD_TRANSPARENT, out icon) >= 0 && icon != IntPtr.Zero;
        }
        finally
        {
            if (Marshal.IsComObject(imageList)) _ = Marshal.ReleaseComObject(imageList);
        }
    }

    private static IntPtr GetFallbackIcon(string path)
    {
        var shellInfo = new NativeMethods.SHFILEINFO { DisplayName = string.Empty, TypeName = string.Empty };
        UIntPtr result = NativeMethods.SHGetFileInfo(
            path,
            0,
            ref shellInfo,
            (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
        if (result == UIntPtr.Zero || shellInfo.Icon == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Windows Shell 未返回图标：{path}");
        }
        return shellInfo.Icon;
    }

    private static byte[] DrawIconPixels(IntPtr icon, int size)
    {
        var bitmapInfo = new NativeMethods.BITMAPINFO
        {
            Header = new NativeMethods.BITMAPINFOHEADER
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                Width = size,
                Height = -size,
                Planes = 1,
                BitCount = 32,
                Compression = NativeMethods.BI_RGB,
                SizeImage = (uint)(size * size * 4)
            }
        };
        IntPtr dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        IntPtr bitmap = NativeMethods.CreateDIBSection(dc, ref bitmapInfo, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr previous = IntPtr.Zero;
        try
        {
            if (dc == IntPtr.Zero || bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法创建图标缓冲区。");
            }
            previous = NativeMethods.SelectObject(dc, bitmap);
            byte[] pixels = new byte[size * size * 4];
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            if (!NativeMethods.DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, NativeMethods.DI_NORMAL))
            {
                throw new InvalidOperationException("无法绘制 Shell 图标。");
            }
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            RepairMissingAlpha(pixels);
            return pixels;
        }
        finally
        {
            if (previous != IntPtr.Zero && dc != IntPtr.Zero) _ = NativeMethods.SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) _ = NativeMethods.DeleteObject(bitmap);
            if (dc != IntPtr.Zero) _ = NativeMethods.DeleteDC(dc);
        }
    }

    private static void RepairMissingAlpha(byte[] pixels)
    {
        bool hasAlpha = false;
        for (int index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0)
            {
                hasAlpha = true;
                break;
            }
        }
        if (hasAlpha) return;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0 || pixels[index + 1] != 0 || pixels[index + 2] != 0) pixels[index + 3] = 255;
        }
    }

    private static void PremultiplyAlpha(byte[] pixels)
    {
        for (int index = 0; index < pixels.Length; index += 4)
        {
            int alpha = pixels[index + 3];
            if (alpha == 255) continue;
            if (alpha == 0)
            {
                pixels[index] = 0;
                pixels[index + 1] = 0;
                pixels[index + 2] = 0;
                continue;
            }
            pixels[index] = (byte)((pixels[index] * alpha + 127) / 255);
            pixels[index + 1] = (byte)((pixels[index + 1] * alpha + 127) / 255);
            pixels[index + 2] = (byte)((pixels[index + 2] * alpha + 127) / 255);
        }
    }

    private static void UnpremultiplyAlpha(byte[] pixels)
    {
        for (int index = 0; index < pixels.Length; index += 4)
        {
            int alpha = pixels[index + 3];
            if (alpha is 0 or 255) continue;
            pixels[index] = (byte)Math.Min(255, pixels[index] * 255 / alpha);
            pixels[index + 1] = (byte)Math.Min(255, pixels[index + 1] * 255 / alpha);
            pixels[index + 2] = (byte)Math.Min(255, pixels[index + 2] * 255 / alpha);
        }
    }

    private static IntPtr CreateBitmap(byte[] pixels, int size)
    {
        var bitmapInfo = new NativeMethods.BITMAPINFO
        {
            Header = new NativeMethods.BITMAPINFOHEADER
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                Width = size,
                Height = -size,
                Planes = 1,
                BitCount = 32,
                Compression = NativeMethods.BI_RGB,
                SizeImage = (uint)pixels.Length
            }
        };
        IntPtr bitmap = NativeMethods.CreateDIBSection(IntPtr.Zero, ref bitmapInfo, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (bitmap != IntPtr.Zero) _ = NativeMethods.DeleteObject(bitmap);
            throw new InvalidOperationException("无法创建Shell拖动图像。");
        }
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        return bitmap;
    }

    internal static IconSnapshot NormalizeDockIcon(IconSnapshot snapshot)
    {
        if (!TryGetVisiblePixelBounds(snapshot, out IconVisibleBounds bounds) ||
            bounds.Width == snapshot.Width && bounds.Height == snapshot.Height) return snapshot;
        byte[] pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
        for (int y = 0; y < bounds.Height; y++)
            System.Buffer.BlockCopy(snapshot.Pixels, ((bounds.Top + y) * snapshot.Width + bounds.Left) * 4,
                pixels, y * bounds.Width * 4, bounds.Width * 4);
        // Keep the actual aspect ratio. Image.Stretch=Uniform fits its longest edge to the Dock slot.
        return new(pixels, bounds.Width, bounds.Height);
    }

    internal static bool TryGetVisiblePixelBounds(
        IconSnapshot snapshot,
        out IconVisibleBounds bounds,
        byte alphaThreshold = 16)
    {
        int left = snapshot.Width;
        int top = snapshot.Height;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                int alphaIndex = (y * snapshot.Width + x) * 4 + 3;
                if (snapshot.Pixels[alphaIndex] < alphaThreshold) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        if (right < left || bottom < top)
        {
            bounds = default;
            return false;
        }
        bounds = new(left, top, right + 1, bottom + 1);
        return true;
    }

    private static string Hash(string path)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{CacheVersion}|{path.ToUpperInvariant()}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal readonly record struct IconVisibleBounds(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
    }

    internal sealed record IconSnapshot(byte[] Pixels, int Width, int Height)
    {
        internal int Size => Width;
    }
}
