#import "_Xcode27SimulatorHid.h"

#import <dlfcn.h>
#import <fcntl.h>
#import <mach/mach.h>
#import <objc/message.h>
#import <poll.h>
#import <unistd.h>

// The guest XPC connection and DTUHID wire protocol are based on Meta's idb
// implementation. See THIRD-PARTY-NOTICES.md for attribution and its MIT license.
typedef xpc_endpoint_t (*AnsightEndpointFromPort)(mach_port_t, uint64_t, uint64_t) NS_RETURNS_RETAINED;
typedef void (*AnsightEnableSimToHost)(xpc_connection_t);

static NSError *Xcode27HidError(NSInteger code, NSString *message)
{
    return [NSError errorWithDomain:@"AnsightSimulatorHid" code:code
                          userInfo:@{NSLocalizedDescriptionKey: message}];
}

static BOOL CoreSimulatorVersionRequiresXcode27Hid(NSString *version)
{
    return version != nil && [version compare:@"1155.4" options:NSNumericSearch] != NSOrderedAscending;
}

BOOL AnsightSimulatorRequiresXcode27Hid(void)
{
    Class deviceClass = NSClassFromString(@"SimDevice");
    NSString *version = deviceClass == Nil
        ? nil
        : [NSBundle bundleForClass:deviceClass].infoDictionary[@"CFBundleVersion"];
    return CoreSimulatorVersionRequiresXcode27Hid(version);
}

BOOL _Xcode27SimulatorHidIsAvailable(void)
{
    BOOL hasConnectionFunctions = dlsym(RTLD_DEFAULT, "xpc_endpoint_create_mach_port_4sim") != NULL
        && dlsym(RTLD_DEFAULT, "xpc_connection_enable_sim2host_4sim") != NULL;
    Class deviceClass = NSClassFromString(@"SimDevice");
    return hasConnectionFunctions && [deviceClass instancesRespondToSelector:sel_registerName("lookup:error:")];
}

@interface _Xcode27SimulatorHid ()

@property(nonatomic, strong) xpc_connection_t connection;
@property(nonatomic, strong) dispatch_queue_t queue;
@property(atomic, strong) NSError *connectionError;
@property(nonatomic, copy) NSString *developerDirectory;
@property(nonatomic, copy) NSString *deviceUdid;
@property(nonatomic, strong) NSTask *stylusTask;
@property(nonatomic, strong) NSFileHandle *stylusInput;
@property(nonatomic, strong) NSFileHandle *stylusOutput;
@property(nonatomic, strong) NSError *stylusError;

@end

@implementation _Xcode27SimulatorHid

- (instancetype)initWithDevice:(id)device
            developerDirectory:(NSString *)developerDirectory
                    deviceUdid:(NSString *)deviceUdid
                         error:(NSError **)error
{
    self = [super init];
    if (self == nil)
    {
        return nil;
    }

    if (!_Xcode27SimulatorHidIsAvailable())
    {
        if (error != NULL)
        {
            *error = Xcode27HidError(50, @"CoreSimulator requires DTUHID input, but its XPC connection APIs are unavailable.");
        }
        return nil;
    }

    self.queue = dispatch_queue_create("ai.ansight.simulator-dtuhid", DISPATCH_QUEUE_SERIAL);
    self.developerDirectory = developerDirectory;
    self.deviceUdid = deviceUdid;
    NSString *service = @"com.apple.coredevice.feature.remote.hid.digitizer";
    mach_port_t port = ((mach_port_t (*)(id, SEL, NSString *, NSError **))objc_msgSend)(
        device, sel_registerName("lookup:error:"), service, error);
    if (port == MACH_PORT_NULL)
    {
        if (error != NULL && *error == nil)
        {
            *error = Xcode27HidError(51, @"The simulator did not expose its DTUHID service. Wait for it to finish booting.");
        }
        return nil;
    }

    AnsightEndpointFromPort endpointFromPort = (AnsightEndpointFromPort)dlsym(
        RTLD_DEFAULT, "xpc_endpoint_create_mach_port_4sim");
    AnsightEnableSimToHost enableSimToHost = (AnsightEnableSimToHost)dlsym(
        RTLD_DEFAULT, "xpc_connection_enable_sim2host_4sim");
    // The endpoint consumes the service lookup's send right.
    xpc_endpoint_t endpoint = endpointFromPort(port, 0, 0);
    self.connection = endpoint == NULL ? NULL : xpc_connection_create_from_endpoint(endpoint);
    if (self.connection == NULL)
    {
        if (error != NULL)
        {
            *error = Xcode27HidError(52, @"Could not connect to the simulator's DTUHID service.");
        }
        return nil;
    }

    enableSimToHost(self.connection);
    xpc_connection_set_target_queue(self.connection, self.queue);
    __weak _Xcode27SimulatorHid *weakSelf = self;
    xpc_connection_set_event_handler(self.connection, ^(xpc_object_t event) {
        if (xpc_get_type(event) == XPC_TYPE_ERROR)
        {
            const char *description = xpc_dictionary_get_string(event, XPC_ERROR_KEY_DESCRIPTION);
            weakSelf.connectionError = Xcode27HidError(53, [NSString stringWithFormat:
                @"Simulator DTUHID connection interrupted: %s. Restart the input session before continuing the gesture.",
                description ?: "unknown XPC error"]);
        }
    });
    xpc_connection_resume(self.connection);

    // A lookup alone also succeeds when launchd cannot start dtuhidd. Require a
    // reply before reporting availability; usage 0 does not press a key.
    xpc_object_t payload = xpc_dictionary_create(NULL, NULL, 0);
    xpc_dictionary_set_uint64(payload, "usageCode", 0);
    xpc_dictionary_set_uint64(payload, "state", 2);
    if (![self sendMessageType:@"IndigoKeyboardButtonEvent" payload:payload error:error])
    {
        return nil;
    }

    // The reply precedes opening the virtual input device during a cold start.
    usleep(200000);
    return self;
}

