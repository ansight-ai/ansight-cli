#import "../AnsightSimulatorHid/AnsightSimulatorHid.m"

static void Require(BOOL condition, NSString *message)
{
    if (!condition)
    {
        fprintf(stderr, "FAIL: %s\n", message.UTF8String);
        exit(1);
    }
}

@interface _Xcode27RecordingHid : _Xcode27SimulatorHid
@property(nonatomic, copy) NSString *messageType;
@property(nonatomic, strong) xpc_object_t payload;
@property(nonatomic, assign) NSUInteger sends;
@property(nonatomic, strong) NSError *interruption;
@end

@implementation _Xcode27RecordingHid
- (NSError *)connectionError
{
    return self.interruption;
}

- (BOOL)sendMessageType:(NSString *)messageType payload:(xpc_object_t)payload error:(NSError **)error
{
    (void)error;
    self.messageType = messageType;
    self.payload = payload;
    self.sends++;
    return YES;
}
@end

@interface _Xcode27RecoverySession : AnsightSimulatorHidSession
@property(nonatomic, strong) _Xcode27RecordingHid *replacement;
@property(nonatomic, assign) NSUInteger connectionAttempts;
@end

@implementation _Xcode27RecoverySession
- (_Xcode27SimulatorHid *)createXcode27ClientForUdid:(NSString *)udid error:(NSError **)error
{
    (void)udid;
    (void)error;
    self.connectionAttempts++;
    return self.replacement;
}
@end

static BOOL SendTouch(AnsightSimulatorHidSession *session, AnsightSimulatorPointerPhase phase, NSError **error)
{
    return [session sendPointerToDevice:@"test-device" phase:phase
        normalizedX:0.25 normalizedY:0.75 hasSecondaryContact:NO
        secondaryNormalizedX:0 secondaryNormalizedY:0 pointerId:17
        timestampMilliseconds:0 error:error];
}

static void VerifyRecoveryOnNewGesture(void)
{
    _Xcode27RecordingHid *interrupted = [_Xcode27RecordingHid new];
    interrupted.interruption = [NSError errorWithDomain:@"DTUHIDTest" code:53 userInfo:nil];
    _Xcode27RecoverySession *session = [_Xcode27RecoverySession new];
    session.requiresXcode27Hid = YES;
    session.xcode27ClientsByUdid = [@{@"test-device": interrupted} mutableCopy];

    NSError *error = nil;
    Require(!SendTouch(session, AnsightSimulatorPointerPhaseMove, &error),
        @"An interrupted gesture must not send another move.");
    Require(error == interrupted.interruption, @"Return the interruption to the caller.");
    Require(session.connectionAttempts == 0, @"An interrupted move must not open a connection.");

    Require(!SendTouch(session, AnsightSimulatorPointerPhaseDown, NULL), @"A failed reconnect must fail the press.");
    Require(!SendTouch(session, AnsightSimulatorPointerPhaseUp, NULL), @"Do not send a release after reconnect failed.");
    Require(session.connectionAttempts == 1, @"Only the new press may attempt to reconnect.");

    session.replacement = [_Xcode27RecordingHid new];
    Require(SendTouch(session, AnsightSimulatorPointerPhaseDown, NULL), @"The next press can reconnect.");
    Require(SendTouch(session, AnsightSimulatorPointerPhaseUp, NULL), @"The release uses the replacement connection.");
    Require(session.connectionAttempts == 2, @"Keep the recovered connection for the rest of the gesture.");
    Require(interrupted.sends == 0 && session.replacement.sends == 2,
        @"Send only the new gesture, and send each event once.");
}

