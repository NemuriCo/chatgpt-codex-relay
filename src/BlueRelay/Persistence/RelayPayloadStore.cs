using System.Security.Cryptography;
using System.Text;
using BlueRelay.Models;

namespace BlueRelay.Persistence;

/// <summary>
/// Stores potentially large relay bodies outside state.json. The state file keeps
/// only a stable relative path and integrity metadata.
/// </summary>
public sealed class RelayPayloadStore
{
    public RelayPayloadStore(string? rootDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueRelay",
            "relay"));
    }

    public string RootDirectory { get; }

    public async Task<RelayPayload> WriteAsync(
        Guid workstreamId,
        Guid taskId,
        string fileName,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        text ??= string.Empty;

        var directory = Path.Combine(RootDirectory, workstreamId.ToString("D"), taskId.ToString("D"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var bytes = Encoding.UTF8.GetBytes(text);

        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new RelayPayload
        {
            Kind = RelayPayloadKind.TextMarkdown,
            Path = Path.GetRelativePath(RootDirectory, path),
            Length = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        };
    }

    public async Task<string?> ReadAsync(
        RelayPayload? payload,
        CancellationToken cancellationToken = default)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.Path))
        {
            return null;
        }

        var path = Resolve(payload.Path);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (payload.Length >= 0 && bytes.LongLength != payload.Length)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(payload.Sha256) &&
            !string.Equals(
                Convert.ToHexString(SHA256.HashData(bytes)),
                payload.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Encoding.UTF8.GetString(bytes);
    }

    public string? Read(RelayPayload? payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.Path))
        {
            return null;
        }

        var path = Resolve(payload.Path);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        if (payload.Length >= 0 && bytes.LongLength != payload.Length)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(payload.Sha256) &&
            !string.Equals(
                Convert.ToHexString(SHA256.HashData(bytes)),
                payload.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Encoding.UTF8.GetString(bytes);
    }

    public string Resolve(string relativePath)
    {
        var root = RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(RootDirectory, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The relay payload path is outside the BlueRelay payload directory.");
        }

        return path;
    }
}
