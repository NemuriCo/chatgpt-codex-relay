using BlueRelay.Models;

namespace BlueRelay.Services;

public sealed record WorkstreamMutationResult(bool Success, string Error, Workstream? Workstream = null);
