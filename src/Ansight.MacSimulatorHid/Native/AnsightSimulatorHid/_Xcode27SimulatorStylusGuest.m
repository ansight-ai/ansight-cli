// Runs only inside the selected simulator. The virtual touchscreen discovery
// and enumeration pattern is adapted from duoctl (see THIRD-PARTY-NOTICES.md).
#import <Foundation/Foundation.h>
#import <dlfcn.h>
#import <mach/mach_time.h>
#import <math.h>
#import <objc/message.h>
#import <unistd.h>

typedef CFTypeRef AnsightGuestHidEvent;
extern CFTypeRef IOHIDEventSystemClientCreateWithType(CFAllocatorRef, int, CFDictionaryRef);
extern CFArrayRef IOHIDEventSystemClientCopyServices(CFTypeRef);
extern CFTypeRef IOHIDServiceClientCopyProperty(CFTypeRef, CFStringRef);
extern AnsightGuestHidEvent IOHIDEventCreateDigitizerEvent(CFAllocatorRef, uint64_t, uint32_t, uint32_t,
    uint32_t, uint32_t, uint32_t, double, double, double, double, double, Boolean, Boolean, uint32_t);
extern void IOHIDEventAppendEvent(AnsightGuestHidEvent, AnsightGuestHidEvent, uint32_t);
extern void IOHIDEventSetIntegerValue(AnsightGuestHidEvent, uint32_t, CFIndex);
typedef AnsightGuestHidEvent (*AnsightCreateGuestStylus)(CFAllocatorRef, uint64_t, uint32_t, uint32_t,
    uint32_t, uint32_t, double, double, double, double, double, double,
    double, double, uint32_t, uint32_t, uint32_t);

static void Reply(BOOL success, NSString *message)
{
    NSData *data = [NSJSONSerialization dataWithJSONObject:@{
        @"isSuccess": @(success), @"message": message
    } options:0 error:NULL];
    fwrite(data.bytes, 1, data.length, stdout);
    fputc('\n', stdout);
    fflush(stdout);
}

static NSDictionary *MainTouchscreenProperties(void)
{
    CFTypeRef client = IOHIDEventSystemClientCreateWithType(NULL, 1, NULL);
    if (client == NULL)
    {
        return nil;
    }

    NSArray *services = CFBridgingRelease(IOHIDEventSystemClientCopyServices(client));
    NSArray<NSString *> *keys = @[
        @"PrimaryUsagePage", @"PrimaryUsage", @"DeviceUsagePairs", @"Product", @"Transport",
        @"Built-In", @"DeviceTypeHint", @"displayUUID"
    ];
    NSMutableArray<NSDictionary *> *screens = [NSMutableArray array];
    NSDictionary *mainScreen = nil;
    for (id service in services)
    {
        NSMutableDictionary *properties = [NSMutableDictionary dictionary];
        for (NSString *key in keys)
        {
            id value = CFBridgingRelease(IOHIDServiceClientCopyProperty(
                (__bridge CFTypeRef)service, (__bridge CFStringRef)key));
            if (value != nil)
            {
                properties[key] = value;
            }
        }

        NSString *product = properties[@"Product"] ?: @"";
        BOOL isTouchscreen = [properties[@"PrimaryUsagePage"] intValue] == 13
            && [properties[@"PrimaryUsage"] intValue] == 4
            && ![product hasPrefix:@"Ansight Xcode 27"];
        if (isTouchscreen)
        {
            [screens addObject:properties];
            if ([product containsString:@"mainTouchscreen"])
            {
                mainScreen = properties;
            }
        }
    }
    CFRelease(client);
    return mainScreen ?: (screens.count == 1 ? screens.firstObject : nil);
}

@interface _Xcode27SimulatorStylusGuest : NSObject
@property(nonatomic, copy) NSDictionary *properties;
@property(nonatomic, strong) dispatch_semaphore_t enumerated;
@property(nonatomic, strong) id service;
@property(nonatomic, assign) AnsightCreateGuestStylus createStylus;
- (BOOL)activate;
- (BOOL)sendSample:(NSDictionary *)sample;
- (void)cancel;
@end

@implementation _Xcode27SimulatorStylusGuest

- (id)propertyForKey:(NSString *)key forService:(id)service
{
    (void)service;
    return self.properties[key];
}

- (BOOL)setProperty:(id)value forKey:(NSString *)key forService:(id)service
{
    (void)value;
    (void)key;
    (void)service;
    return YES;
}

- (id)copyEventMatching:(NSDictionary *)matching forService:(id)service
{
    (void)matching;
    (void)service;
    return nil;
}

- (BOOL)setOutputEvent:(id)event forService:(id)service
{
    (void)event;
    (void)service;
    return YES;
}

- (void)notification:(NSInteger)type withProperty:(NSDictionary *)property forService:(id)service
{
    (void)property;
    (void)service;
    if (type == 10)
    {
        dispatch_semaphore_signal(self.enumerated);
    }
}

