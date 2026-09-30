#import "../AnsightSimulatorHid/AnsightSimulatorHid.m"

static void Require(BOOL condition, NSString *message)
{
    if (!condition)
    {
        fprintf(stderr, "FAIL: %s\n", message.UTF8String);
        exit(1);
    }
}

static NSError *DisconnectedError(void)
{
    return [NSError errorWithDomain:@"SimulatorKit.HIDError" code:2 userInfo:@{
        NSLocalizedDescriptionKey: @"Mach port invalid, device disconnected",
    }];
}

@interface FakeHidClient : NSObject
@property(nonatomic, strong) NSError *deliveryError;
@property(nonatomic, assign) NSUInteger attempts;
@property(nonatomic, assign) NSUInteger delivered;
@property(nonatomic, assign) BOOL deferCompletion;
@property(nonatomic, copy) void (^pendingCompletion)(void);
@property(nonatomic, strong) NSData *lastMessage;
- (void)sendWithMessage:(IndigoHIDMessage)message
          freeWhenDone:(BOOL)freeWhenDone
       completionQueue:(dispatch_queue_t)queue
            completion:(void (^)(NSError *error))completion;
@end

@implementation FakeHidClient
- (void)sendWithMessage:(IndigoHIDMessage)message
          freeWhenDone:(BOOL)freeWhenDone
       completionQueue:(dispatch_queue_t)queue
            completion:(void (^)(NSError *error))completion
{
    self.attempts++;
    self.lastMessage = [NSData dataWithBytes:message length:malloc_size(message)];
    // Consume and overwrite the message, like SimulatorKit. Reusing the first
    // attempt's buffer instead of rebuilding it fails under AddressSanitizer.
    memset(message, 0xa5, malloc_size(message));
    NSError *deliveryError = self.deliveryError;
    void (^finish)(void) = ^{
        if (freeWhenDone) free(message);
        completion(deliveryError);
    };
    if (self.deferCompletion)
    {
        self.pendingCompletion = finish;
    }
    else
    {
        if (deliveryError == nil) self.delivered++;
        dispatch_async(queue, finish);
    }
}
@end

@interface FakeHidSession : AnsightSimulatorHidSession
@property(nonatomic, strong) NSArray<FakeHidClient *> *availableClients;
@property(nonatomic, assign) NSUInteger createdClients;
@property(nonatomic, assign) NSUInteger builtMessages;
@end

@implementation FakeHidSession
- (id)createClientForUdid:(NSString *)udid error:(NSError **)error
{
    (void)udid;
    if (self.createdClients >= self.availableClients.count)
    {
        if (error != NULL) *error = CreateError(5, @"Simulator is no longer available.");
        return nil;
    }
    return self.availableClients[self.createdClients++];
}

@end

static FakeHidSession *MakeSession(NSArray<FakeHidClient *> *clients)
{
    FakeHidSession *session = [FakeHidSession new];
    session.clientsByUdid = [NSMutableDictionary dictionary];
    session.availableClients = clients;
    return session;
}

static BOOL Send(FakeHidSession *session, NSError **error)
{
    return [session sendHidMessageToDevice:@"test-device" messageBuilder:^IndigoHIDMessage(NSError **buildError) {
        (void)buildError;
        session.builtMessages++;
        return calloc(1, 64);
    } error:error];
}

static void CachedClientReconnects(void)
{
    FakeHidClient *original = [FakeHidClient new];
    FakeHidClient *replacement = [FakeHidClient new];
    FakeHidSession *session = MakeSession(@[original, replacement]);
    NSError *error = nil;
    Require(Send(session, &error), @"Initial input should work.");
    original.deliveryError = DisconnectedError();
    Require(Send(session, &error), @"Input should recover after cached client disconnects.");
    Require(error == nil, @"Successful reconnect must clear errors.");
    Require(original.attempts == 2 && original.delivered == 1, @"Disconnected attempt must not be delivered.");
    Require(replacement.attempts == 1 && replacement.delivered == 1, @"Replacement must deliver exactly once.");
    Require(session.createdClients == 2 && session.builtMessages == 3, @"Retry must build a fresh message and client.");
    Require(Send(session, &error) && session.createdClients == 2, @"Replacement should stay cached.");
}

