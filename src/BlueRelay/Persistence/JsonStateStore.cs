using System.Text.Json;
using System.Text.Json.Serialization;
using BlueRelay.Models;

namespace BlueRelay.Persistence;

public sealed class JsonStateStore : IStateStore
{
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonStateStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueRelay",
            "state.json");
    }

    public string FilePath { get; }

    public async Task<StateLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return new StateLoadResult(new ApplicationState(), null);
        }

        try
        {
            await using var stream = File.OpenRead(FilePath);
            var state = await JsonSerializer.DeserializeAsync<ApplicationState>(stream, _serializerOptions, cancellationToken)
                .ConfigureAwait(false);
            state ??= new ApplicationState();
            state.Projects ??= [];
            return new StateLoadResult(state, null);
        }
        catch (JsonException exception)
        {
            var backupPath = TryBackupCorruptFile();
            var backupMessage = backupPath is null
                ? "The saved BlueRelay state was damaged and could not be backed up."
                : $"A damaged state file was backed up to '{backupPath}'.";

            return new StateLoadResult(
                new ApplicationState(),
                $"BlueRelay started with a fresh state. {backupMessage} Details: {exception.Message}");
        }
        catch (IOException exception)
        {
            return new StateLoadResult(
                new ApplicationState(),
                $"BlueRelay could not read its saved state and started with an empty state. Details: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return new StateLoadResult(
                new ApplicationState(),
                $"BlueRelay could not access its saved state and started with an empty state. Details: {exception.Message}");
        }
    }

    public async Task SaveAsync(ApplicationState state, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The BlueRelay state path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, _serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, FilePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string? TryBackupCorruptFile()
    {
        try
        {
            var backupPath = $"{FilePath}.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}.bak";
            File.Copy(FilePath, backupPath, false);
            return backupPath;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