- (BOOL)activate
{
    if (dlopen("/System/Library/PrivateFrameworks/HID.framework/HID", RTLD_NOW | RTLD_LOCAL) == NULL)
    {
        return NO;
    }
    self.createStylus = (AnsightCreateGuestStylus)dlsym(
        RTLD_DEFAULT, "IOHIDEventCreateDigitizerStylusEventWithPolarOrientation");
    NSDictionary *screen = MainTouchscreenProperties();
    Class serviceClass = NSClassFromString(@"HIDVirtualEventService");
    BOOL canActivate = self.createStylus != NULL
        && screen != nil
        && serviceClass != Nil;
    if (!canActivate)
    {
        return NO;
    }

    NSMutableDictionary *properties = [screen mutableCopy];
    properties[@"Product"] = @"Ansight Xcode 27 Pencil";
    self.properties = properties;
    self.enumerated = dispatch_semaphore_create(0);
    self.service = [serviceClass new];
    ((void (*)(id, SEL, id))objc_msgSend)(self.service, sel_registerName("setDelegate:"), self);
    ((void (*)(id, SEL, id))objc_msgSend)(self.service, sel_registerName("setDispatchQueue:"),
        dispatch_queue_create("ai.ansight.xcode27-stylus", DISPATCH_QUEUE_SERIAL));
    ((void (*)(id, SEL))objc_msgSend)(self.service, sel_registerName("activate"));
    return dispatch_semaphore_wait(self.enumerated, dispatch_time(DISPATCH_TIME_NOW, 3 * NSEC_PER_SEC)) == 0;
}

- (BOOL)sendSample:(NSDictionary *)sample
{
    NSArray<NSString *> *keys = @[
        @"phase", @"x", @"y", @"pressure", @"altitude", @"azimuth", @"roll", @"distance", @"pointerId", @"timestamp"
    ];
    for (NSString *key in keys)
    {
        NSNumber *number = sample[key];
        if (![number isKindOfClass:NSNumber.class] || !isfinite(number.doubleValue))
        {
            return NO;
        }
    }

    NSInteger phase = [sample[@"phase"] integerValue];
    double x = [sample[@"x"] doubleValue];
    double y = [sample[@"y"] doubleValue];
    double pressure = [sample[@"pressure"] doubleValue];
    double altitude = [sample[@"altitude"] doubleValue];
    double distance = [sample[@"distance"] doubleValue];
    uint64_t pointerId = [sample[@"pointerId"] unsignedLongLongValue];
    BOOL validPhase = phase >= 0
        && phase <= 6
        && phase == [sample[@"phase"] doubleValue];
    BOOL validPosition = x >= 0
        && x <= 1
        && y >= 0
        && y <= 1;
    BOOL validPressure = pressure >= 0 && pressure <= 1;
    BOOL validAltitude = altitude >= 0 && altitude <= M_PI_2;
    BOOL validDistance = distance >= 0 && distance <= 1;
    BOOL validIdentity = pointerId > 0 && pointerId <= UINT32_MAX;
    BOOL validSample = validPhase
        && validPosition
        && validPressure
        && validAltitude
        && validDistance
        && validIdentity;
    if (!validSample)
    {
        return NO;
    }

    BOOL touching = phase == 0 || phase == 1;
    BOOL inRange = phase != 3 && phase != 6;
    uint32_t mask = phase == 1 || phase == 5 ? 0x44 : 0x67;
    if (phase == 3)
    {
        mask |= 0x80;
    }
    uint64_t timestamp = [sample[@"timestamp"] unsignedLongLongValue];
    if (timestamp == 0)
    {
        timestamp = mach_absolute_time();
    }

    AnsightGuestHidEvent hand = IOHIDEventCreateDigitizerEvent(
        NULL, timestamp, 3, 0, 0, mask, 0, x, y, 0, 0, 0, inRange, touching, 0);
    AnsightGuestHidEvent stylus = self.createStylus(
        NULL, timestamp, 1, (uint32_t)pointerId, mask, 0, x, y, distance, touching ? pressure * 500 : 0,
        0, [sample[@"roll"] doubleValue], M_PI_2 - altitude, 2 * M_PI - [sample[@"azimuth"] doubleValue],
        inRange, false, touching ? 0x00020000 : 0);
    if (hand == NULL || stylus == NULL)
    {
        if (hand != NULL)
        {
            CFRelease(hand);
        }
        if (stylus != NULL)
        {
            CFRelease(stylus);
        }
        return NO;
    }

    IOHIDEventSetIntegerValue(hand, (11 << 16) | 25, 1);
    IOHIDEventSetIntegerValue(stylus, (11 << 16) | 25, 1);
    IOHIDEventAppendEvent(hand, stylus, 0);
    BOOL sent = ((BOOL (*)(id, SEL, id))objc_msgSend)(self.service, sel_registerName("dispatchEvent:"),
        (__bridge id)hand);
    CFRelease(stylus);
    CFRelease(hand);
    return sent;
}

- (void)cancel
{
    if (self.service != nil)
    {
        usleep(100000);
        ((void (*)(id, SEL))objc_msgSend)(self.service, sel_registerName("cancel"));
        self.service = nil;
    }
}

@end

int main(void)
{
    @autoreleasepool
    {
        _Xcode27SimulatorStylusGuest *guest = [_Xcode27SimulatorStylusGuest new];
        if (![guest activate])
        {
            Reply(NO, @"The simulator could not register a Pencil service for its main touchscreen.");
            [guest cancel];
            return 1;
        }
        Reply(YES, @"ready");

        char line[8192];
        while (fgets(line, sizeof(line), stdin) != NULL)
        {
            @autoreleasepool
            {
                if (strchr(line, '\n') == NULL)
                {
                    Reply(NO, @"The Pencil sample exceeded the input limit or was incomplete.");
                    break;
                }
                NSData *data = [NSData dataWithBytes:line length:strlen(line)];
                id sample = [NSJSONSerialization JSONObjectWithData:data options:0 error:NULL];
                BOOL sent = [sample isKindOfClass:NSDictionary.class] && [guest sendSample:sample];
                Reply(sent, sent ? @"accepted" : @"The simulator rejected the Pencil sample.");
                if (!sent)
                {
                    break;
                }
            }
        }
        [guest cancel];
    }
    return 0;
}
