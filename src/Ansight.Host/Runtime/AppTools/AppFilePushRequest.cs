using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.AppTools;

public sealed record AppFilePushRequest(
    string LocalFilePath,
    string DirectoryPath,
    string? SandboxRoot,
    string? FileName,
    bool Overwrite,
    bool CreateDirectory);
