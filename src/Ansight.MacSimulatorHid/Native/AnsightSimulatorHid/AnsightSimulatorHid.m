#import "AnsightSimulatorHid.h"

#import <Foundation/Foundation.h>
#import <CoreFoundation/CoreFoundation.h>
#import <CoreGraphics/CoreGraphics.h>
#import <dlfcn.h>
#import <mach/mach_time.h>
#import <math.h>
#import <malloc/malloc.h>
#import <objc/message.h>
#import <objc/runtime.h>

typedef void *IndigoHIDMessage;
typedef uint32_t IndigoHIDTarget;
typedef IndigoHIDMessage (^AnsightHidMessageBuilder)(NSError **error);
typedef IndigoHIDMessage (*IndigoHIDMessageForMouseNSEventFunction)(
    CGPoint *location,
    CGPoint *secondaryLocation,
    IndigoHIDTarget target,
    NSUInteger eventType,
    NSInteger edge,
    double reservedX,
    double reservedY,
    double normalizedWidth,
    double normalizedHeight);
typedef CFTypeRef (*IOHIDEventCreateDigitizerEventFunction)(
    CFAllocatorRef allocator,
    uint64_t timestamp,
    uint32_t transducerType,
    uint32_t index,
    uint32_t identifier,
    uint32_t eventMask,
    uint32_t buttonMask,
    double x,
    double y,
    double z,
    double tipPressure,
    double barrelPressure,
    Boolean range,
    Boolean touch,
    uint32_t options);
typedef CFTypeRef (*IOHIDEventCreateDigitizerFingerEventFunction)(
    CFAllocatorRef allocator,
    uint64_t timestamp,
    uint32_t index,
    uint32_t identifier,
    uint32_t eventMask,
    double x,
    double y,
    double z,
    double tipPressure,
    double twist,
    Boolean range,
    Boolean touch,
    uint32_t options);
typedef void (*IOHIDEventAppendEventFunction)(
    CFTypeRef parent,
    CFTypeRef child,
    uint32_t options);
typedef IndigoHIDMessage (*IndigoHIDMessageForTrackpadEventFunction)(CFTypeRef event);
typedef IndigoHIDMessage (*IndigoHIDMessageForButtonFunction)(
    uint32_t keyCode,
    uint32_t operation,
    IndigoHIDTarget target);
typedef IndigoHIDMessage (*IndigoHIDMessageForKeyboardArbitraryFunction)(
    uint32_t usageCode,
    uint32_t operation);
typedef IndigoHIDMessage (*IndigoHIDMessageForModifierKeyBitFunction)(
    uint32_t modifierBit,
    uint32_t isPressed);

// CoreSimulator accessibility bridge. This resident implementation is adapted
// from Wand's MIT-licensed AXPTranslator bridge and keeps the translator plus
// SimDevice connection warm inside the Ansight host process.
@protocol AnsightSimulatorAccessibilityDevice <NSObject>
- (void)sendAccessibilityRequestAsync:(id)request
                       completionQueue:(dispatch_queue_t)queue
                     completionHandler:(void (^)(id response))handler;
@end

@protocol AnsightSimulatorAccessibilityTranslatorClass <NSObject>
- (id)sharedInstance;
@end

@protocol AnsightSimulatorAccessibilityTranslator <NSObject>
- (void)setBridgeTokenDelegate:(id)delegate;
- (id)frontmostApplicationWithDisplayId:(unsigned int)displayId
                    bridgeDelegateToken:(NSString *)token;
- (id)macPlatformElementFromTranslation:(id)translation;
@end

@protocol AnsightSimulatorAccessibilityResponseClass <NSObject>
- (id)emptyResponse;
@end

@protocol AnsightSimulatorAccessibilityTranslation <NSObject>
- (void)setBridgeDelegateToken:(NSString *)token;
@end

@protocol AnsightSimulatorAccessibilityElement <NSObject>
- (id)translation;
- (CGRect)accessibilityFrame;
- (NSString *)accessibilityRole;
- (NSString *)accessibilityLabel;
- (NSString *)accessibilityIdentifier;
- (id)accessibilityValue;
- (NSArray *)accessibilityChildren;
- (BOOL)isAccessibilityEnabled;
@end

@interface AnsightSimulatorAccessibilityDispatcher : NSObject

+ (instancetype)shared;
- (id)translator;
- (void)registerToken:(NSString *)token device:(id)device;
- (void)unregisterToken:(NSString *)token;

@end

// SimulatorHID registers the built-in main-screen digitizer under this Indigo
// target. Per-screen targets are reserved for dynamically-created displays.
static const IndigoHIDTarget AnsightMainScreenTouchTarget = 0x32;
static const IndigoHIDTarget AnsightMainScreenButtonTarget = 0x33;

@interface AnsightSimulatorHidSession : NSObject

@property(nonatomic, strong) id serviceContext;
@property(nonatomic, strong) id deviceSet;
@property(nonatomic, strong) NSMutableDictionary<NSString *, id> *clientsByUdid;
@property(nonatomic, assign) IndigoHIDMessageForMouseNSEventFunction messageForMouseEvent;
@property(nonatomic, assign) IOHIDEventCreateDigitizerEventFunction createDigitizerEvent;
@property(nonatomic, assign) IOHIDEventCreateDigitizerFingerEventFunction createDigitizerFingerEvent;
@property(nonatomic, assign) IOHIDEventAppendEventFunction appendHidEvent;
@property(nonatomic, assign) IndigoHIDMessageForTrackpadEventFunction messageForTrackpadEvent;
@property(nonatomic, assign) IndigoHIDMessageForButtonFunction messageForButton;
@property(nonatomic, assign) IndigoHIDMessageForKeyboardArbitraryFunction messageForKeyboard;
@property(nonatomic, assign) IndigoHIDMessageForModifierKeyBitFunction messageForModifier;
@property(nonatomic, strong) dispatch_queue_t accessibilityQueue;

- (instancetype)initWithDeveloperDirectory:(NSString *)developerDirectory error:(NSError **)error;
- (BOOL)sendPointerToDevice:(NSString *)deviceUdid
                      phase:(AnsightSimulatorPointerPhase)phase
                normalizedX:(double)normalizedX
                normalizedY:(double)normalizedY
        hasSecondaryContact:(BOOL)hasSecondaryContact
       secondaryNormalizedX:(double)secondaryNormalizedX
       secondaryNormalizedY:(double)secondaryNormalizedY
                  pointerId:(int64_t)pointerId
       timestampMilliseconds:(int64_t)timestampMilliseconds
                      error:(NSError **)error;
- (BOOL)getMainScreenMetricsForDevice:(NSString *)deviceUdid
                           pixelWidth:(double *)pixelWidth
                          pixelHeight:(double *)pixelHeight
                                scale:(double *)scale
                                error:(NSError **)error;
- (NSString *)accessibilityTreeJsonForDevice:(NSString *)deviceUdid
                                       error:(NSError **)error;
- (void)serializeAccessibilityElement:(id)element
                                token:(NSString *)token
                                depth:(NSUInteger)depth
                              visited:(NSUInteger *)visited
                                 into:(NSMutableArray<NSDictionary *> *)elements;
- (BOOL)sendButtonToDevice:(NSString *)deviceUdid
                     button:(AnsightSimulatorButton)button
                      phase:(AnsightSimulatorButtonPhase)phase
                      error:(NSError **)error;
- (BOOL)sendShakeToDevice:(NSString *)deviceUdid error:(NSError **)error;
- (BOOL)sendKeyToDevice:(NSString *)deviceUdid
               usageCode:(uint32_t)usageCode
                    phase:(AnsightSimulatorKeyPhase)phase
                    error:(NSError **)error;
- (BOOL)sendHidMessageToDevice:(NSString *)udid
                 messageBuilder:(AnsightHidMessageBuilder)messageBuilder
                          error:(NSError **)error;
