using BlueRelay.Models;

namespace BlueRelay.Services;

public static class WorkstreamValidator
{
    public static bool TryValidate(
        string name,
        IEnumerable<Workstream> existingWorkstreams,
        Guid? workstreamId,
        out string normalizedName,
        out string error)
    {
        normalizedName = name.Trim();
        if (normalizedName.Length is < 1 or > 80)
        {
            error = "Workstream name must be between 1 and 80 characters.";
            return false;
        }

        if (normalizedName.Any(char.IsControl) || normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Workstream name contains characters that are not valid on Windows.";
            return false;
        }

        var candidateName = normalizedName;
        if (existingWorkstreams.Any(workstream =>
                workstream.Id != workstreamId &&
                string.Equals(workstream.Name, candidateName, StringComparison.OrdinalIgnoreCase)))
        {
            error = "A workstream with this name already exists.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
