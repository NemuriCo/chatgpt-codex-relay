using BlueRelay.Models;

namespace BlueRelay.Services;

public sealed record ProjectMutationResult(bool Success, string Error, Project? Project = null);