- (id)clientForUdid:(NSString *)udid error:(NSError **)error;
- (id)createClientForUdid:(NSString *)udid error:(NSError **)error;
- (id)deviceForUdid:(NSString *)udid error:(NSError **)error;
- (IndigoHIDMessage)digitizerMessageForPoint:(CGPoint)point
                                       phase:(AnsightSimulatorPointerPhase)phase
                                   pointerId:(int64_t)pointerId
                                       error:(NSError **)error;

@end

static id SendId(id receiver, SEL selector)
{
    return ((id (*)(id, SEL))objc_msgSend)(receiver, selector);
}

static BOOL SendBool(id receiver, SEL selector)
{
    return ((BOOL (*)(id, SEL))objc_msgSend)(receiver, selector);
}

static id SendIdWithObject(id receiver, SEL selector, id value)
{
    return ((id (*)(id, SEL, id))objc_msgSend)(receiver, selector, value);
}

static void AddAccessibilityElements(
    id value,
    NSHashTable *seen,
    NSMutableArray *relatedElements)
{
    if (![value isKindOfClass:NSArray.class])
    {
        return;
    }

    for (id relatedElement in (NSArray *)value)
    {
        if (relatedElement != nil && ![seen containsObject:relatedElement])
        {
            [seen addObject:relatedElement];
            [relatedElements addObject:relatedElement];
        }
    }
}

static BOOL IsAccessibilityRelationshipAttribute(NSString *attributeName)
{
    if (![attributeName isKindOfClass:NSString.class])
    {
        return NO;
    }

    static NSArray<NSString *> *relationshipTokens;
    static dispatch_once_t relationshipTokensOnce;
    dispatch_once(&relationshipTokensOnce, ^{
        relationshipTokens = @[
            @"Children",
            @"Contents",
            @"Tabs",
            @"Rows",
            @"Columns"
        ];
    });
    for (NSString *token in relationshipTokens)
    {
        if ([attributeName rangeOfString:token options:NSCaseInsensitiveSearch].location != NSNotFound)
        {
            return YES;
        }
    }
    return NO;
}

static NSString *AccessibilityDiagnostics(id element)
{
    NSString *diagnosticsEnabled = NSProcessInfo.processInfo.environment[@"ANSIGHT_AX_DIAGNOSTICS"];
    if (diagnosticsEnabled.length == 0 || [diagnosticsEnabled isEqualToString:@"0"])
    {
        return nil;
    }

    SEL attributeNamesSelector = NSSelectorFromString(@"accessibilityAttributeNames");
    SEL attributeValueSelector = NSSelectorFromString(@"accessibilityAttributeValue:");
    if (![element respondsToSelector:attributeNamesSelector]
        || ![element respondsToSelector:attributeValueSelector])
    {
        return @"attribute-api-unavailable";
    }

    @try
    {
        id namesValue = SendId(element, attributeNamesSelector);
        if (![namesValue isKindOfClass:NSArray.class])
        {
            return [NSString stringWithFormat:@"attribute-names=%@", NSStringFromClass([namesValue class])];
        }

        NSMutableArray<NSString *> *descriptions = [NSMutableArray array];
        for (NSString *attributeName in (NSArray *)namesValue)
        {
            if (![attributeName isKindOfClass:NSString.class])
            {
                continue;
            }

            id attributeValue = nil;
            @try
            {
                attributeValue = SendIdWithObject(element, attributeValueSelector, attributeName);
            }
            @catch (__unused NSException *exception)
            {
                [descriptions addObject:[NSString stringWithFormat:@"%@=exception", attributeName]];
                continue;
            }

            if ([attributeValue isKindOfClass:NSArray.class])
            {
                NSArray *values = attributeValue;
                NSString *itemType = values.count == 0
                    ? @"empty"
                    : NSStringFromClass([values.firstObject class]);
                [descriptions addObject:[NSString stringWithFormat:
                    @"%@=%@[%lu](%@)",
                    attributeName,
                    NSStringFromClass([attributeValue class]),
                    (unsigned long)values.count,
                    itemType]];
            }
            else
            {
                [descriptions addObject:[NSString stringWithFormat:
                    @"%@=%@",
                    attributeName,
                    attributeValue == nil ? @"nil" : NSStringFromClass([attributeValue class])]];
            }
        }
        return [descriptions componentsJoinedByString:@" | "];
    }
    @catch (NSException *exception)
    {
        return [NSString stringWithFormat:@"diagnostic-exception=%@", exception.name];
    }
}

static NSArray *AccessibilityRelatedElements(id element)
{
    static NSArray<NSString *> *relationSelectors;
    static dispatch_once_t relationSelectorsOnce;
    dispatch_once(&relationSelectorsOnce, ^{
        relationSelectors = @[
            @"accessibilityChildren",
            @"accessibilityVisibleChildren",
            @"accessibilityContents",
            @"accessibilityTabs",
            @"accessibilityRows",
            @"accessibilityColumns"
        ];
    });

    NSMutableArray *relatedElements = [NSMutableArray array];
    NSHashTable *seen = [NSHashTable hashTableWithOptions:NSHashTableObjectPointerPersonality];
    for (NSString *selectorName in relationSelectors)
    {
        SEL selector = NSSelectorFromString(selectorName);
        if (![element respondsToSelector:selector])
        {
            continue;
        }

        @try
        {
            AddAccessibilityElements(SendId(element, selector), seen, relatedElements);
        }
        @catch (__unused NSException *exception)
        {
            // Some translated accessibility relations are unsupported for a role.
        }
    }

    // AXPMacPlatformElement exposes role-specific relationships through the
    // generic AppKit accessibility attribute API. In particular, UITabBar
    // publishes its tab buttons through AXTabs rather than AXChildren.
    SEL attributeValueSelector = NSSelectorFromString(@"accessibilityAttributeValue:");
    if ([element respondsToSelector:attributeValueSelector])
    {
        NSMutableOrderedSet<NSString *> *relationAttributes = [NSMutableOrderedSet orderedSetWithArray:@[
            @"AXChildren",
            @"AXVisibleChildren",
            @"AXContents",
            @"AXTabs",
            @"AXRows",
            @"AXColumns"
        ]];
        SEL attributeNamesSelector = NSSelectorFromString(@"accessibilityAttributeNames");
        if ([element respondsToSelector:attributeNamesSelector])
        {
            @try
            {
                id availableAttributes = SendId(element, attributeNamesSelector);
                if ([availableAttributes isKindOfClass:NSArray.class])
                {
                    for (NSString *attributeName in (NSArray *)availableAttributes)
                    {
                        if (IsAccessibilityRelationshipAttribute(attributeName))
                        {
                            [relationAttributes addObject:attributeName];
                        }
                    }
                }
            }
            @catch (__unused NSException *exception)
            {
                // Fall back to the standard relationship attributes above.
            }
        }

        for (NSString *attributeName in relationAttributes.array)
        {
            @try
            {
                AddAccessibilityElements(
                    SendIdWithObject(element, attributeValueSelector, attributeName),
                    seen,
                    relatedElements);
            }
            @catch (__unused NSException *exception)
            {
                // Attribute availability varies by translated role and OS.
            }
        }
    }

    return relatedElements;
}

static id SendIdWithObjectAndError(id receiver, SEL selector, id value, NSError **error)
{
    return ((id (*)(id, SEL, id, NSError **))objc_msgSend)(receiver, selector, value, error);
}

static id SendIdWithError(id receiver, SEL selector, NSError **error)
{
    return ((id (*)(id, SEL, NSError **))objc_msgSend)(receiver, selector, error);
}

static void CopyError(NSString *message, char *errorBuffer, size_t errorBufferCapacity)
{
    if (errorBuffer == NULL || errorBufferCapacity == 0)
    {
        return;
    }

    const char *utf8 = (message ?: @"Unknown Simulator HID error.").UTF8String;
    snprintf(errorBuffer, errorBufferCapacity, "%s", utf8 == NULL ? "Unknown Simulator HID error." : utf8);
}

