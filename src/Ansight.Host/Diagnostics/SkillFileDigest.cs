using System.Text.Json.Nodes;

namespace Ansight.Host.Diagnostics;

public sealed record SkillFileDigest(string Path, long Bytes, string Sha256);
