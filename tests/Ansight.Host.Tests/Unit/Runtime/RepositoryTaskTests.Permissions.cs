using System.Text.RegularExpressions;
using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Fact]
    public void PermissionsTypeScriptContractAcceptsNativeApisAndRejectsOverrides()
    {
        if (LocalTypeScriptTaskCompiler.ResolveCompilerPath() is null) return;
        using var repository = CreateRepository("export const placeholder = true;");
        var path = Path.Combine(repository.RootPath, "permissions.ts");
        File.WriteAllText(path, """
            import type { TaskInvocation, PermissionResult, AppPermission, CommonAndroidPermission } from "./ansight-task.d.ts";
            import { Permission, IosPermission, AndroidPermission } from "./ansight-task.js";
            export default async function run({ ansight }: TaskInvocation) {
              const result: PermissionResult = await ansight.permissions.query({ permission: "microphone" });
              const shared: "microphone" = ansight.permissions.names.microphone;
              const ios: "location-always" = ansight.permissions.ios.names.locationAlways;
              const android: "android.permission.CAMERA" = ansight.permissions.android.names.camera;
              const sharedConstant: "camera" = Permission.Camera;
              const iosConstant: "photos-add" = IosPermission.PhotosAdd;
              const androidConstant: "android.permission.RECORD_AUDIO" = AndroidPermission.RecordAudio;
              const namedShared: Permission = sharedConstant;
              const namedIos: IosPermission = iosConstant;
              const namedAndroid: AndroidPermission = androidConstant;
              await ansight.permissions.grant({ permission: namedShared });
              await ansight.permissions.ios.query({ permission: namedIos });
              await ansight.permissions.android.query({ permission: namedAndroid });
              // @ts-expect-error Named constants reject typos.
              Permission.Camra;
              // @ts-expect-error Named constants are readonly.
              AndroidPermission.Camera = "android.permission.CAMERA";
              // @ts-expect-error Native iOS constants differ from shared names.
              await ansight.permissions.grant({ permission: IosPermission.LocationAlways });
              const sharedPermission: AppPermission = shared;
              const iosPermission: IosPermission = ios;
              const androidPermission: CommonAndroidPermission = android;
              await ansight.permissions.grant({ permission: sharedPermission });
              await ansight.permissions.ios.reset({ permission: iosPermission });
              await ansight.permissions.android.grant({ permission: androidPermission });
              await ansight.permissions.android.query({ permission: "com.example.permission.CUSTOM" });
              // @ts-expect-error Shared names reject typos.
              ansight.permissions.names.microfone;
              // @ts-expect-error Native iOS names reject typos.
              ansight.permissions.ios.names.photosAdded;
              // @ts-expect-error Native Android names reject typos.
              ansight.permissions.android.names.recordVideo;
              // @ts-expect-error Catalog values are readonly.
              ansight.permissions.names.camera = "camera";
              // @ts-expect-error iOS catalog values are readonly.
              ansight.permissions.ios.names.photos = "photos";
              // @ts-expect-error Android catalog values are readonly.
              ansight.permissions.android.names.camera = "android.permission.CAMERA";
              // @ts-expect-error The catalog itself is readonly.
              ansight.permissions.names = ansight.permissions.names;
              // @ts-expect-error Generic names do not accept exact Android identifiers.
              await ansight.permissions.grant({ permission: androidPermission });
              // @ts-expect-error Native iOS names differ from shared resource names.
              await ansight.permissions.ios.query({ permission: ansight.permissions.names.locationAlways });
              await ansight.permissions.ios.reset({ permission: "location-always" });
              await ansight.permissions.android.revoke({ permission: "android.permission.CAMERA" });
              // @ts-expect-error Shared permissions use generic resource names.
              await ansight.permissions.grant({ permission: "android.permission.CAMERA" });
              // @ts-expect-error Native Android permissions must be fully qualified.
              await ansight.permissions.android.query({ permission: "camera" });
              // @ts-expect-error The task cannot target another device.
              await ansight.permissions.query({ permission: "photos", deviceId: "override" });
              // @ts-expect-error iOS names match native simctl services.
              await ansight.permissions.ios.grant({ permission: "locationAlways" });
            }
            """);
        Assert.Empty(LocalTypeScriptTaskCompiler.Validate(repository.RootPath, path, RepositoryModuleContractArtifacts.GetTaskTypeDefinitions()));
    }

    [Fact]
    public async Task PermissionsNestedRuntimePinsSessionAndUsesStandardMethods()
    {
        const string module = """
            import { Permission, IosPermission, AndroidPermission } from "../ansight-task.js";
            export const task = {
              "schemaVersion": 1, "appId": "com.example.app", "title": "Permissions",
              "description": "Native permissions", "timeoutSeconds": 10, "maximumActions": 4
            };
            export default async function run({ ansight, expect }) {
              await ansight.permissions.grant({ permission: Permission.Microphone, deviceId: "wrong", sessionId: "wrong" });
              await ansight.permissions.ios.revoke({ permission: IosPermission.Microphone, bundleIdentifier: "wrong" });
              await ansight.permissions.android.query({ permission: AndroidPermission.Camera });
              await ansight.permissions.ios.reset({ permission: IosPermission.Photos });
              expect(Object.isFrozen(ansight.permissions.ios), { id: "immutable" }).toBe(true);
            }
            """;
        using var repository = CreateRepository(module);
        File.WriteAllText(Path.Combine(repository.RootPath, "ansight", "tasks", "ansight-task.js"), RepositoryModuleContractArtifacts.GetTaskRuntimeModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        Assert.Empty(task.DeclaredHostTools);
        Assert.Empty(LocalTaskExtractionCoordinator.ValidateTaskApiSurface(module));
        var calls = new List<string>();
        var permissions = new List<string>();
        var executor = new JavaScriptRepositoryTaskExecutor("node", (name, arguments, _) =>
        {
            calls.Add(name);
            permissions.Add(arguments["permission"]!.GetValue<string>());
            Assert.Equal("enforced-session", arguments["sessionId"]!.GetValue<string>());
            Assert.Null(arguments["deviceId"]);
            Assert.Null(arguments["bundleIdentifier"]);
            return Task.FromResult(RequestResult.ToolResult(new JsonObject { ["status"] = "granted" }, isError: false));
        }, hostApiSuites: RepositoryJavaScriptApiMethods.StandardHostToolSuites);
        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest("run", task, "enforced-session", new JsonObject(), "permissions"), default);
        Assert.True(result.Status == RepositoryTaskRunStatus.Passed, result.Message);
        Assert.Equal(["ansight_grant_permission", "ansight_revoke_ios_permission", "ansight_query_android_permission", "ansight_reset_ios_permission"], calls);
        Assert.Equal(["microphone", "microphone", "android.permission.CAMERA", "photos"], permissions);
    }

    [Fact]
    public async Task PermissionNameCatalogsMatchTypeScriptLiteralsAndAreFrozen()
    {
        var contract = RepositoryModuleContractArtifacts.GetTaskTypeDefinitions();
        var expected = new JsonObject();
        var expectedNamed = new JsonObject();
        var catalogInterfaces = new Dictionary<string, string>
        {
            ["shared"] = "AppPermissionNames",
            ["ios"] = "IosPermissionNames",
            ["android"] = "AndroidPermissionNames"
        };
        foreach (var entry in catalogInterfaces)
        {
            var declaration = Regex.Match(contract, $@"export interface {entry.Value} \{{(?<body>[\s\S]*?)\}}");
            Assert.True(declaration.Success);
            var catalog = new JsonObject();
            foreach (Match member in Regex.Matches(declaration.Groups["body"].Value, """readonly (?<name>\w+): "(?<value>[^"]+)";"""))
                catalog[member.Groups["name"].Value] = member.Groups["value"].Value;
            Assert.NotEmpty(catalog);
            expected[entry.Key] = catalog;
            var named = new JsonObject();
            foreach (var member in catalog)
                named[char.ToUpperInvariant(member.Key[0]) + member.Key[1..]] = member.Value!.DeepClone();
            expectedNamed[entry.Key] = named;
        }
        var module = $$"""
            import { Permission, IosPermission, AndroidPermission } from "../ansight-task.js";
            export const task = {
              "schemaVersion": 1, "appId": "com.example.app", "title": "Permission names",
              "description": "Typed permission catalogs", "timeoutSeconds": 10, "maximumActions": 1
            };
            export default async function run({ ansight, expect }) {
              const catalogs = {
                shared: ansight.permissions.names,
                ios: ansight.permissions.ios.names,
                android: ansight.permissions.android.names
              };
              expect(catalogs, { id: "typed-literals-match-runtime" }).toEqual({{expected.ToJsonString()}});
              const named = { shared: Permission, ios: IosPermission, android: AndroidPermission };
              expect(named, { id: "named-constants-match-types" }).toEqual({{expectedNamed.ToJsonString()}});
              for (const [platform, catalog] of Object.entries(named)) {
                expect(Object.isFrozen(catalog), { id: `${platform}-named-frozen` }).toBe(true);
                let mutationRejected = false;
                try { catalog.Camera = "changed"; } catch { mutationRejected = true; }
                expect(mutationRejected, { id: `${platform}-named-immutable` }).toBe(true);
              }
              for (const [platform, catalog] of Object.entries(catalogs)) {
                expect(Object.isFrozen(catalog), { id: `${platform}-frozen` }).toBe(true);
                let mutationRejected = false;
                try { catalog.camera = "changed"; } catch { mutationRejected = true; }
                expect(mutationRejected, { id: `${platform}-immutable` }).toBe(true);
              }
            }
            """;
        using var repository = CreateRepository(module);
        File.WriteAllText(Path.Combine(repository.RootPath, "ansight", "tasks", "ansight-task.js"), RepositoryModuleContractArtifacts.GetTaskRuntimeModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        Assert.Empty(LocalTaskExtractionCoordinator.ValidateTaskApiSurface(module));
        var executor = new JavaScriptRepositoryTaskExecutor("node", (_, _, _) =>
            throw new InvalidOperationException("Reading permission constants must not call a host tool."),
            hostApiSuites: RepositoryJavaScriptApiMethods.StandardHostToolSuites);
        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest("run", task, "enforced-session", new JsonObject(), "permission-names"), default);
        Assert.True(result.Status == RepositoryTaskRunStatus.Passed, result.Message);
    }

    [Fact]
    public void EveryPermissionConstantDocumentsAvailabilityMappingsAndPlatformLinks()
    {
        var contract = RepositoryModuleContractArtifacts.GetTaskTypeDefinitions();
        foreach (var declarationName in new[] { "interface AppPermissionNames", "interface IosPermissionNames", "interface AndroidPermissionNames", "const Permission:", "const IosPermission:", "const AndroidPermission:" })
        {
            var declaration = Regex.Match(contract, $@"export {Regex.Escape(declarationName)} \{{(?<body>[\s\S]*?)\}}");
            Assert.True(declaration.Success, declarationName);
            var body = declaration.Groups["body"].Value;
            var memberCount = Regex.Matches(body, @"readonly \w+:").Count;
            var documentedMembers = Regex.Matches(body, @"/\*\*(?<docs>[\s\S]*?)\*/\s*readonly \w+:");
            Assert.True(memberCount > 0, declarationName);
            Assert.Equal(memberCount, documentedMembers.Count);
            foreach (Match member in documentedMembers)
            {
                var docs = member.Groups["docs"].Value;
                Assert.Contains("Availability:", docs, StringComparison.Ordinal);
                Assert.Contains("iOS:", docs, StringComparison.Ordinal);
                Assert.Contains("Android:", docs, StringComparison.Ordinal);
                Assert.Contains("@supportedPlatforms", docs, StringComparison.Ordinal);
                Assert.Matches(@"@see https://developer\.(apple|android)\.com/", docs);
            }
        }
    }

}