static NSError *CreateError(NSInteger code, NSString *message)
{
    return [NSError errorWithDomain:@"AnsightSimulatorHid"
                               code:code
                           userInfo:@{NSLocalizedDescriptionKey: message}];
}

@interface AnsightSimulatorAccessibilityDispatcher ()

@property(nonatomic, strong) NSMutableDictionary<NSString *, id> *devicesByToken;
@property(nonatomic, strong) dispatch_queue_t callbackQueue;
@property(nonatomic, strong) id translatorInstance;

@end

@implementation AnsightSimulatorAccessibilityDispatcher

+ (instancetype)shared
{
    static AnsightSimulatorAccessibilityDispatcher *shared;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        shared = [[AnsightSimulatorAccessibilityDispatcher alloc] init];
    });
    return shared;
}

- (instancetype)init
{
    self = [super init];
    if (self != nil)
    {
        self.devicesByToken = [NSMutableDictionary dictionary];
        self.callbackQueue = dispatch_queue_create(
            "ai.ansight.simulator-accessibility-callback",
            DISPATCH_QUEUE_SERIAL);
    }
    return self;
}

- (id)translator
{
    @synchronized (self)
    {
        if (self.translatorInstance == nil)
        {
            Class translatorClass = NSClassFromString(@"AXPTranslator");
            if (translatorClass != Nil)
            {
                self.translatorInstance = [(id<AnsightSimulatorAccessibilityTranslatorClass>)translatorClass
                    sharedInstance];
                [(id<AnsightSimulatorAccessibilityTranslator>)self.translatorInstance
                    setBridgeTokenDelegate:self];
            }
        }
        return self.translatorInstance;
    }
}

- (void)registerToken:(NSString *)token device:(id)device
{
    @synchronized (self.devicesByToken)
    {
        self.devicesByToken[token] = device;
    }
}

- (void)unregisterToken:(NSString *)token
{
    @synchronized (self.devicesByToken)
    {
        [self.devicesByToken removeObjectForKey:token];
    }
}

- (id (^)(id))accessibilityTranslationDelegateBridgeCallbackWithToken:(NSString *)token
{
    id device = nil;
    @synchronized (self.devicesByToken)
    {
        device = self.devicesByToken[token];
    }
    dispatch_queue_t callbackQueue = self.callbackQueue;
    if (device == nil)
    {
        return ^id(id request) {
            (void)request;
            Class responseClass = NSClassFromString(@"AXPTranslatorResponse");
            return responseClass == Nil
                ? nil
                : [(id<AnsightSimulatorAccessibilityResponseClass>)responseClass emptyResponse];
        };
    }

    return ^id(id request) {
        dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
        __block id response = nil;
        [(id<AnsightSimulatorAccessibilityDevice>)device
            sendAccessibilityRequestAsync:request
                          completionQueue:callbackQueue
                        completionHandler:^(id innerResponse) {
                            response = innerResponse;
                            dispatch_semaphore_signal(semaphore);
                        }];
        dispatch_semaphore_wait(
            semaphore,
            dispatch_time(DISPATCH_TIME_NOW, 10 * NSEC_PER_SEC));
        if (response == nil)
        {
            Class responseClass = NSClassFromString(@"AXPTranslatorResponse");
            response = responseClass == Nil
                ? nil
                : [(id<AnsightSimulatorAccessibilityResponseClass>)responseClass emptyResponse];
        }
        return response;
    };
}

- (CGRect)accessibilityTranslationConvertPlatformFrameToSystem:(CGRect)frame
                                                     withToken:(NSString *)token
{
    (void)token;
    return frame;
}

- (id)accessibilityTranslationRootParentWithToken:(NSString *)token
{
    (void)token;
    return nil;
}

@end

static Class ResolveLegacyHidClientClass(void)
{
    Class clientClass = NSClassFromString(@"SimulatorKit.SimDeviceLegacyHIDClient");
    if (clientClass == Nil)
    {
        clientClass = NSClassFromString(@"_TtC12SimulatorKit24SimDeviceLegacyHIDClient");
    }
    return clientClass;
}

static BOOL CheckSimulatorHidCompatibility(NSString *developerDirectory, NSError **error)
{
    NSString *coreSimulatorPath = @"/Library/Developer/PrivateFrameworks/CoreSimulator.framework/CoreSimulator";
    NSString *simulatorKitPath = [developerDirectory stringByAppendingPathComponent:
        @"Library/PrivateFrameworks/SimulatorKit.framework/SimulatorKit"];
    void *coreSimulator = dlopen(coreSimulatorPath.fileSystemRepresentation, RTLD_NOW | RTLD_LOCAL);
    if (coreSimulator == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(
                1,
                [NSString stringWithFormat:@"Could not load CoreSimulator: %s", dlerror()]);
        }
        return NO;
    }

    void *simulatorKit = dlopen(simulatorKitPath.fileSystemRepresentation, RTLD_NOW | RTLD_LOCAL);
    if (simulatorKit == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(
                2,
                [NSString stringWithFormat:@"Could not load SimulatorKit: %s", dlerror()]);
        }
        return NO;
    }

    if (dlsym(simulatorKit, "IndigoHIDMessageForMouseNSEvent") == NULL
        || dlsym(simulatorKit, "IndigoHIDMessageForButton") == NULL
        || dlsym(simulatorKit, "IndigoHIDMessageForKeyboardArbitrary") == NULL
        || dlsym(simulatorKit, "IndigoHIDMessageForModifierKeyBit") == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(
                3,
                @"SimulatorKit does not expose the required HID message functions.");
        }
        return NO;
    }

    Class serviceContextClass = NSClassFromString(@"SimServiceContext");
    if (serviceContextClass == Nil
        || ![serviceContextClass respondsToSelector:
            sel_registerName("sharedServiceContextForDeveloperDir:error:")])
    {
        if (error != NULL)
        {
            *error = CreateError(4, @"CoreSimulator service context is unavailable.");
        }
        return NO;
    }

    Class clientClass = ResolveLegacyHidClientClass();
    if (clientClass == Nil)
    {
        if (error != NULL)
        {
            *error = CreateError(6, @"SimDeviceLegacyHIDClient is unavailable.");
        }
        return NO;
    }
    if (class_getInstanceMethod(clientClass, sel_registerName("initWithDevice:error:")) == NULL
        || class_getInstanceMethod(
            clientClass,
            sel_registerName("sendWithMessage:freeWhenDone:completionQueue:completion:")) == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(7, @"SimDeviceLegacyHIDClient has no supported initializer or send selector.");
        }
        return NO;
    }

    return YES;
}

static BOOL SendHidMessage(id receiver, IndigoHIDMessage message, NSError **error)
{
    SEL selector = sel_registerName("sendWithMessage:freeWhenDone:completionQueue:completion:");
    if (![receiver respondsToSelector:selector])
    {
        free(message);
        if (error != NULL)
        {
            *error = CreateError(7, @"SimDeviceLegacyHIDClient has no supported send selector.");
        }
        return NO;
    }

    dispatch_semaphore_t completionSemaphore = dispatch_semaphore_create(0);
    dispatch_queue_t completionQueue = dispatch_get_global_queue(QOS_CLASS_USER_INTERACTIVE, 0);
    __block NSError *completionError = nil;
    void (^completion)(NSError *) = ^(NSError *sendError) {
        completionError = sendError;
        dispatch_semaphore_signal(completionSemaphore);
    };
    ((void (*)(id, SEL, IndigoHIDMessage, BOOL, id, id))objc_msgSend)(
        receiver,
        selector,
        message,
        YES,
        completionQueue,
        completion);
    if (dispatch_semaphore_wait(
            completionSemaphore,
            dispatch_time(DISPATCH_TIME_NOW, 2 * NSEC_PER_SEC)) != 0)
    {
        if (error != NULL)
        {
            *error = CreateError(19, @"Timed out while delivering Simulator HID input.");
        }
        return NO;
    }
    if (completionError != nil)
    {
        if (error != NULL)
        {
            *error = [NSError errorWithDomain:@"AnsightSimulatorHid"
                                         code:20
                                     userInfo:@{
                NSLocalizedDescriptionKey: [NSString stringWithFormat:
                    @"Simulator HID delivery failed: %@", completionError.localizedDescription],
                NSUnderlyingErrorKey: completionError,
            }];
        }
        return NO;
    }
    return YES;
}