static void FailedReconnectIsBounded(void)
{
    FakeHidClient *original = [FakeHidClient new];
    FakeHidClient *replacement = [FakeHidClient new];
    original.deliveryError = DisconnectedError();
    replacement.deliveryError = DisconnectedError();
    FakeHidSession *session = MakeSession(@[original, replacement]);
    NSError *error = nil;
    Require(!Send(session, &error), @"Two disconnected clients must fail.");
    Require(original.attempts == 1 && replacement.attempts == 1, @"Reconnect must happen only once.");
    Require(session.clientsByUdid.count == 0, @"Failed replacement must be evicted too.");
    Require(error.userInfo[NSUnderlyingErrorKey] == replacement.deliveryError, @"Underlying delivery error must survive.");
}

static void OtherErrorsAreNotRetried(void)
{
    FakeHidClient *client = [FakeHidClient new];
    client.deliveryError = [NSError errorWithDomain:@"SimulatorKit.HIDError" code:42 userInfo:@{
        NSLocalizedDescriptionKey: @"Mach return error 42",
    }];
    FakeHidSession *session = MakeSession(@[client, [FakeHidClient new]]);
    NSError *error = nil;
    Require(!Send(session, &error), @"Other errors must fail.");
    Require(client.attempts == 1 && session.createdClients == 1, @"Other errors must not replay input.");
    Require(error.userInfo[NSUnderlyingErrorKey] == client.deliveryError, @"Preserve native error details.");
}

static void TimeoutIsNotRetried(void)
{
    FakeHidClient *client = [FakeHidClient new];
    client.deferCompletion = YES;
    FakeHidSession *session = MakeSession(@[client, [FakeHidClient new]]);
    NSError *error = nil;
    Require(!Send(session, &error), @"Missing completion must time out.");
    Require(error.code == 19 && client.attempts == 1 && session.createdClients == 1,
        @"Timeout has uncertain delivery and must not be retried.");
    client.pendingCompletion();
    client.pendingCompletion = nil;
}

static void ReconnectFailurePreservesError(void)
{
    FakeHidClient *client = [FakeHidClient new];
    client.deliveryError = DisconnectedError();
    FakeHidSession *session = MakeSession(@[client]);
    NSError *error = nil;
    Require(!Send(session, &error), @"Unavailable simulator must stop recovery.");
    Require(error.code == 5 && session.builtMessages == 1, @"Return reconnect failure without creating another message.");
}

static IndigoHIDMessage KeyboardMessage(uint32_t usage, uint32_t operation)
{
    (void)usage;
    (void)operation;
    return calloc(1, 64);
}

static IndigoHIDMessage ButtonMessage(uint32_t code, uint32_t operation, IndigoHIDTarget target)
{
    (void)target;
    return KeyboardMessage(code, operation);
}

static NSUInteger pointerMessageCount;
static IndigoHIDMessage PointerMessage(
    CGPoint *point, CGPoint *secondary, IndigoHIDTarget target, NSUInteger eventType,
    NSInteger edge, double x, double y, double width, double height)
{
    (void)point;
    (void)secondary;
    (void)target;
    (void)eventType;
    (void)edge;
    (void)x;
    (void)y;
    (void)width;
    (void)height;
    pointerMessageCount++;
    return calloc(1, 64);
}

static NSUInteger digitizerParentCount;
static NSUInteger digitizerFingerCount;
static NSUInteger digitizerMessageCount;

static CFTypeRef DigitizerParent(
    __unused CFAllocatorRef allocator, __unused uint64_t timestamp,
    __unused uint32_t transducerType, __unused uint32_t index, uint32_t identifier,
    uint32_t eventMask, __unused uint32_t buttonMask, double x, double y,
    __unused double z, __unused double tipPressure, __unused double barrelPressure,
    Boolean range, Boolean touch, __unused uint32_t options)
{
    Require(identifier == 17 && eventMask == 0x07 && x == 0.4 && y == 0.5 && range && touch,
        @"Retry must rebuild the original digitizer contact.");
    digitizerParentCount++;
    return CFRetain(CFSTR("parent"));
}

static CFTypeRef DigitizerFinger(
    __unused CFAllocatorRef allocator, __unused uint64_t timestamp,
    __unused uint32_t index, uint32_t identifier, uint32_t eventMask, double x, double y,
    __unused double z, __unused double tipPressure, __unused double twist,
    Boolean range, Boolean touch, __unused uint32_t options)
{
    Require(identifier == 17 && eventMask == 0x07 && x == 0.4 && y == 0.5 && range && touch,
        @"Retry must preserve the finger identifier, phase, and coordinates.");
    digitizerFingerCount++;
    return CFRetain(CFSTR("finger"));
}

