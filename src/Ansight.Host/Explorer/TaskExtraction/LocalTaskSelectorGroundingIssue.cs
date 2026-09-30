using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host.Models.Session;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Host.Runtime.Sanitization;

namespace Ansight.Host.Explorer.TaskExtraction;

internal sealed record LocalTaskSelectorGroundingIssue(
    LocalTaskSelectorCall Call,
    string Message);