@implementation AnsightSimulatorHidSession

- (instancetype)initWithDeveloperDirectory:(NSString *)developerDirectory error:(NSError **)error
{
    self = [super init];
    if (self == nil)
    {
        return nil;
    }

    self.clientsByUdid = [NSMutableDictionary dictionary];
    self.accessibilityQueue = dispatch_queue_create(
        "ai.ansight.simulator-accessibility",
        DISPATCH_QUEUE_SERIAL);

    NSString *coreSimulatorPath = @"/Library/Developer/PrivateFrameworks/CoreSimulator.framework/CoreSimulator";
    NSString *simulatorKitPath = [developerDirectory stringByAppendingPathComponent:
        @"Library/PrivateFrameworks/SimulatorKit.framework/SimulatorKit"];

    void *coreSimulator = dlopen(coreSimulatorPath.fileSystemRepresentation, RTLD_NOW | RTLD_LOCAL);
    if (coreSimulator == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(
                1,
                [NSString stringWithFormat:@"Could not load CoreSimulator: %s", dlerror()]);
        }
        return nil;
    }

    void *simulatorKit = dlopen(simulatorKitPath.fileSystemRepresentation, RTLD_NOW | RTLD_LOCAL);
    if (simulatorKit == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(
                2,
                [NSString stringWithFormat:@"Could not load SimulatorKit: %s", dlerror()]);
        }
        return nil;
    }

    self.messageForMouseEvent = (IndigoHIDMessageForMouseNSEventFunction)dlsym(
        simulatorKit,
        "IndigoHIDMessageForMouseNSEvent");
    self.createDigitizerEvent = (IOHIDEventCreateDigitizerEventFunction)dlsym(
        RTLD_DEFAULT,
        "IOHIDEventCreateDigitizerEvent");
    self.createDigitizerFingerEvent = (IOHIDEventCreateDigitizerFingerEventFunction)dlsym(
        RTLD_DEFAULT,
        "IOHIDEventCreateDigitizerFingerEvent");
    self.appendHidEvent = (IOHIDEventAppendEventFunction)dlsym(
        RTLD_DEFAULT,
        "IOHIDEventAppendEvent");
    self.messageForTrackpadEvent = (IndigoHIDMessageForTrackpadEventFunction)dlsym(
        simulatorKit,
        "IndigoHIDMessageForTrackpadEventFromHIDEventRef");
    self.messageForButton = (IndigoHIDMessageForButtonFunction)dlsym(
        simulatorKit,
        "IndigoHIDMessageForButton");
    self.messageForKeyboard = (IndigoHIDMessageForKeyboardArbitraryFunction)dlsym(
        simulatorKit,
        "IndigoHIDMessageForKeyboardArbitrary");
    self.messageForModifier = (IndigoHIDMessageForModifierKeyBitFunction)dlsym(
        simulatorKit,
        "IndigoHIDMessageForModifierKeyBit");
    if (self.messageForMouseEvent == NULL
        || self.messageForButton == NULL
        || self.messageForKeyboard == NULL
        || self.messageForModifier == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(
                3,
                @"SimulatorKit does not expose the required HID message functions.");
        }
        return nil;
    }

    Class clientClass = ResolveLegacyHidClientClass();
    if (clientClass == Nil)
    {
        if (error != NULL)
        {
            *error = CreateError(6, @"SimDeviceLegacyHIDClient is unavailable.");
        }
        return nil;
    }
    if (class_getInstanceMethod(clientClass, sel_registerName("initWithDevice:error:")) == NULL
        || class_getInstanceMethod(
            clientClass,
            sel_registerName("sendWithMessage:freeWhenDone:completionQueue:completion:")) == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(7, @"SimDeviceLegacyHIDClient has no supported initializer or send selector.");
        }
        return nil;
    }

    Class serviceContextClass = NSClassFromString(@"SimServiceContext");
    SEL contextSelector = sel_registerName("sharedServiceContextForDeveloperDir:error:");
    if (serviceContextClass == Nil || ![serviceContextClass respondsToSelector:contextSelector])
    {
        if (error != NULL)
        {
            *error = CreateError(4, @"CoreSimulator service context is unavailable.");
        }
        return nil;
    }

    self.serviceContext = SendIdWithObjectAndError(
        serviceContextClass,
        contextSelector,
        developerDirectory,
        error);
    if (self.serviceContext == nil)
    {
        return nil;
    }

    self.deviceSet = SendIdWithError(
        self.serviceContext,
        sel_registerName("defaultDeviceSetWithError:"),
        error);
    return self.deviceSet == nil ? nil : self;
}

- (BOOL)sendKeyToDevice:(NSString *)deviceUdid
               usageCode:(uint32_t)usageCode
                    phase:(AnsightSimulatorKeyPhase)phase
                    error:(NSError **)error
{
    @synchronized (self)
    {
        if (usageCode < 4 || usageCode > 231)
        {
            if (error != NULL)
            {
                *error = CreateError(15, @"The USB HID keyboard usage code is out of range.");
            }
            return NO;
        }

        uint32_t operation = 0;
        switch (phase)
        {
            case AnsightSimulatorKeyPhaseDown:
                operation = 1;
                break;
            case AnsightSimulatorKeyPhaseUp:
                operation = 2;
                break;
            default:
                if (error != NULL)
                {
                    *error = CreateError(16, @"Unsupported keyboard phase.");
                }
                return NO;
        }

        uint32_t modifierBit = 0;
        switch (usageCode)
        {
            case 224:
            case 228:
                modifierBit = 18;
                break;
            case 225:
            case 229:
                modifierBit = 17;
                break;
            case 226:
            case 230:
                modifierBit = 19;
                break;
            case 227:
            case 231:
                modifierBit = 20;
                break;
            default:
                break;
        }

        AnsightHidMessageBuilder messageBuilder = ^IndigoHIDMessage(NSError **messageError) {
            IndigoHIDMessage message = modifierBit == 0
                ? self.messageForKeyboard(usageCode, operation)
                : self.messageForModifier(
                    modifierBit,
                    phase == AnsightSimulatorKeyPhaseDown ? 1 : 0);
            if (message == NULL)
            {
                if (messageError != NULL)
                {
                    *messageError = CreateError(17, @"SimulatorKit did not create a keyboard HID message.");
                }
                return NULL;
            }
            return message;
        };

        @try
        {
            return [self sendHidMessageToDevice:deviceUdid messageBuilder:messageBuilder error:error];
        }
        @catch (NSException *exception)
        {
            if (error != NULL)
            {
                *error = CreateError(
                    18,
                    [NSString stringWithFormat:@"%@: %@", exception.name, exception.reason ?: @"native exception"]);
            }
            return NO;
        }
    }
}