static void AppendDigitizerFinger(CFTypeRef parent, CFTypeRef child, __unused uint32_t options)
{
    Require(CFEqual(parent, CFSTR("parent")) && CFEqual(child, CFSTR("finger")),
        @"Digitizer message must include both native events.");
}

static IndigoHIDMessage TrackpadMessage(CFTypeRef event)
{
    Require(CFEqual(event, CFSTR("parent")), @"Wrap the parent digitizer event.");
    digitizerMessageCount++;
    return calloc(1, 0x120);
}

static void DigitizerInputIsRebuilt(void)
{
    FakeHidClient *original = [FakeHidClient new];
    FakeHidClient *replacement = [FakeHidClient new];
    original.deliveryError = DisconnectedError();
    FakeHidSession *session = MakeSession(@[original, replacement]);
    session.createDigitizerEvent = DigitizerParent;
    session.createDigitizerFingerEvent = DigitizerFinger;
    session.appendHidEvent = AppendDigitizerFinger;
    session.messageForTrackpadEvent = TrackpadMessage;
    NSError *error = nil;
    Require([session sendPointerToDevice:@"test-device" phase:AnsightSimulatorPointerPhaseDown
        normalizedX:0.4 normalizedY:0.5 hasSecondaryContact:NO
        secondaryNormalizedX:0 secondaryNormalizedY:0 pointerId:17 timestampMilliseconds:0 error:&error],
        @"Single-contact digitizer input must recover.");
    Require(error == nil && digitizerParentCount == 2 && digitizerFingerCount == 2
        && digitizerMessageCount == 2 && session.createdClients == 2,
        @"Reconnection must rebuild the complete digitizer message exactly once.");
    Require(original.delivered == 0 && replacement.delivered == 1,
        @"Digitizer recovery must deliver exactly once.");
    const uint8_t *bytes = replacement.lastMessage.bytes;
    Require(*((const uint32_t *)(bytes + 0x6c)) == AnsightMainScreenTouchTarget
        && *((const uint32_t *)(bytes + 0x10c)) == AnsightMainScreenTouchTarget,
        @"Rebuilt digitizer messages must retain the main-screen targets.");
}

static void EachInputUsesRecovery(void)
{
    for (NSUInteger input = 0; input < 4; input++)
    {
        FakeHidClient *original = [FakeHidClient new];
        FakeHidClient *replacement = [FakeHidClient new];
        original.deliveryError = DisconnectedError();
        FakeHidSession *session = MakeSession(@[original, replacement]);
        session.messageForKeyboard = KeyboardMessage;
        session.messageForModifier = KeyboardMessage;
        session.messageForButton = ButtonMessage;
        session.messageForMouseEvent = PointerMessage;
        NSError *error = nil;
        BOOL sent;
        switch (input)
        {
            case 0:
                sent = [session sendPointerToDevice:@"test-device"
                                              phase:AnsightSimulatorPointerPhaseDown
                                        normalizedX:0.4 normalizedY:0.5
                                hasSecondaryContact:NO secondaryNormalizedX:0 secondaryNormalizedY:0
                                          pointerId:1 timestampMilliseconds:0 error:&error];
                Require(pointerMessageCount == 2, @"Pointer must regenerate its message after reconnect.");
                break;
            case 1:
                sent = [session sendButtonToDevice:@"test-device" button:AnsightSimulatorButtonHome
                                             phase:AnsightSimulatorButtonPhaseDown error:&error];
                break;
            default:
                sent = [session sendKeyToDevice:@"test-device" usageCode:input == 2 ? 4 : 225
                                         phase:AnsightSimulatorKeyPhaseDown error:&error];
                break;
        }
        Require(sent && error == nil && session.createdClients == 2,
            [NSString stringWithFormat:@"Input type %lu must use reconnect recovery.", (unsigned long)input]);
        Require(original.delivered == 0 && replacement.delivered == 1, @"Recovered input must be delivered exactly once.");
    }
}

int main(void)
{
    @autoreleasepool
    {
        CachedClientReconnects();
        FailedReconnectIsBounded();
        OtherErrorsAreNotRetried();
        TimeoutIsNotRetried();
        ReconnectFailurePreservesError();
        DigitizerInputIsRebuilt();
        EachInputUsesRecovery();
        puts("PASS: Simulator HID recovery (10 scenarios, no live simulator required).");
    }
    return 0;
}
