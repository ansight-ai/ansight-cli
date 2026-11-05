#ifndef ANSIGHT_SIMULATOR_FRAMEWORKS_H
#define ANSIGHT_SIMULATOR_FRAMEWORKS_H

#import <Foundation/Foundation.h>
#import <dlfcn.h>

// Keep discovery scoped to the selected Xcode. Loading SimulatorKit from a
// different installation can mix incompatible private APIs in the host process.
static inline void *AnsightLoadSimulatorKit(
    NSString *developerDirectory,
    NSString **failureMessage)
{
    NSString *normalizedDirectory = developerDirectory.stringByStandardizingPath;
    NSString *contentsDirectory = normalizedDirectory.stringByDeletingLastPathComponent;
    NSArray<NSString *> *candidates = @[
        [normalizedDirectory stringByAppendingPathComponent:
            @"Library/PrivateFrameworks/SimulatorKit.framework/SimulatorKit"],
        [contentsDirectory stringByAppendingPathComponent:
            @"SharedFrameworks/SimulatorKit.framework/SimulatorKit"],
    ];
    NSMutableArray<NSString *> *failures = [NSMutableArray array];

    for (NSString *path in candidates)
    {
        void *framework = dlopen(path.fileSystemRepresentation, RTLD_NOW | RTLD_LOCAL);
        if (framework != NULL)
        {
            if (failureMessage != NULL)
            {
                *failureMessage = nil;
            }

            // Native classes and function pointers remain in use by the host.
            return framework;
        }

        const char *loaderError = dlerror();
        NSString *detail = loaderError == NULL
            ? @"The dynamic loader returned no diagnostic."
            : [NSString stringWithUTF8String:loaderError];
        [failures addObject:[NSString stringWithFormat:@"%@: %@", path, detail]];
    }

    if (failureMessage != NULL)
    {
        *failureMessage = [NSString stringWithFormat:
            @"Could not load SimulatorKit for %@. Tried:\n%@",
            normalizedDirectory,
            [failures componentsJoinedByString:@"\n"]];
    }

    return NULL;
}

#endif