- (BOOL)sendPointerToDevice:(NSString *)deviceUdid
                      phase:(AnsightSimulatorPointerPhase)phase
                normalizedX:(double)normalizedX
                normalizedY:(double)normalizedY
        hasSecondaryContact:(BOOL)hasSecondaryContact
       secondaryNormalizedX:(double)secondaryNormalizedX
       secondaryNormalizedY:(double)secondaryNormalizedY
                  pointerId:(int64_t)pointerId
       timestampMilliseconds:(int64_t)timestampMilliseconds
                      error:(NSError **)error
{
    (void)timestampMilliseconds;
    @synchronized (self)
    {
        AnsightHidMessageBuilder messageBuilder = ^IndigoHIDMessage(NSError **messageError) {
            CGPoint point = CGPointMake(
                fmax(0.0, fmin(1.0, normalizedX)),
                fmax(0.0, fmin(1.0, normalizedY)));
            CGPoint secondaryPoint = CGPointMake(
                fmax(0.0, fmin(1.0, secondaryNormalizedX)),
                fmax(0.0, fmin(1.0, secondaryNormalizedY)));
            IndigoHIDMessage message = NULL;
            BOOL hasDigitizerBuilder = self.createDigitizerEvent != NULL
                && self.createDigitizerFingerEvent != NULL
                && self.appendHidEvent != NULL
                && self.messageForTrackpadEvent != NULL;
            if (!hasSecondaryContact && hasDigitizerBuilder)
            {
                message = [self digitizerMessageForPoint:point
                                                  phase:phase
                                              pointerId:pointerId
                                                  error:messageError];
            }
            else
            {
                NSUInteger eventType = 0;
                switch (phase)
                {
                    case AnsightSimulatorPointerPhaseDown:
                        eventType = 1;
                        break;
                    case AnsightSimulatorPointerPhaseMove:
                        eventType = 6;
                        break;
                    case AnsightSimulatorPointerPhaseUp:
                    case AnsightSimulatorPointerPhaseCancel:
                        eventType = 2;
                        break;
                    default:
                        if (messageError != NULL)
                        {
                            *messageError = CreateError(8, @"Unsupported pointer phase.");
                        }
                        return NULL;
                }

                message = self.messageForMouseEvent(
                    &point,
                    hasSecondaryContact ? &secondaryPoint : NULL,
                    AnsightMainScreenTouchTarget,
                    eventType,
                    0,
                    1.0,
                    1.0,
                    1.0,
                    1.0);
            }
            if (message == NULL)
            {
                if (messageError != NULL)
                {
                    if (*messageError == nil)
                    {
                        *messageError = CreateError(9, @"SimulatorKit did not create a HID message.");
                    }
                }
                return NULL;
            }
            return message;
        };

        BOOL sent = NO;
        @try
        {
            sent = [self sendHidMessageToDevice:deviceUdid messageBuilder:messageBuilder error:error];
        }
        @catch (NSException *exception)
        {
            if (error != NULL)
            {
                *error = CreateError(
                    10,
                    [NSString stringWithFormat:@"%@: %@", exception.name, exception.reason ?: @"native exception"]);
            }
            return NO;
        }

        if (!sent)
        {
            return NO;
        }

        return YES;
    }
}

- (IndigoHIDMessage)digitizerMessageForPoint:(CGPoint)point
                                       phase:(AnsightSimulatorPointerPhase)phase
                                   pointerId:(int64_t)pointerId
                                       error:(NSError **)error
{
    uint32_t eventMask = 0;
    Boolean inRange = false;
    Boolean touching = false;
    switch (phase)
    {
        case AnsightSimulatorPointerPhaseDown:
        case AnsightSimulatorPointerPhaseMove:
            eventMask = 0x07;
            inRange = true;
            touching = true;
            break;
        case AnsightSimulatorPointerPhaseUp:
        case AnsightSimulatorPointerPhaseCancel:
            eventMask = 0x06;
            break;
        default:
            if (error != NULL)
            {
                *error = CreateError(8, @"Unsupported pointer phase.");
            }
            return NULL;
    }

    uint32_t identifier = (uint32_t)(pointerId & UINT32_MAX);
    if (identifier == 0)
    {
        identifier = 1;
    }

    uint64_t timestamp = mach_absolute_time();
    CFTypeRef parent = self.createDigitizerEvent(
        NULL,
        timestamp,
        2,
        0,
        identifier,
        eventMask,
        0,
        point.x,
        point.y,
        0.0,
        0.0,
        0.0,
        inRange,
        touching,
        0);
    if (parent == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(21, @"IOKit did not create a digitizer parent event.");
        }
        return NULL;
    }

    CFTypeRef finger = self.createDigitizerFingerEvent(
        NULL,
        timestamp,
        0,
        identifier,
        eventMask,
        point.x,
        point.y,
        0.0,
        0.0,
        0.0,
        inRange,
        touching,
        0);
    if (finger == NULL)
    {
        CFRelease(parent);
        if (error != NULL)
        {
            *error = CreateError(22, @"IOKit did not create a digitizer finger event.");
        }
        return NULL;
    }

    self.appendHidEvent(parent, finger, 0);
    IndigoHIDMessage message = self.messageForTrackpadEvent(parent);
    CFRelease(finger);
    CFRelease(parent);
    if (message == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(23, @"SimulatorKit did not wrap the digitizer HID event.");
        }
        return NULL;
    }

    size_t messageSize = malloc_size(message);
    if (messageSize < 0x70)
    {
        free(message);
        if (error != NULL)
        {
            *error = CreateError(24, @"SimulatorKit returned an incomplete digitizer HID message.");
        }
        return NULL;
    }

    uint8_t *bytes = (uint8_t *)message;
    *((uint32_t *)(bytes + 0x6c)) = AnsightMainScreenTouchTarget;
    bytes[0x3a] = 0;
    bytes[0x3b] = 0;
    if (messageSize >= 0x110)
    {
        *((uint32_t *)(bytes + 0x10c)) = AnsightMainScreenTouchTarget;
    }
    if (messageSize >= 0xdc)
    {
        bytes[0xda] = 0;
        bytes[0xdb] = 0;
    }

    return message;
}

- (BOOL)sendButtonToDevice:(NSString *)deviceUdid
                     button:(AnsightSimulatorButton)button
                      phase:(AnsightSimulatorButtonPhase)phase
                      error:(NSError **)error
{
    @synchronized (self)
    {
        uint32_t keyCode = 0;
        switch (button)
        {
            case AnsightSimulatorButtonHome:
                keyCode = 0;
                break;
            case AnsightSimulatorButtonLock:
                keyCode = 1;
                break;
            case AnsightSimulatorButtonVolumeUp:
                keyCode = 2;
                break;
            case AnsightSimulatorButtonVolumeDown:
                keyCode = 3;
                break;
            default:
                if (error != NULL)
                {
                    *error = CreateError(11, @"Unsupported simulator button.");
                }
                return NO;
        }

        uint32_t operation = 0;
        switch (phase)
        {
            case AnsightSimulatorButtonPhaseDown:
                operation = 1;
                break;
            case AnsightSimulatorButtonPhaseUp:
                operation = 2;
                break;
            default:
                if (error != NULL)
                {
                    *error = CreateError(12, @"Unsupported simulator button phase.");
                }
                return NO;
        }

        AnsightHidMessageBuilder messageBuilder = ^IndigoHIDMessage(NSError **messageError) {
            IndigoHIDMessage message = self.messageForButton(
                keyCode,
                operation,
                AnsightMainScreenButtonTarget);
            if (message == NULL)
            {
                if (messageError != NULL)
                {
                    *messageError = CreateError(13, @"SimulatorKit did not create a button HID message.");
                }
                return NULL;
            }
            return message;
        };

        @try
        {
            return [self sendHidMessageToDevice:deviceUdid messageBuilder:messageBuilder error:error];
        }
        @catch (NSException *exception)
        {
            if (error != NULL)
            {
                *error = CreateError(
                    14,
                    [NSString stringWithFormat:@"%@: %@", exception.name, exception.reason ?: @"native exception"]);
            }
            return NO;
        }
    }
}

