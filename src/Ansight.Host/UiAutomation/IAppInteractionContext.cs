using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.UiAutomation;

/// <summary>Stateful app interaction, independent of the connection carrying commands.</summary>
public interface IAppInteractionContext
{
    string SessionId { get; }

    Task<AppInteractionResult> ExecuteAsync(AppInteractionRequest request, CancellationToken cancellationToken = default);
}
