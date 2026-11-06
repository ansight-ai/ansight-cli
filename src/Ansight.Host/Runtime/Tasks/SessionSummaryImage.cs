using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using Ansight.Host.Sessions.Video;

namespace Ansight.Host.Runtime.Tasks;

internal sealed record SessionSummaryImage(string FilePath, DateTimeOffset CapturedAtUtc, string Source);