- (BOOL)sendHidMessageToDevice:(NSString *)udid
                 messageBuilder:(AnsightHidMessageBuilder)messageBuilder
                          error:(NSError **)error
{
    for (NSUInteger attempt = 0; attempt < 2; attempt++)
    {
        NSError *sendError = nil;
        id client = [self clientForUdid:udid error:&sendError];
        if (client == nil)
        {
            if (error != NULL) *error = sendError;
            return NO;
        }

        IndigoHIDMessage message = messageBuilder(&sendError);
        if (message == NULL)
        {
            if (error != NULL) *error = sendError;
            return NO;
        }
        if (SendHidMessage(client, message, &sendError))
        {
            if (error != NULL) *error = nil;
            return YES;
        }

        NSError *underlyingError = sendError.userInfo[NSUnderlyingErrorKey];
        // SimulatorKit reports MACH_SEND_INVALID_DEST using this private error.
        // The message was not delivered, so reconnecting and rebuilding it once
        // is safe. Timeouts and other errors have uncertain delivery and must
        // never replay input. A resident host can outlive the simulator's port.
        BOOL disconnected = [underlyingError.localizedDescription isEqualToString:
            @"Mach port invalid, device disconnected"];
        if (disconnected)
        {
            [self.clientsByUdid removeObjectForKey:udid];
        }
        if (!disconnected || attempt > 0)
        {
            if (error != NULL) *error = sendError;
            return NO;
        }
    }
    return NO;
}

- (id)clientForUdid:(NSString *)udid error:(NSError **)error
{
    id existing = self.clientsByUdid[udid];
    if (existing != nil)
    {
        return existing;
    }

    id client = [self createClientForUdid:udid error:error];
    if (client != nil)
    {
        self.clientsByUdid[udid] = client;
    }
    return client;
}

- (id)createClientForUdid:(NSString *)udid error:(NSError **)error
{
    id device = [self deviceForUdid:udid error:error];
    if (device == nil)
    {
        return nil;
    }

    Class clientClass = ResolveLegacyHidClientClass();
    if (clientClass == Nil)
    {
        if (error != NULL)
        {
            *error = CreateError(6, @"SimDeviceLegacyHIDClient is unavailable.");
        }
        return nil;
    }

    id allocated = SendId(clientClass, sel_registerName("alloc"));
    return SendIdWithObjectAndError(
        allocated,
        sel_registerName("initWithDevice:error:"),
        device,
        error);
}

- (id)deviceForUdid:(NSString *)udid error:(NSError **)error
{
    NSDictionary *devicesByUdid = SendId(self.deviceSet, sel_registerName("devicesByUDID"));
    id device = devicesByUdid[udid];
    if (device == nil)
    {
        NSUUID *uuid = [[NSUUID alloc] initWithUUIDString:udid];
        device = uuid == nil ? nil : devicesByUdid[uuid];
    }
    if (device == nil)
    {
        NSArray *devices = SendId(self.deviceSet, sel_registerName("devices"));
        for (id candidate in devices)
        {
            id candidateUdid = SendId(candidate, sel_registerName("UDID"));
            NSString *candidateUdidText = [candidateUdid respondsToSelector:sel_registerName("UUIDString")]
                ? SendId(candidateUdid, sel_registerName("UUIDString"))
                : [candidateUdid description];
            if ([candidateUdidText caseInsensitiveCompare:udid] == NSOrderedSame)
            {
                device = candidate;
                break;
            }
        }
    }
    if (device == nil)
    {
        if (error != NULL)
        {
            *error = CreateError(
                5,
                [NSString stringWithFormat:@"Simulator %@ was not found.", udid]);
        }
        return nil;
    }

    return device;
}

- (BOOL)sendShakeToDevice:(NSString *)deviceUdid error:(NSError **)error
{
    @synchronized (self)
    {
        id device = [self deviceForUdid:deviceUdid error:error];
        if (device == nil)
        {
            return NO;
        }

        SEL selector = sel_registerName("postDarwinNotification:error:");
        if (![device respondsToSelector:selector])
        {
            if (error != NULL)
            {
                *error = CreateError(25, @"CoreSimulator does not expose device Darwin notifications.");
            }
            return NO;
        }

        return ((BOOL (*)(id, SEL, id, NSError **))objc_msgSend)(
            device, selector, @"com.apple.UIKit.SimulatorShake", error);
    }
}

- (BOOL)getMainScreenMetricsForDevice:(NSString *)deviceUdid
                           pixelWidth:(double *)pixelWidth
                          pixelHeight:(double *)pixelHeight
                                scale:(double *)scale
                                error:(NSError **)error
{
    @synchronized (self)
    {
        id device = [self deviceForUdid:deviceUdid error:error];
        if (device == nil)
        {
            return NO;
        }

        @try
        {
            id deviceType = SendId(device, sel_registerName("deviceType"));
            if (deviceType == nil)
            {
                if (error != NULL)
                {
                    *error = CreateError(25, @"Simulator device type metadata is unavailable.");
                }
                return NO;
            }

            NSValue *screenSizeValue = [deviceType valueForKey:@"mainScreenSize"];
            NSNumber *screenScaleValue = [deviceType valueForKey:@"mainScreenScale"];
            CGSize screenSize = screenSizeValue.sizeValue;
            double screenScale = screenScaleValue.doubleValue;
            if (!isfinite(screenSize.width)
                || !isfinite(screenSize.height)
                || !isfinite(screenScale)
                || screenSize.width <= 0
                || screenSize.height <= 0
                || screenScale <= 0)
            {
                if (error != NULL)
                {
                    *error = CreateError(26, @"Simulator main-screen metrics are invalid.");
                }
                return NO;
            }

            *pixelWidth = screenSize.width;
            *pixelHeight = screenSize.height;
            *scale = screenScale;
            return YES;
        }
        @catch (NSException *exception)
        {
            if (error != NULL)
            {
                *error = CreateError(
                    27,
                    [NSString stringWithFormat:@"Could not read Simulator main-screen metrics: %@: %@",
                        exception.name,
                        exception.reason ?: @"native exception"]);
            }
            return NO;
        }
    }
}