- (BOOL)sendMessageType:(NSString *)messageType payload:(xpc_object_t)payload error:(NSError **)error
{
    NSError *connectionError = self.connectionError;
    if (connectionError != nil)
    {
        if (error != NULL)
        {
            *error = connectionError;
        }
        return NO;
    }

    xpc_object_t message = xpc_dictionary_create(NULL, NULL, 0);
    xpc_dictionary_set_string(message, "messageType", messageType.UTF8String);
    xpc_dictionary_set_string(message, "featureIdentifier", "com.apple.coredevice.feature.remote.hid.digitizer");
    xpc_dictionary_set_bool(message, "isBarrier", false);
    xpc_dictionary_set_value(message, "payload", payload);
    xpc_connection_send_message(self.connection, message);

    xpc_object_t barrierPayload = xpc_dictionary_create(NULL, NULL, 0);
    xpc_dictionary_set_uint64(barrierPayload, "usageCode", 0);
    xpc_dictionary_set_uint64(barrierPayload, "state", 2);
    xpc_object_t barrier = xpc_dictionary_create(NULL, NULL, 0);
    xpc_dictionary_set_string(barrier, "messageType", "IndigoKeyboardButtonEvent");
    xpc_dictionary_set_string(barrier, "featureIdentifier", "com.apple.coredevice.feature.remote.hid.digitizer");
    xpc_dictionary_set_bool(barrier, "isBarrier", true);
    xpc_dictionary_set_value(barrier, "payload", barrierPayload);

    dispatch_semaphore_t completed = dispatch_semaphore_create(0);
    __block NSError *replyError = nil;
    xpc_connection_send_message_with_reply(self.connection, barrier, self.queue, ^(xpc_object_t reply) {
        if (xpc_get_type(reply) == XPC_TYPE_ERROR)
        {
            const char *description = xpc_dictionary_get_string(reply, XPC_ERROR_KEY_DESCRIPTION);
            replyError = Xcode27HidError(54, [NSString stringWithFormat:
                @"Simulator DTUHID rejected input: %s", description ?: "unknown XPC error"]);
        }
        dispatch_semaphore_signal(completed);
    });

    if (dispatch_semaphore_wait(completed, dispatch_time(DISPATCH_TIME_NOW, 4 * NSEC_PER_SEC)) != 0)
    {
        // A timeout has uncertain delivery. Never replay the input over another
        // connection or fall back to the suppressed Indigo channel.
        self.connectionError = Xcode27HidError(55, @"Timed out waiting for the simulator's DTUHID service. Input receipt is unknown; restart the input session.");
        if (error != NULL)
        {
            *error = self.connectionError;
        }
        return NO;
    }

    if (replyError != nil)
    {
        self.connectionError = replyError;
        if (error != NULL)
        {
            *error = replyError;
        }
        return NO;
    }

    return YES;
}

