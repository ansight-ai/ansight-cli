using Ansight.Host.Replay;
using Ansight.Host.Runtime.RepositoryContracts;

namespace Ansight.Host.Tests.Unit.Replay;

public sealed class LocalTaskRuntimeSupportTests
{
    [Theory]
    [InlineData("import type { TaskInvocation } from './ansight-task.d.ts';", false)]
    [InlineData("import type { Permission } from './ansight-task.js';", false)]
    [InlineData("// import { Permission } from './ansight-task.js';", false)]
    [InlineData("/* import './ansight-task.js'; */", false)]
    [InlineData("const example = `import './ansight-task.js';`;", false)]
    [InlineData("import { Permission } from './ansight-task.js';", true)]
    [InlineData("import /* permissions */ {\n Permission\n} from \"./ansight-task.js\";", true)]
    [InlineData("import * as permissions from './ansight-task.js';", true)]
    [InlineData("import './ansight-task.js';", true)]
    [InlineData("const permissions = await import('./ansight-task.js');", true)]
    [InlineData("export { Permission } from './ansight-task.js';", true)]
    public void Save_CreatesRuntimeOnlyForRuntimeImports(string source, bool expected)
    {
        var directory = Directory.CreateTempSubdirectory("ansight-runtime-support-");
        try
        {
            LocalTaskExtractionCoordinator.EnsureRequiredTaskRuntime(directory.FullName, source);
            var path = Path.Combine(directory.FullName, "ansight-task.js");
            Assert.Equal(expected, File.Exists(path));
            if (expected) Assert.Equal(RepositoryModuleContractArtifacts.GetTaskRuntimeModule(), File.ReadAllText(path));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Save_SupportsNestedHelpersAndPreservesExistingRuntime()
    {
        var directory = Directory.CreateTempSubdirectory("ansight-runtime-support-");
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(directory.FullName, "helpers"));
            File.WriteAllText(Path.Combine(nested.FullName, "permissions.ts"), "export { Permission } from '../ansight-task.js';");
            LocalTaskExtractionCoordinator.EnsureRequiredTaskRuntime(directory.FullName, "import './helpers/permissions.ts';");
            var path = Path.Combine(directory.FullName, "ansight-task.js");
            Assert.Equal(RepositoryModuleContractArtifacts.GetTaskRuntimeModule(), File.ReadAllText(path));
            File.WriteAllText(path, "// Existing workspace helper");
            LocalTaskExtractionCoordinator.EnsureRequiredTaskRuntime(directory.FullName, "import './ansight-task.js';");
            Assert.Equal("// Existing workspace helper", File.ReadAllText(path));
        }
        finally { directory.Delete(recursive: true); }
    }
}