- (NSString *)accessibilityTreeJsonForDevice:(NSString *)deviceUdid
                                       error:(NSError **)error
{
    @synchronized (self)
    {
        id device = [self deviceForUdid:deviceUdid error:error];
        if (device == nil)
        {
            return nil;
        }

        static void *accessibilityFramework;
        static NSString *accessibilityFrameworkLoadError;
        static dispatch_once_t accessibilityFrameworkOnce;
        dispatch_once(&accessibilityFrameworkOnce, ^{
            accessibilityFramework = dlopen(
                "/System/Library/PrivateFrameworks/AccessibilityPlatformTranslation.framework/AccessibilityPlatformTranslation",
                RTLD_NOW | RTLD_LOCAL);
            if (accessibilityFramework == NULL)
            {
                const char *loadError = dlerror();
                accessibilityFrameworkLoadError = loadError == NULL
                    ? @"unknown dynamic-loader error"
                    : [NSString stringWithUTF8String:loadError];
            }
        });
        if (accessibilityFramework == NULL)
        {
            if (error != NULL)
            {
                *error = CreateError(
                    28,
                    [NSString stringWithFormat:
                        @"Could not load AccessibilityPlatformTranslation: %@",
                        accessibilityFrameworkLoadError]);
            }
            return nil;
        }

        __block NSString *json = nil;
        __block NSError *captureError = nil;
        dispatch_semaphore_t completion = dispatch_semaphore_create(0);
        dispatch_async(self.accessibilityQueue, ^{
            @try
            {
                AnsightSimulatorAccessibilityDispatcher *dispatcher =
                    [AnsightSimulatorAccessibilityDispatcher shared];
                id translator = [dispatcher translator];
                if (translator == nil)
                {
                    captureError = CreateError(29, @"AXPTranslator is unavailable.");
                    dispatch_semaphore_signal(completion);
                    return;
                }

                NSString *token = [NSUUID UUID].UUIDString;
                [dispatcher registerToken:token device:device];
                @try
                {
                    id translation = [(id<AnsightSimulatorAccessibilityTranslator>)translator
                        frontmostApplicationWithDisplayId:0
                                      bridgeDelegateToken:token];
                    if (translation == nil)
                    {
                        captureError = CreateError(
                            30,
                            @"The Simulator accessibility service returned no frontmost application.");
                    }
                    else
                    {
                        [(id<AnsightSimulatorAccessibilityTranslation>)translation
                            setBridgeDelegateToken:token];
                        id root = [(id<AnsightSimulatorAccessibilityTranslator>)translator
                            macPlatformElementFromTranslation:translation];
                        if (root == nil)
                        {
                            captureError = CreateError(
                                31,
                                @"The Simulator accessibility root could not be translated.");
                        }
                        else
                        {
                            id rootTranslation = [(id<AnsightSimulatorAccessibilityElement>)root translation];
                            if (rootTranslation != nil)
                            {
                                [(id<AnsightSimulatorAccessibilityTranslation>)rootTranslation
                                    setBridgeDelegateToken:token];
                            }

                            NSMutableArray<NSDictionary *> *elements = [NSMutableArray array];
                            NSUInteger visited = 0;
                            [self serializeAccessibilityElement:root
                                                         token:token
                                                         depth:0
                                                       visited:&visited
                                                          into:elements];

                            id deviceType = SendId(device, sel_registerName("deviceType"));
                            NSValue *screenSizeValue = [deviceType valueForKey:@"mainScreenSize"];
                            NSNumber *screenScaleValue = [deviceType valueForKey:@"mainScreenScale"];
                            CGSize screenSize = screenSizeValue.sizeValue;
                            double screenScale = MAX(screenScaleValue.doubleValue, 1.0);
                            double viewportWidth = screenSize.width / screenScale;
                            double viewportHeight = screenSize.height / screenScale;
                            NSDictionary *firstElement = elements.firstObject;
                            double rootWidth = [firstElement[@"width"] doubleValue];
                            double rootHeight = [firstElement[@"height"] doubleValue];
                            if (isfinite(rootWidth) && isfinite(rootHeight)
                                && rootWidth > 0 && rootHeight > 0)
                            {
                                viewportWidth = rootWidth;
                                viewportHeight = rootHeight;
                            }
                            NSDictionary *payload = @{
                                @"format": @"ansight.core-simulator-accessibility.raw.v1",
                                @"viewportWidth": @(viewportWidth),
                                @"viewportHeight": @(viewportHeight),
                                @"rawNodeCount": @(visited),
                                @"elements": elements,
                            };
                            NSData *data = [NSJSONSerialization dataWithJSONObject:payload
                                                                           options:0
                                                                             error:&captureError];
                            if (data != nil)
                            {
                                json = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
                            }
                        }
                    }
                }
                @finally
                {
                    [dispatcher unregisterToken:token];
                }
            }
            @catch (NSException *exception)
            {
                captureError = CreateError(
                    32,
                    [NSString stringWithFormat:@"Simulator accessibility capture failed: %@: %@",
                        exception.name,
                        exception.reason ?: @"native exception"]);
            }
            dispatch_semaphore_signal(completion);
        });

        if (dispatch_semaphore_wait(
                completion,
                dispatch_time(DISPATCH_TIME_NOW, 15 * NSEC_PER_SEC)) != 0)
        {
            if (error != NULL)
            {
                *error = CreateError(33, @"Timed out while reading Simulator accessibility.");
            }
            return nil;
        }
        if (json == nil && error != NULL)
        {
            *error = captureError ?: CreateError(
                34,
                @"Simulator accessibility returned no JSON payload.");
        }
        return json;
    }
}

- (void)serializeAccessibilityElement:(id)element
                                token:(NSString *)token
                                depth:(NSUInteger)depth
                              visited:(NSUInteger *)visited
                                 into:(NSMutableArray<NSDictionary *> *)elements
{
    if (element == nil || depth > 64 || *visited >= 5000)
    {
        return;
    }
    (*visited)++;

    id<AnsightSimulatorAccessibilityElement> accessibilityElement =
        (id<AnsightSimulatorAccessibilityElement>)element;
    id translation = [accessibilityElement translation];
    if (translation != nil)
    {
        [(id<AnsightSimulatorAccessibilityTranslation>)translation setBridgeDelegateToken:token];
    }

    CGRect frame = [accessibilityElement accessibilityFrame];
    NSString *role = [accessibilityElement accessibilityRole];
    NSString *label = [accessibilityElement accessibilityLabel];
    NSString *identifier = [accessibilityElement accessibilityIdentifier];
    id valueObject = [accessibilityElement accessibilityValue];
    NSString *value = nil;
    if ([valueObject isKindOfClass:NSString.class])
    {
        value = valueObject;
    }
    else if ([valueObject isKindOfClass:NSNumber.class])
    {
        value = [valueObject stringValue];
    }
    if ([label isEqualToString:@"Tab Bar"])
    {
        value = AccessibilityDiagnostics(element) ?: value;
    }

    static NSSet<NSString *> *actionableRoles;
    static dispatch_once_t actionableRolesOnce;
    dispatch_once(&actionableRolesOnce, ^{
        actionableRoles = [NSSet setWithArray:@[
            @"AXButton", @"AXTextField", @"AXTextArea", @"AXSecureTextField",
            @"AXSwitch", @"AXSlider", @"AXLink", @"AXCheckBox", @"AXMenuItem",
            @"AXTabButton", @"AXSearchField", @"AXCell", @"AXPopUpButton",
            @"AXSegmentedControl", @"AXToggle", @"AXStepper", @"AXTab",
            @"AXScrollArea", @"AXApplication"
        ]];
    });
    BOOL hasFrame = isfinite(frame.origin.x)
        && isfinite(frame.origin.y)
        && isfinite(frame.size.width)
        && isfinite(frame.size.height)
        && frame.size.width > 0
        && frame.size.height > 0;
    BOOL meaningful = hasFrame
        && (identifier.length > 0
            || label.length > 0
            || value.length > 0
            || [actionableRoles containsObject:role]);
    if (meaningful)
    {
        BOOL enabled = YES;
        @try
        {
            enabled = [accessibilityElement isAccessibilityEnabled];
        }
        @catch (__unused NSException *exception)
        {
        }

        NSMutableDictionary *serialized = [NSMutableDictionary dictionary];
        serialized[@"role"] = role ?: @"";
        if (label.length > 0) serialized[@"label"] = label;
        if (identifier.length > 0) serialized[@"id"] = identifier;
        if (value.length > 0) serialized[@"value"] = value;
        serialized[@"x"] = @(frame.origin.x);
        serialized[@"y"] = @(frame.origin.y);
        serialized[@"width"] = @(frame.size.width);
        serialized[@"height"] = @(frame.size.height);
        serialized[@"enabled"] = @(enabled);
        SEL selectedSelector = NSSelectorFromString(@"accessibilitySelected");
        if ([element respondsToSelector:selectedSelector])
        {
            @try
            {
                serialized[@"selected"] = @(SendBool(element, selectedSelector));
            }
            @catch (__unused NSException *exception)
            {
            }
        }
        serialized[@"depth"] = @(depth);
        [elements addObject:serialized];
    }

    NSArray *children = AccessibilityRelatedElements(element);
    for (id child in children)
    {
        [self serializeAccessibilityElement:child
                                      token:token
                                      depth:depth + 1
                                    visited:visited
                                       into:elements];
    }
}

@end

bool AnsightSimulatorHidCheckCompatibility(
    const char *developerDirectory,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (developerDirectory == NULL)
        {
            CopyError(@"A developer directory is required.", errorBuffer, errorBufferCapacity);
            return false;
        }

        NSError *error = nil;
        NSString *developerDirectoryValue = [NSString stringWithUTF8String:developerDirectory];
        BOOL compatible = CheckSimulatorHidCompatibility(developerDirectoryValue, &error);
        if (!compatible)
        {
            CopyError(error.localizedDescription, errorBuffer, errorBufferCapacity);
        }
        return compatible;
    }
}