int main(void)
{
    @autoreleasepool
    {
        _Xcode27RecordingHid *client = [_Xcode27RecordingHid new];
        AnsightSimulatorHidSession *session = [AnsightSimulatorHidSession new];
        session.requiresXcode27Hid = YES;
        session.xcode27ClientsByUdid = [@{@"test-device": client} mutableCopy];
        NSError *error = nil;
        for (int phase = 0; phase <= 3; phase++)
        {
            Require([session sendPointerToDevice:@"test-device" phase:phase
                normalizedX:0.25 normalizedY:0.75 hasSecondaryContact:NO
                secondaryNormalizedX:0 secondaryNormalizedY:0 pointerId:17
                timestampMilliseconds:0 error:&error], @"Each pointer phase uses DTUHID.");
            Require([client.messageType isEqualToString:@"IndigoDigitizerEvent"], @"Use the digitizer event envelope.");
            Require(xpc_dictionary_get_uint64(client.payload, "eventType") == (uint64_t)(phase == 3 ? 2 : phase),
                @"Cancel releases the contact; other phases preserve start/position/end.");
            Require(xpc_get_type(xpc_dictionary_get_value(client.payload, "eventType")) == XPC_TYPE_UINT64,
                @"The guest's decoder requires unsigned integer phases.");
            xpc_object_t point = xpc_dictionary_get_value(client.payload, "pointOne");
            Require(xpc_dictionary_get_double(point, "x") == 0.25, @"Preserve normalized horizontal position.");
            Require(xpc_dictionary_get_double(point, "y") == 0.75, @"Preserve normalized vertical position.");
            Require(xpc_dictionary_get_value(client.payload, "pointTwo") == NULL, @"Single touch omits the second contact.");
            Require(xpc_dictionary_get_uint64(client.payload, "target") == 0, @"DTUHID uses target 0 for the main display.");
        }

        Require([session sendPointerToDevice:@"test-device" phase:AnsightSimulatorPointerPhaseDown
            normalizedX:0.2 normalizedY:0.3 hasSecondaryContact:YES
            secondaryNormalizedX:0.8 secondaryNormalizedY:0.7 pointerId:17
            timestampMilliseconds:0 error:&error], @"Two-finger input uses DTUHID.");
        xpc_object_t second = xpc_dictionary_get_value(client.payload, "pointTwo");
        Require(xpc_dictionary_get_double(second, "x") == 0.8, @"Preserve the second contact's horizontal position.");
        Require(xpc_dictionary_get_double(second, "y") == 0.7, @"Preserve the second contact's vertical position.");

        const uint64_t usages[] = {0x40, 0x30, 0xe9, 0xea};
        for (int button = 0; button < 4; button++)
        {
            Require([session sendButtonToDevice:@"test-device" button:button
                phase:AnsightSimulatorButtonPhaseDown error:&error], @"Hardware buttons use DTUHID.");
            Require([client.messageType isEqualToString:@"IndigoButtonEvent"], @"Use the hardware button envelope.");
            Require(xpc_dictionary_get_uint64(client.payload, "usagePage") == 12, @"Use the consumer usage page.");
            Require(xpc_dictionary_get_uint64(client.payload, "usageCode") == usages[button], @"Map each button to its HID usage.");
            Require(xpc_dictionary_get_uint64(client.payload, "state") == 1, @"DTUHID press is 1-based.");
        }

        Require([session sendKeyToDevice:@"test-device" usageCode:225
            phase:AnsightSimulatorKeyPhaseUp error:&error], @"Modifiers use the Xcode 27 keyboard channel.");
        Require([client.messageType isEqualToString:@"IndigoKeyboardButtonEvent"], @"Use the keyboard event envelope.");
        Require(xpc_dictionary_get_uint64(client.payload, "usageCode") == 225, @"Preserve modifier HID usage without legacy bit conversion.");
        Require(xpc_dictionary_get_uint64(client.payload, "state") == 2, @"DTUHID release is 2.");

        NSUInteger sends = client.sends;
        Require(![session sendPointerToDevice:@"test-device" phase:99
            normalizedX:0.2 normalizedY:0.3 hasSecondaryContact:NO
            secondaryNormalizedX:0 secondaryNormalizedY:0 pointerId:17
            timestampMilliseconds:0 error:&error], @"Invalid phases fail before sending.");
        Require(client.sends == sends, @"Invalid phases must not enqueue input.");
        VerifyRecoveryOnNewGesture();
        puts("PASS: DTUHID routing and reconnection on a new gesture after an interruption.");
    }
    return 0;
}
