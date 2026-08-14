using BlueRelay.Models;

namespace BlueRelay.Services;

public static class ProjectValidator
{
    public static bool TryValidate(
        string name,
        string localPath,
        IEnumerable<Project> existingProjects,
        Guid? projectId,
        out string normalizedName,
        out string normalizedPath,
        out string error)
    {
        normalizedName = name.Trim();
        normalizedPath = localPath.Trim();

        if (normalizedName.Length is < 1 or > 80)
        {
            error = "Project name must be between 1 and 80 characters.";
            return false;
        }

        if (normalizedName.Any(char.IsControl) || normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Project name contains characters that are not valid on Windows.";
            return false;
        }

        var candidateName = normalizedName;
        if (existingProjects.Any(project =>
                project.Id != projectId &&
                string.Equals(project.Name, candidateName, StringComparison.OrdinalIgnoreCase)))
        {
            error = "A project with this name already exists.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            error = "Choose a local project directory.";
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(normalizedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Local path is not valid.";
            return false;
        }

        if (!Directory.Exists(normalizedPath))
        {
            error = "Local path must point to an existing directory.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