AnsightSimulatorHidSessionRef AnsightSimulatorHidSessionCreate(
    const char *developerDirectory,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (developerDirectory == NULL)
        {
            CopyError(@"A developer directory is required.", errorBuffer, errorBufferCapacity);
            return NULL;
        }

        NSError *error = nil;
        NSString *developerDirectoryValue = [NSString stringWithUTF8String:developerDirectory];
        AnsightSimulatorHidSession *session = [[AnsightSimulatorHidSession alloc]
            initWithDeveloperDirectory:developerDirectoryValue
            error:&error];
        if (session == nil)
        {
            CopyError(error.localizedDescription, errorBuffer, errorBufferCapacity);
            return NULL;
        }

        return (AnsightSimulatorHidSessionRef)CFBridgingRetain(session);
    }
}

bool AnsightSimulatorHidSessionSendPointer(
    AnsightSimulatorHidSessionRef session,
    const char *deviceUdid,
    AnsightSimulatorPointerPhase phase,
    double normalizedX,
    double normalizedY,
    bool hasSecondaryContact,
    double secondaryNormalizedX,
    double secondaryNormalizedY,
    int64_t pointerId,
    int64_t timestampMilliseconds,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (session == NULL || deviceUdid == NULL)
        {
            CopyError(@"A valid HID session and simulator UDID are required.", errorBuffer, errorBufferCapacity);
            return false;
        }

        AnsightSimulatorHidSession *sessionValue = (__bridge AnsightSimulatorHidSession *)session;
        NSString *deviceUdidValue = [NSString stringWithUTF8String:deviceUdid];
        NSError *error = nil;
        BOOL success = [sessionValue sendPointerToDevice:deviceUdidValue
                                                   phase:phase
                                             normalizedX:normalizedX
                                             normalizedY:normalizedY
                                     hasSecondaryContact:hasSecondaryContact
                                    secondaryNormalizedX:secondaryNormalizedX
                                    secondaryNormalizedY:secondaryNormalizedY
                                               pointerId:pointerId
                                    timestampMilliseconds:timestampMilliseconds
                                                   error:&error];
        if (!success)
        {
            CopyError(error.localizedDescription, errorBuffer, errorBufferCapacity);
        }
        return success;
    }
}

bool AnsightSimulatorHidSessionGetMainScreenMetrics(
    AnsightSimulatorHidSessionRef session,
    const char *deviceUdid,
    double *pixelWidth,
    double *pixelHeight,
    double *scale,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (session == NULL
            || deviceUdid == NULL
            || pixelWidth == NULL
            || pixelHeight == NULL
            || scale == NULL)
        {
            CopyError(@"A valid HID session, simulator UDID, and metrics outputs are required.", errorBuffer, errorBufferCapacity);
            return false;
        }

        AnsightSimulatorHidSession *sessionValue = (__bridge AnsightSimulatorHidSession *)session;
        NSString *deviceUdidValue = [NSString stringWithUTF8String:deviceUdid];
        NSError *error = nil;
        BOOL success = [sessionValue getMainScreenMetricsForDevice:deviceUdidValue
                                                        pixelWidth:pixelWidth
                                                       pixelHeight:pixelHeight
                                                             scale:scale
                                                             error:&error];
        if (!success)
        {
            CopyError(error.localizedDescription, errorBuffer, errorBufferCapacity);
        }
        return success;
    }
}

char *AnsightSimulatorHidSessionCopyAccessibilityTreeJson(
    AnsightSimulatorHidSessionRef session,
    const char *deviceUdid,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (session == NULL || deviceUdid == NULL)
        {
            CopyError(
                @"A valid HID session and simulator UDID are required.",
                errorBuffer,
                errorBufferCapacity);
            return NULL;
        }

        AnsightSimulatorHidSession *sessionValue = (__bridge AnsightSimulatorHidSession *)session;
        NSString *deviceUdidValue = [NSString stringWithUTF8String:deviceUdid];
        NSError *error = nil;
        NSString *json = [sessionValue accessibilityTreeJsonForDevice:deviceUdidValue error:&error];
        if (json == nil)
        {
            CopyError(error.localizedDescription, errorBuffer, errorBufferCapacity);
            return NULL;
        }

        const char *utf8 = json.UTF8String;
        if (utf8 == NULL)
        {
            CopyError(
                @"Simulator accessibility JSON could not be encoded as UTF-8.",
                errorBuffer,
                errorBufferCapacity);
            return NULL;
        }
        size_t length = strlen(utf8);
        char *owned = malloc(length + 1);
        if (owned == NULL)
        {
            CopyError(
                @"Could not allocate the Simulator accessibility JSON buffer.",
                errorBuffer,
                errorBufferCapacity);
            return NULL;
        }
        memcpy(owned, utf8, length + 1);
        return owned;
    }
}

void AnsightSimulatorHidFreeString(char *value)
{
    free(value);
}

bool AnsightSimulatorHidSessionSendButton(
    AnsightSimulatorHidSessionRef session,
    const char *deviceUdid,
    AnsightSimulatorButton button,
    AnsightSimulatorButtonPhase phase,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (session == NULL || deviceUdid == NULL)
        {
            CopyError(@"A valid HID session and simulator UDID are required.", errorBuffer, errorBufferCapacity);
            return false;
        }

        AnsightSimulatorHidSession *sessionValue = (__bridge AnsightSimulatorHidSession *)session;
        NSString *deviceUdidValue = [NSString stringWithUTF8String:deviceUdid];
        NSError *error = nil;
        BOOL success = [sessionValue sendButtonToDevice:deviceUdidValue
                                                  button:button
                                                   phase:phase
                                                   error:&error];
        if (!success)
        {
            CopyError(error.localizedDescription, errorBuffer, errorBufferCapacity);
        }
        return success;
    }
}

bool AnsightSimulatorHidSessionSendShake(
    AnsightSimulatorHidSessionRef session,
    const char *deviceUdid,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (session == NULL || deviceUdid == NULL)
        {
            CopyError(@"A valid HID session and simulator UDID are required.", errorBuffer, errorBufferCapacity);
            return false;
        }

        AnsightSimulatorHidSession *sessionValue = (__bridge AnsightSimulatorHidSession *)session;
        NSString *deviceUdidValue = [NSString stringWithUTF8String:deviceUdid];
        NSError *error = nil;
        BOOL success = [sessionValue sendShakeToDevice:deviceUdidValue error:&error];
        if (!success)
        {
            CopyError(error.localizedDescription, errorBuffer, errorBufferCapacity);
        }
        return success;
    }
}

bool AnsightSimulatorHidSessionSendKey(
    AnsightSimulatorHidSessionRef session,
    const char *deviceUdid,
    uint32_t usageCode,
    AnsightSimulatorKeyPhase phase,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (session == NULL || deviceUdid == NULL)
        {
            CopyError(@"A valid HID session and simulator UDID are required.", errorBuffer, errorBufferCapacity);
            return false;
        }

        AnsightSimulatorHidSession *sessionValue = (__bridge AnsightSimulatorHidSession *)session;
        NSString *deviceUdidValue = [NSString stringWithUTF8String:deviceUdid];
        NSError *error = nil;
        BOOL success = [sessionValue sendKeyToDevice:deviceUdidValue
                                          usageCode:usageCode
                                               phase:phase
                                               error:&error];
        if (!success)
        {
            CopyError(error.localizedDescription, errorBuffer, errorBufferCapacity);
        }
        return success;
    }
}

void AnsightSimulatorHidSessionDestroy(AnsightSimulatorHidSessionRef session)
{
    if (session != NULL)
    {
        CFBridgingRelease(session);
    }
}
