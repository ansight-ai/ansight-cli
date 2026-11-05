#import "../../../native/AnsightSimulatorFrameworks.h"

static void Require(BOOL condition, NSString *message)
{
    if (!condition)
    {
        fprintf(stderr, "FAIL: %s\n", message.UTF8String);
        exit(1);
    }
}

static NSString *FrameworkPath(NSString *developerDirectory, BOOL sharedLayout)
{
    if (sharedLayout)
    {
        return [developerDirectory.stringByDeletingLastPathComponent stringByAppendingPathComponent:
            @"SharedFrameworks/SimulatorKit.framework/SimulatorKit"];
    }

    return [developerDirectory stringByAppendingPathComponent:
        @"Library/PrivateFrameworks/SimulatorKit.framework/SimulatorKit"];
}

static void InstallFixture(NSString *source, NSString *destination)
{
    NSError *error = nil;
    BOOL created = [NSFileManager.defaultManager createDirectoryAtPath:destination.stringByDeletingLastPathComponent
                                         withIntermediateDirectories:YES
                                                          attributes:nil
                                                               error:&error];
    Require(created, [NSString stringWithFormat:@"Create fixture directory: %@", error]);

    BOOL copied = [NSFileManager.defaultManager copyItemAtPath:source toPath:destination error:&error];
    Require(copied, [NSString stringWithFormat:@"Copy fixture library: %@", error]);
}

static void RequireVersion(NSString *developerDirectory, int expectedVersion)
{
    NSString *failure = @"stale diagnostic";
    void *framework = AnsightLoadSimulatorKit(developerDirectory, &failure);
    Require(framework != NULL, [NSString stringWithFormat:@"Load fixture: %@", failure]);
    Require(failure == nil, @"A successful fallback must clear the failure diagnostic.");

    int (*readVersion)(void) = (int (*)(void))dlsym(framework, "AnsightFrameworkFixtureVersion");
    Require(readVersion != NULL, @"The loaded library must expose the fixture symbol.");
    Require(readVersion() == expectedVersion, @"Load the expected framework from the selected Xcode layout.");
    dlclose(framework);
}

int main(int argc, const char *argv[])
{
    @autoreleasepool
    {
        Require(argc == 4, @"Supply a test directory and the two fixture libraries.");
        NSString *root = [NSString stringWithUTF8String:argv[1]];
        NSString *legacyFixture = [NSString stringWithUTF8String:argv[2]];
        NSString *sharedFixture = [NSString stringWithUTF8String:argv[3]];

        NSString *legacy = [root stringByAppendingPathComponent:@"Xcode 26.app/Contents/Developer"];
        InstallFixture(legacyFixture, FrameworkPath(legacy, NO));
        RequireVersion(legacy, 26);

        NSString *shared = [root stringByAppendingPathComponent:@"Xcode 27.app/Contents/Developer"];
        InstallFixture(sharedFixture, FrameworkPath(shared, YES));
        RequireVersion(shared, 27);

        NSString *both = [root stringByAppendingPathComponent:@"Both.app/Contents/Developer"];
        InstallFixture(legacyFixture, FrameworkPath(both, NO));
        InstallFixture(sharedFixture, FrameworkPath(both, YES));
        RequireVersion(both, 26);

        NSString *fallback = [root stringByAppendingPathComponent:@"Fallback.app/Contents/Developer"];
        NSString *invalidLibrary = [root stringByAppendingPathComponent:@"invalid-library"];
        NSError *error = nil;
        Require([@"not a Mach-O library" writeToFile:invalidLibrary atomically:YES
                                         encoding:NSUTF8StringEncoding error:&error],
            @"Create the unloadable framework fixture.");
        InstallFixture(invalidLibrary, FrameworkPath(fallback, NO));
        InstallFixture(sharedFixture, FrameworkPath(fallback, YES));
        RequireVersion(fallback, 27);

        NSString *missing = [root stringByAppendingPathComponent:@"Missing.app/Contents/Developer"];
        NSString *failure = nil;
        Require(AnsightLoadSimulatorKit(missing, &failure) == NULL,
            @"Do not load a framework from a different installed Xcode.");
        Require([failure containsString:FrameworkPath(missing, NO)], @"Report the older path attempted.");
        Require([failure containsString:FrameworkPath(missing, YES)], @"Report the newer path attempted.");
        Require([failure containsString:@"Could not load SimulatorKit"], @"Preserve compatibility failure classification.");
        Require(AnsightLoadSimulatorKit(missing, NULL) == NULL, @"The failure output is optional.");

        puts("PASS: SimulatorKit discovery (legacy, shared, preference, fallback, missing, optional diagnostic).");
    }

    return 0;
}