- (BOOL)readStylusReply:(NSError **)error
{
    NSMutableData *data = [NSMutableData data];
    double deadline = NSProcessInfo.processInfo.systemUptime + 4;
    while (data.length < 8192)
    {
        double remaining = deadline - NSProcessInfo.processInfo.systemUptime;
        if (remaining <= 0)
        {
            break;
        }
        struct pollfd descriptor = {self.stylusOutput.fileDescriptor, POLLIN, 0};
        int status = poll(&descriptor, 1, (int)(remaining * 1000));
        if (status < 0 && errno == EINTR)
        {
            continue;
        }
        if (status <= 0 || (descriptor.revents & POLLIN) == 0)
        {
            break;
        }
        char byte;
        if (read(descriptor.fd, &byte, 1) != 1)
        {
            break;
        }
        if (byte != '\n')
        {
            [data appendBytes:&byte length:1];
            continue;
        }

        id reply = [NSJSONSerialization JSONObjectWithData:data options:0 error:NULL];
        if ([reply isKindOfClass:NSDictionary.class])
        {
            NSNumber *success = reply[@"isSuccess"];
            if ([success isKindOfClass:NSNumber.class] && success.boolValue)
            {
                return YES;
            }
            NSString *message = reply[@"message"];
            if ([message isKindOfClass:NSString.class] && error != NULL)
            {
                *error = Xcode27HidError(56, message);
                return NO;
            }
        }
        break;
    }

    if (error != NULL)
    {
        *error = Xcode27HidError(57, @"The Xcode 27 Pencil helper did not acknowledge input. Receipt is unknown; restart the input session.");
    }
    return NO;
}

- (BOOL)startStylusHelper:(NSError **)error
{
    Dl_info image;
    if (dladdr((const void *)&AnsightSimulatorRequiresXcode27Hid, &image) == 0 || image.dli_fname == NULL)
    {
        if (error != NULL)
        {
            *error = Xcode27HidError(58, @"Could not locate the bundled Xcode 27 Pencil helper.");
        }
        return NO;
    }

    NSString *libraryPath = [NSString stringWithUTF8String:image.dli_fname];
    NSString *helper = [libraryPath.stringByDeletingLastPathComponent
        stringByAppendingPathComponent:@"AnsightSimulatorStylusGuest"];
    if (![NSFileManager.defaultManager isExecutableFileAtPath:helper])
    {
        if (error != NULL)
        {
            *error = Xcode27HidError(58, [NSString stringWithFormat:
                @"The bundled Pencil helper is missing or not executable: %@. Reinstall the matching Ansight CLI build.", helper]);
        }
        return NO;
    }

    NSPipe *input = [NSPipe pipe];
    NSPipe *output = [NSPipe pipe];
    self.stylusInput = input.fileHandleForWriting;
    self.stylusOutput = output.fileHandleForReading;
    if (fcntl(self.stylusInput.fileDescriptor, F_SETNOSIGPIPE, 1) == -1)
    {
        if (error != NULL)
        {
            *error = Xcode27HidError(59, @"Could not configure the Pencil helper's input pipe.");
        }
        return NO;
    }

    NSMutableDictionary *environment = [NSProcessInfo.processInfo.environment mutableCopy];
    environment[@"DEVELOPER_DIR"] = self.developerDirectory;
    NSTask *task = [[NSTask alloc] init];
    task.executableURL = [NSURL fileURLWithPath:@"/usr/bin/xcrun"];
    task.arguments = @[@"simctl", @"spawn", self.deviceUdid, helper];
    task.environment = environment;
    task.standardInput = input;
    task.standardOutput = output;
    task.standardError = [NSFileHandle fileHandleWithNullDevice];
    self.stylusTask = task;
    if (![task launchAndReturnError:error])
    {
        return NO;
    }
    return [self readStylusReply:error];
}

- (BOOL)sendStylusSample:(NSDictionary *)sample error:(NSError **)error
{
    // The owner serializes all input to this device. Keep one guest service for
    // the session so individual Pencil samples do not spawn a process.
    if (self.stylusError != nil)
    {
        if (error != NULL)
        {
            *error = self.stylusError;
        }
        return NO;
    }

    NSError *failure = nil;
    BOOL ready = self.stylusTask != nil || [self startStylusHelper:&failure];
    if (ready)
    {
        NSMutableData *data = [[NSJSONSerialization dataWithJSONObject:sample options:0 error:&failure] mutableCopy];
        if (data != nil)
        {
            [data appendBytes:"\n" length:1];
            if ([self.stylusInput writeData:data error:&failure] && [self readStylusReply:&failure])
            {
                return YES;
            }
        }
    }

    // Closing the owned stdin lets the helper unregister its service. Never
    // repeat a sample whose receipt is uncertain, including after a timeout.
    [self.stylusInput closeAndReturnError:NULL];
    self.stylusInput = nil;
    self.stylusError = failure ?: Xcode27HidError(59, @"The Xcode 27 Pencil helper failed.");
    if (error != NULL)
    {
        *error = self.stylusError;
    }
    return NO;
}

- (void)dealloc
{
    [_stylusInput closeAndReturnError:NULL];
    [_stylusOutput closeAndReturnError:NULL];
    if (_connection != NULL)
    {
        xpc_connection_cancel(_connection);
    }
}

@end
